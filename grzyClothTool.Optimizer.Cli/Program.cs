using grzyClothTool.Optimization;
using grzyClothTool.Optimization.Lods;
using System.Diagnostics;
using System.Text;

namespace grzyClothTool.Optimizer.Cli;

internal static class Program
{
    private const string Usage = """
        grzyOptimizer - optimizes GTA V textures (.ytd and textures embedded in .ydd) in a folder,
        and optionally generates the missing LODs of .ydd models with Blender + Sollumz.

        Usage:
          grzyOptimizer <folder> [<folder>...] [options]
          (or drag one or more folders onto grzyOptimizer.exe: the settings are then asked in the terminal)

        By default the optimized copy of the whole folder is written to "<folder>_optimized";
        the original folder is not touched. Several folders run one after another with the same settings,
        each with its own copy and reports.

        Options:
          -o, --out <folder>     Output folder (default: <folder>_optimized; only with a single input folder)
              --in-place         Overwrite the files in <folder> instead of writing a copy
              --diffuse <px>     Max diffuse resolution  (default: 1024)
              --normal <px>      Max normal map resolution (default: 1024)
              --specular <px>    Max specular resolution (default: 1024)
              --dry-run          Only list what would change; write nothing
          -j, --threads <n>      Parallel files (default: CPU cores - 1)
              --max-tris <n>     Report clothes whose High LOD has more triangles (default: 15000)
              --include-hair     Also optimize hair (^hair_*, *hair* overlays). By default hair files are copied
                                 unchanged: optimized hair crashed FiveM clients (NVIDIA driver) in the barbershop
          -v, --verbose          List every texture change
              --no-menu          With only a folder given, run with the defaults instead of asking
          -h, --help             Show this help

        Updates:
              --update           Download and install the latest grzyOptimizer, then exit
              --no-update        Skip the update check at startup (or set GRZYOPTIMIZER_SKIP_UPDATE=1)
          At startup grzyOptimizer checks GitHub for a newer version (5s max, silent when offline). Interactive
          runs (double-click / dropped folder) offer to install it and continue with the new version; command line
          runs only print a notice, so scripts are never changed under them.

        LOD generation (needs Blender 4.2+; any of these options turns it on):
              --lods             Generate the Medium/Low LODs missing from .ydd models
              --blender <path>   blender.exe (default: newest Blender in Program Files, then PATH)
              --sollumz <mode>   Which Sollumz to use (default: installed):
                                   installed  the Sollumz add-on enabled in your Blender
                                   bundled    the Sollumz copy shipped with grzyOptimizer (sollumz/ folder);
                                              runs in a clean Blender, your add-ons are not touched
                                   <folder>   any Sollumz add-on folder, loaded like "bundled"
              --lod-medium <f>   Medium LOD size as a fraction of the High LOD (default: 0.5)
              --lod-low <f>      Low LOD size as a fraction of the High LOD (default: 0.25)
              --lod-workers <n>  Blender processes at the same time (default: 2)

        What is optimized (same rules as grzyClothTool):
          - resolution: nearest power of two, halved until within the limit (aspect ratio kept)
          - uncompressed textures (A8R8G8B8/X8R8G8B8/A8B8G8R8) are compressed to DXT5
          - textures with a single mip level get a full mip chain
          Textures in formats that cannot be re-encoded (ATI2/BC5, BC7) are reported and kept.

        LODs: for every drawable with a High model but no Medium/Low, Sollumz's "Generate LODs" decimates the
        High mesh (edge collapse) and only the new LOD models are added to the original .ydd; textures, shaders
        and the High model are kept as they are. Clothes with physics (.yld next to the .ydd) are skipped.

        Reports: after each run a review report (failed/unreadable files, clothes above --max-tris, missing LODs,
        warnings) and a session log (settings + every change) are written to logs/ next to the exe and, unless it is
        a dry run, to the output folder as grzyOptimizer_report.txt and grzyOptimizer_log.txt.

        Settings: the answers of the interactive menu are saved to grzyOptimizer.settings.json next to the exe
        and offered as defaults next time. Command line runs only take the Blender path and Sollumz mode from it.
        """;

    private static readonly object ConsoleLock = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Updater.CleanupPreviousUpdate();
        if (args.Contains("--update"))
        {
            return await Updater.UpdateCommandAsync();
        }
        bool skipUpdate = Updater.SkipRequested || args.Contains("--no-update");
        args = args.Where(a => a != "--no-update").ToArray();

        var settings = OptimizerSettings.Load(out var settingsWarning);
        if (settingsWarning != null)
        {
            WriteLine(settingsWarning, ConsoleColor.Yellow);
        }

