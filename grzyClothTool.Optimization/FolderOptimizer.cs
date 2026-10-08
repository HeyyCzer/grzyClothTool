using CodeWalker.GameFiles;
using System.Text.RegularExpressions;
using grzyClothTool.Optimization.Lods;

namespace grzyClothTool.Optimization;

public sealed record FolderOptimizerOptions
{
    public required string InputFolder { get; init; }

    /// <summary>Where the optimized copy of the folder goes. Null optimizes the files in place.</summary>
    public string? OutputFolder { get; init; }

    public int DiffuseLimit { get; init; } = 1024;
    public int NormalLimit { get; init; } = 1024;
    public int SpecularLimit { get; init; } = 1024;

    /// <summary>Only report what would change; nothing is written.</summary>
    public bool DryRun { get; init; }

    public int MaxParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Generate missing Medium/Low LODs of .ydd files with Blender + Sollumz. Null leaves models untouched.</summary>
    public LodGenerationOptions? Lods { get; init; }

    /// <summary>
    /// Copy hair unchanged: no texture optimization, no LODs. Hair is a file named like hair
    /// (<see cref="FolderOptimizer.IsHairFile"/>), a .ydd holding a hair model in any slot
    /// (<see cref="FolderOptimizer.IsHairModel"/>) and the .ytd files of that model.
    /// Optimized hair packs crashed FiveM clients in the NVIDIA driver while switching hairstyles (barbershop).
    /// </summary>
    public bool SkipHair { get; init; } = true;

    public int GetLimit(TextureKind kind) => kind switch
    {
        TextureKind.Normal => NormalLimit,
        TextureKind.Specular => SpecularLimit,
        _ => DiffuseLimit
    };
}

public enum FileOutcome
{
    /// <summary>At least one texture was (or, in a dry run, would be) optimized, or LODs were added.</summary>
    Optimized,
    /// <summary>Texture file without anything to optimize.</summary>
    Unchanged,
    /// <summary>Not a texture file; copied as-is to the output folder.</summary>
    Copied,
    /// <summary>Texture file that could not be read (encrypted/corrupted); kept as-is.</summary>
    Skipped,
    /// <summary>Texture file left out by an option (e.g. <see cref="FolderOptimizerOptions.SkipHair"/>); copied as-is.</summary>
    Excluded,
    /// <summary>Optimizing failed; the original file was kept.</summary>
    Failed
}

public sealed record TextureChange(string Name, TextureKind Kind, TextureInfo Before, TextureInfo After, IReadOnlyList<string> Reasons);

public sealed record FileResult(
    string RelativePath,
    FileOutcome Outcome,
    IReadOnlyList<TextureChange> Changes,
    IReadOnlyList<string> Notes,
    long SizeBefore,
    long SizeAfter,
    string? Error = null,
    IReadOnlyList<LodChange>? LodChanges = null,
    IReadOnlyList<DrawableStats>? DrawableStats = null)
{
    public IReadOnlyList<LodChange> Lods => LodChanges ?? [];

    /// <summary>Triangle counts of the drawables of a .ydd, as read before any LOD was added. Empty for other files.</summary>
    public IReadOnlyList<DrawableStats> Drawables => DrawableStats ?? [];
}

public sealed record FolderOptimizationSummary(IReadOnlyList<FileResult> Files, TimeSpan Elapsed)
{
    /// <summary>Blender and Sollumz versions used for the LODs, when Blender was started.</summary>
    public string? LodVersions { get; init; }

    public int Count(FileOutcome outcome) => Files.Count(f => f.Outcome == outcome);
    public int TexturesOptimized => Files.Sum(f => f.Changes.Count);
    public int LodsGenerated => Files.Sum(f => f.Lods.Count);
    public long SizeBefore => Files.Sum(f => f.SizeBefore);
    public long SizeAfter => Files.Sum(f => f.SizeAfter);
    public long TextureMemoryBefore => Files.SelectMany(f => f.Changes).Sum(c => TextureRules.EstimateSizeBytes(c.Before));
    public long TextureMemoryAfter => Files.SelectMany(f => f.Changes).Sum(c => TextureRules.EstimateSizeBytes(c.After));
}

/// <summary>
/// Optimizes every .ytd and every texture embedded in .ydd files of a folder (recursively), applying the
/// same rules as grzyClothTool: nearest power of two within the resolution limit, DXT5 for uncompressed
/// textures and a full mip chain. Works on any resource layout; no grzyClothTool project is needed.
/// With <see cref="FolderOptimizerOptions.Lods"/> set, .ydd files missing Medium/Low LODs also get them generated.
/// </summary>
public sealed class FolderOptimizer(FolderOptimizerOptions options)
{
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    private readonly string _input = Path.GetFullPath(options.InputFolder);
    private readonly string? _output = options.OutputFolder == null ? null : Path.GetFullPath(options.OutputFolder);

