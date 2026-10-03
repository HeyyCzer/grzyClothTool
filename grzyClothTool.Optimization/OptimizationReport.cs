using System.Text;
using grzyClothTool.Optimization.Lods;

namespace grzyClothTool.Optimization;

public sealed record OptimizationReportOptions
{
    /// <summary>Settings of the run, written at the top of both files in this order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Settings { get; init; } = [];

    public bool DryRun { get; init; }

    /// <summary>The run was stopped before every file was processed; the files only cover what was done.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Drawables whose High LOD has more triangles than this are listed for review.</summary>
    public int HighTriangleLimit { get; init; } = 15000;
}

/// <summary>
/// Text files written after a run of <see cref="FolderOptimizer"/>:
/// the review report (only what someone has to look at by hand) and the session log (everything that was done).
/// </summary>
public static class OptimizationReport
{
    private const string Rule = "================================================================================";

    /// <summary>Failed and unreadable files, heavy drawables, LODs still missing and every warning, grouped by kind.</summary>
    public static string BuildReview(FolderOptimizationSummary summary, OptimizationReportOptions options)
    {
        var sb = new StringBuilder();
        WriteHeader(sb, "grzyOptimizer - manual review report", options);

        var failed = summary.Files.Where(f => f.Outcome == FileOutcome.Failed).ToList();
        var unreadable = summary.Files.Where(f => f.Outcome == FileOutcome.Skipped).ToList();
        var heavy = summary.Files
            .SelectMany(f => f.Drawables.Select(d => (File: f, Drawable: d)))
            .Where(x => x.Drawable.HighTriangles > options.HighTriangleLimit)
            .OrderByDescending(x => x.Drawable.HighTriangles)
            .ToList();
        var missingLods = summary.Files
            .SelectMany(f => f.Drawables.Select(d => (File: f, Drawable: d, Missing: StillMissing(f, d, options.DryRun))))
            .Where(x => x.Missing.Count > 0)
            .ToList();
        // Notes of failed/unreadable files are already shown in their own section.
        var warnings = summary.Files
            .Where(f => f.Notes.Count > 0 && f.Outcome is not (FileOutcome.Failed or FileOutcome.Skipped))
            .ToList();

        int total = failed.Count + unreadable.Count + heavy.Count + missingLods.Count + warnings.Sum(f => f.Notes.Count);
        sb.AppendLine(total == 0 ? "Nothing needs manual review." : $"{total} item(s) to review.");
        sb.AppendLine();

        Section(sb, $"FAILED - original file kept ({failed.Count})", failed, f =>
        {
            sb.AppendLine($"  {f.RelativePath}");
            sb.AppendLine($"      error: {f.Error}");
            foreach (var note in f.Notes)
            {
                sb.AppendLine($"      {note}");
            }
        });

        Section(sb, $"UNREADABLE - encrypted or corrupted, kept as-is ({unreadable.Count})", unreadable, f =>
        {
            sb.AppendLine($"  {f.RelativePath}");
            foreach (var note in f.Notes)
            {
                sb.AppendLine($"      {note}");
            }
        });

        Section(sb, $"HIGH TRIANGLE COUNT - High LOD above {options.HighTriangleLimit} triangles ({heavy.Count})", heavy, x =>
        {
            var d = x.Drawable;
            sb.AppendLine($"  {d.HighTriangles,8} tris  {x.File.RelativePath}{DrawableSuffix(x.File, d)}  (Medium {Tris(d.MediumTriangles)}, Low {Tris(d.LowTriangles)})");
        });

        var missingTitle = options.DryRun ? "LODS STILL MISSING after the planned changes" : "LODS STILL MISSING";
        Section(sb, $"{missingTitle} ({missingLods.Count})", missingLods, x =>
        {
            sb.AppendLine($"  {x.File.RelativePath}{DrawableSuffix(x.File, x.Drawable)}: no {string.Join("/", x.Missing)} LOD (High {x.Drawable.HighTriangles} tris)");
        });

        Section(sb, $"WARNINGS ({warnings.Sum(f => f.Notes.Count)})", warnings, f =>
        {
            sb.AppendLine($"  {f.RelativePath}");
            foreach (var note in f.Notes)
            {
                sb.AppendLine($"      {note}");
            }
        });

        return sb.ToString();
    }

