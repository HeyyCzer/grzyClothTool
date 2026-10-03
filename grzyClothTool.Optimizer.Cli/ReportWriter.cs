using grzyClothTool.Optimization;

namespace grzyClothTool.Optimizer.Cli;

/// <summary>
/// Saves the review report and the session log of a run to two places: a timestamped copy in <c>logs/</c> next to
/// the exe (history of every run) and <c>grzyOptimizer_*.txt</c> at the root of the output folder (or of the
/// optimized folder itself in place), so they travel with the result. Dry runs write nothing to the output.
/// </summary>
internal static class ReportWriter
{
    public const string ReportFileName = "grzyOptimizer_report.txt";
    public const string LogFileName = "grzyOptimizer_log.txt";

    public static string LogsFolder =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "logs");

    /// <returns>The files written; <paramref name="errors"/> gets the destinations that could not be written.</returns>
    public static List<string> Write(FolderOptimizationSummary summary, OptimizationReportOptions reportOptions,
        FolderOptimizerOptions run, DateTime started, out List<string> errors)
    {
        var report = OptimizationReport.BuildReview(summary, reportOptions);
        var log = OptimizationReport.BuildLog(summary, reportOptions);
        var written = new List<string>();
        errors = [];

        var prefix = $"{started:yyyy-MM-dd_HH-mm-ss}_{Path.GetFileName(run.InputFolder.TrimEnd('\\', '/'))}";
        TryWrite(LogsFolder, $"{prefix}_report.txt", report, written, errors);
        TryWrite(LogsFolder, $"{prefix}_log.txt", log, written, errors);

        if (!run.DryRun)
        {
            var outputRoot = run.OutputFolder ?? run.InputFolder;
            TryWrite(outputRoot, ReportFileName, report, written, errors);
            TryWrite(outputRoot, LogFileName, log, written, errors);
        }

        return written;
    }

    private static void TryWrite(string folder, string fileName, string content, List<string> written, List<string> errors)
    {
        var path = Path.Combine(folder, fileName);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, content);
            written.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Could not write {path} ({ex.Message}).");
        }
    }
}