        // Double-click (no arguments) or folders dropped on the exe (only folders): ask the settings in the
        // terminal and keep the window open at the end. Any option on the command line skips the menu.
        bool interactive = args.All(a => !IsOption(a));

        if (!skipUpdate && await Updater.CheckOnStartupAsync(args, interactive) is { } updatedExitCode)
        {
            // The new version ran this job (and its own "press any key").
            return updatedExitCode;
        }

        if (args.Length == 0)
        {
            Console.WriteLine("grzyOptimizer - drag a folder here (or type its path) and press Enter:");
            var typed = Console.ReadLine()?.Trim().Trim('"');
            if (string.IsNullOrEmpty(typed))
            {
                Console.WriteLine(Usage);
                return Pause(interactive, 2);
            }
            args = [typed];
        }

        CliOptions cli;
        try
        {
            cli = CliOptions.Parse(args, settings);
            // Checked up front so a typo in the last folder doesn't show up only after the first ones ran.
            if (cli.InputFolders.FirstOrDefault(f => !Directory.Exists(f)) is { } missing)
            {
                throw new ArgumentException($"Folder not found: '{missing}'.");
            }
            if (interactive)
            {
                cli = InteractiveMenu.Ask(cli.InputFolders, settings);
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Run with --help to see the options.");
            return Pause(interactive, 2);
        }

        if (cli.ShowHelp)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (cli.Lods && !ValidateLods(cli.ToOptimizerOptions(cli.InputFolders[0]).Lods!))
        {
            return Pause(interactive, 2);
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Cancelling after the files in progress...");
            cancellation.Cancel();
        };

        var exitCodes = new List<int>();
        for (int i = 0; i < cli.InputFolders.Count; i++)
        {
            var folder = cli.InputFolders[i];
            if (cli.InputFolders.Count > 1)
            {
                WriteLine($"=== Folder {i + 1}/{cli.InputFolders.Count}: {Path.GetFullPath(folder)} ===", ConsoleColor.Cyan);
            }

            int exitCode = await RunFolderAsync(cli, cli.ToOptimizerOptions(folder), args, cancellation.Token);
            exitCodes.Add(exitCode);
            if (cancellation.IsCancellationRequested)
            {
                break;
            }
            Console.WriteLine();
        }

        if (cli.InputFolders.Count > 1)
        {
            PrintFoldersSummary(cli.InputFolders, exitCodes);
        }
        return Pause(interactive, exitCodes.Max());
    }