    /// <summary>Settings, then every file with each change made to it, then the totals.</summary>
    public static string BuildLog(FolderOptimizationSummary summary, OptimizationReportOptions options)
    {
        var sb = new StringBuilder();
        WriteHeader(sb, "grzyOptimizer - session log", options);

        sb.AppendLine("FILES");
        foreach (var file in summary.Files)
        {
            foreach (var line in DescribeFile(file))
            {
                sb.AppendLine(line);
            }
        }
        sb.AppendLine();

        sb.AppendLine("SUMMARY");
        sb.AppendLine($"  Files:              {summary.Files.Count}");
        sb.AppendLine($"  Optimized:          {summary.Count(FileOutcome.Optimized)} file(s), {summary.TexturesOptimized} texture(s)");
        sb.AppendLine($"  LODs generated:     {summary.LodsGenerated}{(options.DryRun ? " (planned)" : "")}");
        sb.AppendLine($"  Already fine:       {summary.Count(FileOutcome.Unchanged)} texture file(s)");
        sb.AppendLine($"  Other files copied: {summary.Count(FileOutcome.Copied)}");
        sb.AppendLine($"  Unreadable (kept):  {summary.Count(FileOutcome.Skipped)}");
        sb.AppendLine($"  Failed (kept):      {summary.Count(FileOutcome.Failed)}");
        if (!options.DryRun)
        {
            sb.AppendLine($"  Disk size:          {FormatBytes(summary.SizeBefore)} -> {FormatBytes(summary.SizeAfter)}");
        }
        sb.AppendLine($"  Texture memory:     {FormatBytes(summary.TextureMemoryBefore)} -> {FormatBytes(summary.TextureMemoryAfter)} (optimized textures, estimated)");
        if (summary.LodVersions != null)
        {
            sb.AppendLine($"  LOD tools:          {summary.LodVersions}");
        }
        sb.AppendLine($"  Time:               {summary.Elapsed:hh\\:mm\\:ss}");
        return sb.ToString();
    }

    /// <summary>Log lines of one file: outcome, then every texture change, LOD, drawable, note and error.</summary>
    public static IEnumerable<string> DescribeFile(FileResult file)
    {
        var size = file.SizeAfter != file.SizeBefore
            ? $"  {FormatBytes(file.SizeBefore)} -> {FormatBytes(file.SizeAfter)}"
            : $"  {FormatBytes(file.SizeBefore)}";
        yield return $"  [{file.Outcome.ToString().ToUpperInvariant()}] {file.RelativePath}{size}";

        foreach (var change in file.Changes)
        {
            yield return $"      texture {change.Name} [{change.Kind}] {Describe(change.Before)} -> {Describe(change.After)}  ({string.Join(", ", change.Reasons)})";
        }
        foreach (var drawable in file.Drawables)
        {
            yield return $"      drawable {drawable.Name}: High {drawable.HighTriangles}, Medium {drawable.MediumTriangles}, Low {drawable.LowTriangles}, Very low {drawable.VeryLowTriangles} tris";
        }
        foreach (var lod in file.Lods)
        {
            var after = lod.Triangles is { } triangles ? $"{triangles} tris" : "to generate";
            yield return $"      LOD {lod.Drawable} [{lod.Level}] high {lod.HighTriangles} tris -> {after}";
        }
        foreach (var note in file.Notes)
        {
            yield return $"      note: {note}";
        }
        if (file.Error != null)
        {
            yield return $"      error: {file.Error}";
        }
    }

    /// <summary>LOD levels the drawable lacks once the run is over (generated or, in a dry run, planned LODs count as present).</summary>
    private static List<LodLevel> StillMissing(FileResult file, DrawableStats drawable, bool dryRun)
    {
        var missing = new List<LodLevel>();
        if (drawable.HighTriangles == 0)
        {
            return missing;
        }

        // A failed file was kept as it was, so LODs generated for it were not written.
        bool Added(LodLevel level) => file.Outcome != FileOutcome.Failed && file.Lods.Any(l =>
            l.Drawable == drawable.Name && l.Level == level && (dryRun || l.Triangles != null));

        if (drawable.MediumTriangles == 0 && !Added(LodLevel.Medium)) missing.Add(LodLevel.Medium);
        if (drawable.LowTriangles == 0 && !Added(LodLevel.Low)) missing.Add(LodLevel.Low);
        return missing;
    }

    private static void WriteHeader(StringBuilder sb, string title, OptimizationReportOptions options)
    {
        sb.AppendLine(Rule);
        sb.AppendLine(title);
        sb.AppendLine(Rule);
        int width = options.Settings.Count == 0 ? 0 : options.Settings.Max(s => s.Key.Length) + 1;
        foreach (var (key, value) in options.Settings)
        {
            sb.AppendLine($"{(key + ":").PadRight(width + 1)} {value}");
        }
        if (options.DryRun)
        {
            sb.AppendLine("DRY RUN - nothing was written; the changes below are what would be done.");
        }
        if (options.Cancelled)
        {
            sb.AppendLine("CANCELLED - only the files processed before the cancellation are included.");
        }
        sb.AppendLine(Rule);
        sb.AppendLine();
    }

    private static void Section<T>(StringBuilder sb, string title, IReadOnlyList<T> items, Action<T> writeItem)
    {
        if (items.Count == 0)
        {
            return;
        }
        sb.AppendLine(title);
        foreach (var item in items)
        {
            writeItem(item);
        }
        sb.AppendLine();
    }

    /// <summary>Clothing .ydd files usually hold a single drawable named like the file; only name it when that tells something.</summary>
    private static string DrawableSuffix(FileResult file, DrawableStats drawable) =>
        file.Drawables.Count > 1 ? $" [{drawable.Name}]" : "";

    private static string Tris(int triangles) => triangles == 0 ? "missing" : triangles.ToString();

    public static string Describe(TextureInfo info) =>
        $"{info.Width}x{info.Height} {info.Compression.Replace("D3DFMT_", "")} ({info.MipMapCount} mips)";

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{bytes / 1024.0:0.#} KB"
    };
}