    /// <summary>
    /// Per .ydd path: completes with whether it is hair as soon as the file is loaded and classified (long before
    /// its textures/LODs are done), so the .ytd files of the same drawable wait only for that, not for every .ydd.
    /// </summary>
    private readonly Dictionary<string, TaskCompletionSource<bool>> _hairCheckByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="DrawableKey"/> → hair checks of the .ydd files with that key (u/r variants share one).</summary>
    private readonly Dictionary<string, List<Task<bool>>> _hairChecksByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ped hair shaders; GTA's own hair uses ped_hair_cutout_alpha and ped_hair_spiked.</summary>
    private static readonly HashSet<uint> HairShaders =
    [
        .. new[]
        {
            "ped_hair_cutout_alpha", "ped_hair_cutout_alpha_cloth", "ped_hair_cutout_alpha_mask",
            "ped_hair_spiked", "ped_hair_spiked_enveff", "ped_hair_spiked_mask", "ped_hair_spiked_noalpha"
        }.Select(JenkHash.GenHash)
    ];

    // "mp_f_freemode_01^berd_002_u" (model) and "mp_f_freemode_01^berd_diff_002_a_uni" (its textures);
    // props: "p_head_000" and "p_head_diff_000_a".
    private static readonly Regex ModelName = new(@"^(?<prefix>.*?)(?<component>[a-z]+)_(?<id>\d{3})(_[a-z])?$", RegexOptions.IgnoreCase);
    private static readonly Regex TextureName = new(@"^(?<prefix>.*?)(?<component>[a-z]+)_diff_(?<id>\d{3})(_|$)", RegexOptions.IgnoreCase);

