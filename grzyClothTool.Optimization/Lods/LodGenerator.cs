using CodeWalker.GameFiles;

namespace grzyClothTool.Optimization.Lods;

/// <summary>
/// Adds the missing Medium/Low LODs of a .ydd using Blender + Sollumz: the file goes to Blender as CodeWalker XML,
/// Sollumz decimates the High model ("Generate LODs") and exports it back, and only the new LOD models are copied
/// into the original file (see <see cref="LodGrafter"/>).
/// </summary>
public sealed class LodGenerator(LodGenerationOptions options) : IAsyncDisposable
{
    private readonly Lazy<BlenderWorkerPool> _pool = new(() => new BlenderWorkerPool(options));

    public LodGenerationOptions Options => options;

    /// <summary>Blender and Sollumz versions in use, once the first worker has started.</summary>
    public string? Versions => _pool.IsValueCreated ? _pool.Value.Versions : null;

    /// <summary>
    /// Generates the missing LODs of <paramref name="ydd"/> in place. Returns true when the file changed.
    /// In a dry run only <paramref name="changes"/> is filled (without triangle counts) and Blender is not started.
    /// </summary>
    public async Task<bool> AddMissingLodsAsync(YddFile ydd, string sourcePath, List<LodChange> changes, List<string> notes,
        bool dryRun, CancellationToken cancellationToken)
    {
        var missing = LodGrafter.FindMissing(ydd);
        if (missing.Count == 0)
        {
            return false;
        }

        // Physically simulated clothes (.yld next to the .ydd) are bound vertex by vertex to the High model.
        if (File.Exists(Path.ChangeExtension(sourcePath, ".yld")))
        {
            notes.Add("has cloth physics (.yld); LODs not generated");
            return false;
        }

        if (dryRun)
        {
            changes.AddRange(missing.SelectMany(m => m.Levels.Select(level => new LodChange(m.Name, level, m.HighTriangles, null))));
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var work = Path.Combine(Path.GetTempPath(), "grzyOptimizer", "lods", Guid.NewGuid().ToString("N"));
        var inputDir = Path.Combine(work, "in");
        var outputDir = Path.Combine(work, "out");

        try
        {
            // CodeWalker XML layout: foo.ydd.xml + foo/ with the embedded textures as .dds.
            Directory.CreateDirectory(inputDir);
            var inputXml = Path.Combine(inputDir, name + ".ydd.xml");
            await File.WriteAllTextAsync(inputXml, YddXml.GetXml(ydd, Path.Combine(inputDir, name)), cancellationToken);

            var result = await _pool.Value.RunAsync(inputXml, outputDir, cancellationToken);
            if (!result.Ok)
            {
                notes.Add($"LODs not generated: {result.Error}");
                return false;
            }
            if (result.OutputXml == null)
            {
                return false;
            }

            // Textures are not needed from the export (only models are copied), so no dds folder is read.
            var generated = XmlYdd.GetYdd(await File.ReadAllTextAsync(result.OutputXml, cancellationToken));
            var added = LodGrafter.Graft(ydd, missing, generated, notes);
            changes.AddRange(added);
            return added.Count > 0;
        }
        catch (FileNotFoundException ex)
        {
            // Blender not installed: same message for every file, but it is the actionable one.
            notes.Add($"LODs not generated: {ex.Message}");
            return false;
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception)
        {
            // Temp folder; Windows may still hold a handle for a moment.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pool.IsValueCreated)
        {
            await _pool.Value.DisposeAsync();
        }
    }
}
