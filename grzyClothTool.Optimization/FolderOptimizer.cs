using CodeWalker.GameFiles;
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
    IReadOnlyList<LodChange>? LodChanges = null)
{
    public IReadOnlyList<LodChange> Lods => LodChanges ?? [];
}

public sealed record FolderOptimizationSummary(IReadOnlyList<FileResult> Files, TimeSpan Elapsed)
{
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

        await Parallel.ForEachAsync(files, parallelOptions, async (path, token) =>
        {
            var result = await ProcessFileAsync(path, lods, token);
            lock (results)
            {
                results.Add(result);
            }
            progress?.Report(result);
        });

        results.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, PathComparison));
        return new FolderOptimizationSummary(results, DateTime.UtcNow - started);
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
            return new FileResult(relative, FileOutcome.Failed, changes, notes, original.Length, original.Length, ex.Message, lodChanges);
        }

        if (changes.Count == 0 && lodChanges.Count == 0)
        {
            CopyToOutput(path, relative);
            return new FileResult(relative, FileOutcome.Unchanged, [], notes, original.Length, original.Length);
        }

        if (options.DryRun || optimized == null)
        {
            // optimized is also null when the planned LODs could not be generated; keep the original then.
            if (!options.DryRun)
            {
                CopyToOutput(path, relative);
            }
            return new FileResult(relative, FileOutcome.Optimized, changes, notes, original.Length, original.Length, null, lodChanges);
        }

        WriteResult(path, relative, optimized);
        return new FileResult(relative, FileOutcome.Optimized, changes, notes, original.Length, optimized.Length, null, lodChanges);
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
        return texturesReplaced || lodsAdded ? ydd.Save() : null;
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
            var target = TextureRules.ComputeTarget(before, limit);

            if (target == null)
            {
                if (!TextureRules.CanAutoOptimize(before.Compression) && Math.Max(before.Width, before.Height) > limit)
                {
                    notes.Add($"{texture.Name} ({before.Width}x{before.Height} {before.Compression}) exceeds {limit}px but its format cannot be re-encoded");
                }
                continue;
            }

            changes.Add(new TextureChange(texture.Name, kind, before, target, TextureRules.DescribeChanges(before, target)));
            if (!options.DryRun)
            {
                replacements[texture.Name] = TextureCodec.Optimize(texture, target);
            }
            else
            {
                replacements[texture.Name] = texture;
            }
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
}