    public async Task<FolderOptimizationSummary> RunAsync(IProgress<FileResult>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_input))
        {
            throw new DirectoryNotFoundException($"Input folder not found: {_input}");
        }

        if (_output != null && string.Equals(_output.TrimEnd('\\', '/'), _input.TrimEnd('\\', '/'), PathComparison))
        {
            throw new ArgumentException("Output folder must be different from the input folder (use in-place mode instead).");
        }

        var started = DateTime.UtcNow;
        var files = Directory.EnumerateFiles(_input, "*", SearchOption.AllDirectories)
            .Where(path => _output == null || !IsInside(path, _output)) // output inside input: don't re-read our own results
            .ToList();

        var results = new List<FileResult>(files.Count);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
            CancellationToken = cancellationToken
        };

        await using var lods = options.Lods == null ? null : new LodGenerator(options.Lods);

        // A .ydd found to be hair by its content (SkipHair) excludes the .ytd files of the same drawable. Those
        // wait only for that classification (HairTexturesAsync), not for whole phases: a barrier between all
        // .ydd and all .ytd files kept texture encoding from overlapping with Blender LOD generation.
        // .ytd files are queued last, so every .ydd they can wait for has already started (no deadlock).
        if (options.SkipHair)
        {
            foreach (var path in files.Where(p => Path.GetExtension(p).Equals(".ydd", PathComparison)))
            {
                var check = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _hairCheckByPath[path] = check;
                if (DrawableKey(path) is { } key)
                {
                    (_hairChecksByKey.TryGetValue(key, out var checks) ? checks : _hairChecksByKey[key] = []).Add(check.Task);
                }
            }
        }

        await Parallel.ForEachAsync(files.OrderBy(IsYtd), parallelOptions, async (path, token) =>
        {
            FileResult result;
            try
            {
                result = await ProcessFileAsync(path, lods, token);
            }
            finally
            {
                ReportHairCheck(path, false); // no-op once classified; unblocks .ytd waiters on early exits
            }
            lock (results)
            {
                results.Add(result);
            }
            progress?.Report(result);
        });

        results.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, PathComparison));
        return new FolderOptimizationSummary(results, DateTime.UtcNow - started) { LodVersions = lods?.Versions };
    }

    private async Task<FileResult> ProcessFileAsync(string path, LodGenerator? lods, CancellationToken cancellationToken)
    {
        var relative = Path.GetRelativePath(_input, path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var original = File.ReadAllBytes(path);

        if (extension != ".ytd" && extension != ".ydd")
        {
            CopyToOutput(path, relative);
            return new FileResult(relative, FileOutcome.Copied, [], [], original.Length, original.Length);
        }

        if (options.SkipHair && IsHairFile(path))
        {
            CopyToOutput(path, relative);
            return new FileResult(relative, FileOutcome.Excluded, [], [], original.Length, original.Length);
        }

        if (options.SkipHair && extension == ".ytd" && await HairTexturesAsync(path, cancellationToken))
        {
            CopyToOutput(path, relative);
            return new FileResult(relative, FileOutcome.Excluded, [], ["hair textures; kept as-is"], original.Length, original.Length);
        }

        GameFile loaded;
        try
        {
            loaded = extension == ".ytd" ? LoadYtd(original) : LoadYdd(original);
        }
        catch (Exception ex)
        {
            // Encrypted or corrupted resources: keep them untouched.
            CopyToOutput(path, relative);
            return new FileResult(relative, FileOutcome.Skipped, [], [$"could not be read ({ex.Message}); kept as-is"], original.Length, original.Length);
        }

        bool isHairModel = options.SkipHair && loaded is YddFile hairCandidate && IsHairModel(hairCandidate);
        ReportHairCheck(path, isHairModel);
        if (isHairModel)
        {
            CopyToOutput(path, relative);
            return new FileResult(relative, FileOutcome.Excluded, [], ["hair model; kept as-is"],
                original.Length, original.Length);
        }

        var drawables = loaded is YddFile yddFile ? LodGrafter.Describe(yddFile) : null;
        var changes = new List<TextureChange>();
        var lodChanges = new List<LodChange>();
        var notes = new List<string>();
        byte[]? optimized;

        try
        {
            optimized = loaded is YtdFile ytd
                ? OptimizeYtd(ytd, changes, notes)
                : await OptimizeYddAsync((YddFile)loaded, path, lods, changes, lodChanges, notes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CopyToOutput(path, relative);
            return new FileResult(relative, FileOutcome.Failed, changes, notes, original.Length, original.Length, ex.Message, lodChanges, drawables);
        }

        if (changes.Count == 0 && lodChanges.Count == 0)
        {
            CopyToOutput(path, relative);
            return new FileResult(relative, FileOutcome.Unchanged, [], notes, original.Length, original.Length, null, null, drawables);
        }

        if (options.DryRun || optimized == null)
        {
            // optimized is also null when the planned LODs could not be generated; keep the original then.
            if (!options.DryRun)
            {
                CopyToOutput(path, relative);
            }
            return new FileResult(relative, FileOutcome.Optimized, changes, notes, original.Length, original.Length, null, lodChanges, drawables);
        }

        WriteResult(path, relative, optimized);
        return new FileResult(relative, FileOutcome.Optimized, changes, notes, original.Length, optimized.Length, null, lodChanges, drawables);
    }

    private void ReportHairCheck(string path, bool isHair)
    {
        if (_hairCheckByPath.TryGetValue(path, out var check))
        {
            check.TrySetResult(isHair);
        }
    }

    /// <summary>Whether a .ydd of the same drawable as this .ytd was classified as hair; waits for that check.</summary>
    private async Task<bool> HairTexturesAsync(string ytdPath, CancellationToken cancellationToken)
    {
        if (DrawableKey(ytdPath) is not { } key || !_hairChecksByKey.TryGetValue(key, out var checks))
        {
            return false;
        }

        var results = await Task.WhenAll(checks).WaitAsync(cancellationToken);
        return results.Any(isHair => isHair);
    }

    private static YtdFile LoadYtd(byte[] data)
    {
        var ytd = new YtdFile();
        ytd.Load(data);
        return ytd;
    }

    private static YddFile LoadYdd(byte[] data)
    {
        var ydd = new YddFile();
        ydd.Load(data);
        return ydd;
    }

    /// <summary>Returns the new file bytes, or null when nothing changed or in a dry run.</summary>
    private byte[]? OptimizeYtd(YtdFile ytd, List<TextureChange> changes, List<string> notes)
    {
        var textures = ytd.TextureDict?.Textures?.data_items;
        if (textures == null || textures.Length == 0)
        {
            return null;
        }

        var replacements = OptimizeTextures(textures, _ => null, changes, notes);
        if (replacements.Count == 0 || options.DryRun)
        {
            return null;
        }

        ytd.TextureDict!.BuildFromTextureList(textures.Select(t => replacements.GetValueOrDefault(t.Name, t)).ToList());
        return ytd.Save();
    }

    private async Task<byte[]?> OptimizeYddAsync(YddFile ydd, string path, LodGenerator? lods, List<TextureChange> changes,
        List<LodChange> lodChanges, List<string> notes, CancellationToken cancellationToken)
    {
        bool texturesReplaced = OptimizeYddTextures(ydd, changes, notes);
        bool lodsAdded = lods != null && await lods.AddMissingLodsAsync(ydd, path, lodChanges, notes, options.DryRun, cancellationToken);
        if (!texturesReplaced && !lodsAdded)
        {
            return null;
        }

        var saved = ydd.Save();
        if (lodsAdded)
        {
            ValidateSavedLods(saved, lodChanges);
        }
        return saved;
    }

    /// <summary>
    /// Reads the saved file back, as the game would, and checks the LODs that were added. Throws when one is broken,
    /// so the file fails and the original is kept instead of shipping a model that can crash the game.
    /// </summary>
    private static void ValidateSavedLods(byte[] saved, List<LodChange> lodChanges)
    {
        var added = lodChanges.Select(c => (c.Drawable, Level: c.Level.ToString())).ToHashSet();
        var errors = LodValidator.Validate(LoadYdd(saved), added)
            .Where(i => i.Severity == LodIssueSeverity.Error && added.Contains((i.Drawable, i.Level)))
            .ToList();
        if (errors.Count > 0)
        {
            throw new InvalidDataException("generated LODs failed validation after saving: "
                + string.Join("; ", errors.Take(3)) + (errors.Count > 3 ? $" (+{errors.Count - 3} more)" : ""));
        }
    }

    /// <summary>Returns true when at least one embedded texture was replaced.</summary>
    private bool OptimizeYddTextures(YddFile ydd, List<TextureChange> changes, List<string> notes)
    {
        bool anyReplaced = false;
        foreach (var drawable in ydd.Drawables ?? [])
        {
            var shaderGroup = drawable?.ShaderGroup;
            var textures = shaderGroup?.TextureDictionary?.Textures?.data_items;
            if (shaderGroup == null || textures == null || textures.Length == 0)
            {
                continue;
            }

            var kindsFromShaders = ClassifyByShaders(shaderGroup);
            var replacements = OptimizeTextures(textures, name => kindsFromShaders.GetValueOrDefault(name), changes, notes);
            if (replacements.Count == 0 || options.DryRun)
            {
                continue;
            }

            shaderGroup.TextureDictionary!.BuildFromTextureList(textures.Select(t => replacements.GetValueOrDefault(t.Name, t)).ToList());

            // Shaders reference embedded textures directly; point them to the new objects (matched by name,
            // like grzyClothTool's build does) so the old texture data is not written a second time.
            foreach (var shader in shaderGroup.Shaders?.data_items ?? [])
            {
                foreach (var parameter in shader?.ParametersList?.Parameters ?? [])
                {
                    if (parameter.Data is Texture texture && texture.Name != null && replacements.TryGetValue(texture.Name, out var replacement))
                    {
                        parameter.Data = replacement;
                    }
                }
            }

            anyReplaced = true;
        }

        return anyReplaced;
    }

    /// <summary>Optimizes the textures that need it; returns new textures by (case-insensitive) name.</summary>
    private Dictionary<string, Texture> OptimizeTextures(Texture[] textures, Func<string, TextureKind?> kindFromShaders,
        List<TextureChange> changes, List<string> notes)
    {
        var replacements = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);

        foreach (var texture in textures)
        {
            if (texture?.Name == null)
            {
                continue;
            }

            var kind = kindFromShaders(texture.Name) ?? ClassifyByName(texture.Name);
            var limit = options.GetLimit(kind);
            var before = TextureCodec.Describe(texture);
            bool truncated = TextureCodec.IsTruncated(texture);
            var target = truncated ? TextureRules.ComputeRepairTarget(before, limit) : TextureRules.ComputeTarget(before, limit);

            if (target == null)
            {
                if (!TextureRules.CanAutoOptimize(before.Compression) && Math.Max(before.Width, before.Height) > limit)
                {
                    notes.Add($"{texture.Name} ({before.Width}x{before.Height} {before.Compression}) exceeds {limit}px but its format cannot be re-encoded");
                }
                continue;
            }

            var after = target;
            if (!options.DryRun)
            {
                var optimized = TextureCodec.Optimize(texture, target);
                replacements[texture.Name] = optimized;
                after = TextureCodec.Describe(optimized); // AUTO resolves to DXT1/DXT5 only once pixels are known
            }
            else
            {
                replacements[texture.Name] = texture;
            }

            var reasons = TextureRules.DescribeChanges(before, after);
            if (truncated)
            {
                reasons.Insert(0, "Repair truncated data");
            }
            if (target.Compression == TextureRules.AutoCompression && after.Compression == "D3DFMT_DXT1")
            {
                reasons.Add("Opaque -> DXT1");
            }
            changes.Add(new TextureChange(texture.Name, kind, before, after, reasons));
        }

        return replacements;
    }

    /// <summary>Reads which sampler (diffuse/normal/specular) each embedded texture is bound to.</summary>
    private static Dictionary<string, TextureKind?> ClassifyByShaders(ShaderGroup shaderGroup)
    {
        var kinds = new Dictionary<string, TextureKind?>(StringComparer.OrdinalIgnoreCase);

        foreach (var shader in shaderGroup.Shaders?.data_items ?? [])
        {
            var parameters = shader?.ParametersList?.Parameters;
            var hashes = shader?.ParametersList?.Hashes;
            if (parameters == null || hashes == null)
            {
                continue;
            }

            for (int i = 0; i < parameters.Length && i < hashes.Length; i++)
            {
                var name = parameters[i].Data switch
                {
                    Texture texture => texture.Name,
                    TextureBase textureBase => textureBase.Name,
                    _ => null
                };

                TextureKind? kind = (uint)hashes[i] switch
                {
                    (uint)ShaderParamNames.BumpSampler => TextureKind.Normal,
                    (uint)ShaderParamNames.SpecSampler => TextureKind.Specular,
                    (uint)ShaderParamNames.DiffuseSampler => TextureKind.Diffuse,
                    _ => null
                };

                if (name != null && kind != null)
                {
                    kinds.TryAdd(name, kind);
                }
            }
        }

        return kinds;
    }

    /// <summary>Fallback for textures no shader references (and all .ytd textures): GTA naming conventions.</summary>
    public static TextureKind ClassifyByName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith("_n") || lower.Contains("_normal") || lower.Contains("_bump"))
        {
            return TextureKind.Normal;
        }
        if (lower.EndsWith("_s") || lower.Contains("_spec"))
        {
            return TextureKind.Specular;
        }
        return TextureKind.Diffuse;
    }

    private void CopyToOutput(string source, string relative)
    {
        if (_output == null || options.DryRun)
        {
            return;
        }

        var destination = Path.Combine(_output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private void WriteResult(string source, string relative, byte[] content)
    {
        var destination = _output == null ? source : Path.Combine(_output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Write next to the destination and swap, so an interrupted run never leaves a half-written file.
        var temp = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, content);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static bool IsInside(string path, string folder)
    {
        var normalizedFolder = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedFolder, PathComparison);
    }

    /// <summary>
    /// Hair component files ("mp_f_freemode_01^hair_001_u.ydd", "..^hair_diff_001_a_uni.ytd") and hair overlay
    /// textures ("mp_fm_body_hair_001.ytd"). Only the part after '^' is checked for components, because DLC
    /// collection names like "mp_f_gunrunning_hair_01^jbib_000_u.ydd" contain "hair" too.
    /// </summary>
    public static bool IsHairFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        int caret = name.LastIndexOf('^');
        return caret >= 0
            ? name[(caret + 1)..].StartsWith("hair_", PathComparison)
            : name.Contains("hair", PathComparison);
    }

    /// <summary>
    /// A hair model in any slot: packs often reuse hair as berd/hats/etc. ("..^berd_002_u.ydd" holding the drawable
    /// "mp_f_freemode_01^hair_005_u"). Detected by a ped_hair shader or a drawable named like a hair component.
    /// </summary>
    public static bool IsHairModel(YddFile ydd)
    {
        foreach (var drawable in ydd.Drawables ?? [])
        {
            // "mp_f_freemode_01^hair_005_u", or just "hair_003_u" when the creator didn't keep the prefix.
            var name = drawable?.Name ?? "";
            if (name[(name.LastIndexOf('^') + 1)..].StartsWith("hair_", PathComparison))
            {
                return true;
            }
            if (drawable?.ShaderGroup?.Shaders?.data_items?.Any(s => s != null && HairShaders.Contains(s.Name.Hash)) == true)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Folder + collection prefix + component + drawable id, shared by a model and its texture dictionaries
    /// ("x^berd_002_u.ydd" and "x^berd_diff_002_a_uni.ytd"). Null for names outside that convention.
    /// </summary>
    public static string? DrawableKey(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var match = IsYtd(path) ? TextureName.Match(name) : ModelName.Match(name);
        return match.Success
            ? $"{Path.GetDirectoryName(Path.GetFullPath(path))}|{match.Groups["prefix"].Value}{match.Groups["component"].Value}|{match.Groups["id"].Value}"
            : null;
    }

    private static bool IsYtd(string path) => Path.GetExtension(path).Equals(".ytd", PathComparison);
}