    /// <summary>Optimizes one folder, prints its summary and writes its reports. Returns the exit code of that folder.</summary>
    private static async Task<int> RunFolderAsync(CliOptions cli, FolderOptimizerOptions options, string[] args, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Input:   {Path.GetFullPath(options.InputFolder)}");
        Console.WriteLine(options.OutputFolder == null ? "Output:  in place" : $"Output:  {Path.GetFullPath(options.OutputFolder)}");
        Console.WriteLine($"Limits:  diffuse {options.DiffuseLimit}px, normal {options.NormalLimit}px, specular {options.SpecularLimit}px");
        Console.WriteLine(options.SkipHair ? "Hair:    kept as-is (--include-hair to optimize)" : "Hair:    optimized");
        if (options.Lods is { } lods)
        {
            var sollumz = lods.Sollumz == SollumzSource.Installed ? "installed add-on" : lods.SollumzFolder;
            Console.WriteLine($"LODs:    medium {lods.MediumRatio:0.##}, low {lods.LowRatio:0.##} of High - {BlenderLocator.Resolve(lods.BlenderPath)} (Sollumz: {sollumz})");
        }
        Console.WriteLine($"Report:  clothes above {cli.MaxHighTriangles} High LOD triangles");
        Console.WriteLine($"Threads: {options.MaxParallelism}{(options.DryRun ? "   (dry run - nothing will be written)" : "")}");
        Console.WriteLine();

        int total = CountFiles(options);
        int done = 0;
        // Kept here too so a cancelled run still gets its report and log.
        var processed = new List<FileResult>();
        var progress = new SyncProgress<FileResult>(result =>
        {
            lock (processed)
            {
                processed.Add(result);
            }
            int current = Interlocked.Increment(ref done);
            PrintResult(result, current, total, cli.Verbose);
        });

        var started = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();
        FolderOptimizationSummary summary;
        try
        {
            summary = await new FolderOptimizer(options).RunAsync(progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelled. Files already written are complete; the rest was not processed.");
            List<FileResult> partial;
            lock (processed)
            {
                partial = processed.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            }
            WriteReports(new FolderOptimizationSummary(partial, stopwatch.Elapsed), cli, options, args, started, cancelled: true);
            return 130;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }

        PrintSummary(summary, options.DryRun);
        WriteReports(summary, cli, options, args, started, cancelled: false);
        return summary.Count(FileOutcome.Failed) > 0 ? 1 : 0;
    }

    /// <param name="exitCodes">Exit code of each folder that ran, in order; folders after a cancel have none.</param>
    private static void PrintFoldersSummary(IReadOnlyList<string> folders, List<int> exitCodes)
    {
        Console.WriteLine();
        Console.WriteLine("Folders");
        for (int i = 0; i < folders.Count; i++)
        {
            var (text, color) = i >= exitCodes.Count ? ("not run (cancelled)", ConsoleColor.DarkGray)
                : exitCodes[i] switch
                {
                    0 => ("ok", ConsoleColor.Green),
                    1 => ("done, with failed files (see its report)", ConsoleColor.Yellow),
                    130 => ("cancelled", ConsoleColor.Yellow),
                    _ => ("error", ConsoleColor.Red)
                };
            WriteLine($"  {Path.GetFullPath(folders[i])}: {text}", color);
        }
    }

    private static void WriteReports(FolderOptimizationSummary summary, CliOptions cli, FolderOptimizerOptions options,
        string[] args, DateTime started, bool cancelled)
    {
        var reportOptions = new OptimizationReportOptions
        {
            Settings = DescribeSettings(cli, options, args, started),
            DryRun = options.DryRun,
            Cancelled = cancelled,
            HighTriangleLimit = cli.MaxHighTriangles
        };

        var written = ReportWriter.Write(summary, reportOptions, options, started, out var errors);
        Console.WriteLine();
        foreach (var path in written)
        {
            Console.WriteLine($"Written: {path}");
        }
        foreach (var error in errors)
        {
            WriteLine(error, ConsoleColor.Yellow);
        }
    }

    private static List<KeyValuePair<string, string>> DescribeSettings(CliOptions cli, FolderOptimizerOptions options, string[] args, DateTime started)
    {
        var settings = new List<KeyValuePair<string, string>>
        {
            new("Started", started.ToString("yyyy-MM-dd HH:mm:ss")),
            new("Command line", string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))),
            new("Input", options.InputFolder),
            new("Output", options.OutputFolder ?? "in place"),
            new("Dry run", options.DryRun ? "yes" : "no"),
            new("Max diffuse", $"{options.DiffuseLimit}px"),
            new("Max normal", $"{options.NormalLimit}px"),
            new("Max specular", $"{options.SpecularLimit}px"),
            new("Max High tris", cli.MaxHighTriangles.ToString()),
            new("Hair", options.SkipHair ? "kept as-is" : "optimized"),
            new("Threads", options.MaxParallelism.ToString())
        };

        if (options.Lods is { } lods)
        {
            settings.Add(new("LODs", $"medium {lods.MediumRatio:0.##}, low {lods.LowRatio:0.##} of High"));
            settings.Add(new("Blender", BlenderLocator.Resolve(lods.BlenderPath)));
            settings.Add(new("Sollumz", lods.Sollumz == SollumzSource.Installed ? "installed add-on" : lods.SollumzFolder ?? ""));
            settings.Add(new("Blender workers", lods.MaxWorkers.ToString()));
        }
        else
        {
            settings.Add(new("LODs", "not generated"));
        }

        return settings;
    }

    private static bool ValidateLods(LodGenerationOptions lods)
    {
        try
        {
            BlenderLocator.Resolve(lods.BlenderPath);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return false;
        }

        if (lods.Sollumz == SollumzSource.Folder && !File.Exists(Path.Combine(lods.SollumzFolder!, "__init__.py")))
        {
            Console.Error.WriteLine(lods.SollumzFolder == CliOptions.BundledSollumzFolder
                ? $"Error: the bundled Sollumz is missing ({lods.SollumzFolder}). Build after \"git submodule update --init\"."
                : $"Error: no Sollumz add-on in '{lods.SollumzFolder}'.");
            return false;
        }

        return true;
    }

    private static int CountFiles(FolderOptimizerOptions options)
    {
        try
        {
            return Directory.EnumerateFiles(options.InputFolder, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void PrintResult(FileResult result, int current, int total, bool verbose)
    {
        // Plain copies and untouched files only clutter the output.
        if (result.Outcome is FileOutcome.Copied or FileOutcome.Unchanged or FileOutcome.Excluded && result.Notes.Count == 0)
        {
            if (current % 200 == 0)
            {
                WriteLine($"[{current}/{total}] ...", ConsoleColor.DarkGray);
            }
            return;
        }

        var prefix = $"[{current}/{total}]";
        switch (result.Outcome)
        {
            case FileOutcome.Optimized:
                var size = result.SizeAfter != result.SizeBefore ? $"  {FormatBytes(result.SizeBefore)} -> {FormatBytes(result.SizeAfter)}" : "";
                var what = result.Lods.Count == 0
                    ? $"{result.Changes.Count} texture(s)"
                    : $"{result.Changes.Count} texture(s), {result.Lods.Count} LOD(s)";
                WriteLine($"{prefix} OK      {result.RelativePath}  ({what}){size}", ConsoleColor.Green);
                if (verbose)
                {
                    foreach (var change in result.Changes)
                    {
                        WriteLine($"          {change.Name} [{change.Kind}] {Describe(change.Before)} -> {Describe(change.After)}  ({string.Join(", ", change.Reasons)})", ConsoleColor.Gray);
                    }
                }
                // LODs are always listed: there are few of them and their triangle counts are worth checking.
                foreach (var lod in result.Lods)
                {
                    var after = lod.Triangles is { } triangles ? $"{triangles} tris" : "to generate";
                    WriteLine($"          {lod.Drawable} [LOD {lod.Level}] high {lod.HighTriangles} tris -> {after}", ConsoleColor.Gray);
                }
                break;
            case FileOutcome.Skipped:
                WriteLine($"{prefix} SKIPPED {result.RelativePath}", ConsoleColor.Yellow);
                break;
            case FileOutcome.Failed:
                WriteLine($"{prefix} FAILED  {result.RelativePath}: {result.Error}", ConsoleColor.Red);
                break;
            default:
                WriteLine($"{prefix} NOTE    {result.RelativePath}", ConsoleColor.Yellow);
                break;
        }

        foreach (var note in result.Notes)
        {
            WriteLine($"          {note}", ConsoleColor.Yellow);
        }
    }

    private static void PrintSummary(FolderOptimizationSummary summary, bool dryRun)
    {
        Console.WriteLine();
        Console.WriteLine(dryRun ? "Summary (dry run - nothing was written)" : "Summary");
        Console.WriteLine($"  Files:              {summary.Files.Count}");
        Console.WriteLine($"  Optimized:          {summary.Count(FileOutcome.Optimized)} file(s), {summary.TexturesOptimized} texture(s)");
        if (summary.LodsGenerated > 0)
        {
            Console.WriteLine($"  LODs generated:     {summary.LodsGenerated}");
        }
        Console.WriteLine($"  Already fine:       {summary.Count(FileOutcome.Unchanged)} texture file(s)");
        Console.WriteLine($"  Other files copied: {summary.Count(FileOutcome.Copied)}");
        if (summary.Count(FileOutcome.Excluded) > 0)
        {
            Console.WriteLine($"  Hair (kept as-is):  {summary.Count(FileOutcome.Excluded)}");
        }
        if (summary.Count(FileOutcome.Skipped) > 0)
        {
            WriteLine($"  Unreadable (kept):  {summary.Count(FileOutcome.Skipped)}", ConsoleColor.Yellow);
        }
        if (summary.Count(FileOutcome.Failed) > 0)
        {
            WriteLine($"  Failed (kept):      {summary.Count(FileOutcome.Failed)}", ConsoleColor.Red);
        }
        if (!dryRun)
        {
            Console.WriteLine($"  Disk size:          {FormatBytes(summary.SizeBefore)} -> {FormatBytes(summary.SizeAfter)}");
        }
        Console.WriteLine($"  Texture memory:     {FormatBytes(summary.TextureMemoryBefore)} -> {FormatBytes(summary.TextureMemoryAfter)} (optimized textures, estimated)");
        Console.WriteLine($"  Time:               {summary.Elapsed:hh\\:mm\\:ss}");
    }

    private static string Describe(TextureInfo info) => OptimizationReport.Describe(info);

    private static string FormatBytes(long bytes) => OptimizationReport.FormatBytes(bytes);

    private static void WriteLine(string text, ConsoleColor color)
    {
        lock (ConsoleLock)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
        }
    }

    private static bool IsOption(string arg) => arg.StartsWith('-') || arg == "/?";

    private static int Pause(bool interactive, int exitCode)
    {
        if (interactive && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to close...");
            Console.ReadKey(intercept: true);
        }
        return exitCode;
    }

    /// <summary>IProgress that runs the callback on the reporting thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
