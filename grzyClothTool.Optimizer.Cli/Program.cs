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
          grzyOptimizer <folder> [options]
          (or drag a folder onto grzyOptimizer.exe: the settings are then asked in the terminal)

        By default the optimized copy of the whole folder is written to "<folder>_optimized";
        the original folder is not touched.

        Options:
          -o, --out <folder>     Output folder (default: <folder>_optimized)
              --in-place         Overwrite the files in <folder> instead of writing a copy
              --diffuse <px>     Max diffuse resolution  (default: 1024)
              --normal <px>      Max normal map resolution (default: 1024)
              --specular <px>    Max specular resolution (default: 1024)
              --dry-run          Only list what would change; write nothing
          -j, --threads <n>      Parallel files (default: CPU cores - 1)
          -v, --verbose          List every texture change
              --no-menu          With only a folder given, run with the defaults instead of asking
          -h, --help             Show this help

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

        Settings: the answers of the interactive menu are saved to grzyOptimizer.settings.json next to the exe
        and offered as defaults next time. Command line runs only take the Blender path and Sollumz mode from it.
        """;

    private static readonly object ConsoleLock = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var settings = OptimizerSettings.Load(out var settingsWarning);
        if (settingsWarning != null)
        {
            WriteLine(settingsWarning, ConsoleColor.Yellow);
        }

        // Double-click (no arguments) or a folder dropped on the exe (the folder alone): ask the settings in the
        // terminal and keep the window open at the end. Any option on the command line skips the menu.
        bool interactive = args.Length == 0 || args is [var only] && !IsOption(only);
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
            if (interactive)
            {
                if (!Directory.Exists(cli.InputFolder))
                {
                    throw new ArgumentException($"Folder not found: '{cli.InputFolder}'.");
                }
                cli = InteractiveMenu.Ask(cli.InputFolder!, settings);
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

        var options = cli.ToOptimizerOptions();
        if (options.Lods != null && !ValidateLods(options.Lods))
        {
            return Pause(interactive, 2);
        }

        Console.WriteLine($"Input:   {Path.GetFullPath(options.InputFolder)}");
        Console.WriteLine(options.OutputFolder == null ? "Output:  in place" : $"Output:  {Path.GetFullPath(options.OutputFolder)}");
        Console.WriteLine($"Limits:  diffuse {options.DiffuseLimit}px, normal {options.NormalLimit}px, specular {options.SpecularLimit}px");
        if (options.Lods is { } lods)
        {
            var sollumz = lods.Sollumz == SollumzSource.Installed ? "installed add-on" : lods.SollumzFolder;
            Console.WriteLine($"LODs:    medium {lods.MediumRatio:0.##}, low {lods.LowRatio:0.##} of High - {BlenderLocator.Resolve(lods.BlenderPath)} (Sollumz: {sollumz})");
        }
        Console.WriteLine($"Threads: {options.MaxParallelism}{(options.DryRun ? "   (dry run - nothing will be written)" : "")}");
        Console.WriteLine();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Cancelling after the files in progress...");
            cancellation.Cancel();
        };

        int total = CountFiles(options);
        int done = 0;
        var progress = new SyncProgress<FileResult>(result =>
        {
            int current = Interlocked.Increment(ref done);
            PrintResult(result, current, total, cli.Verbose);
        });

        FolderOptimizationSummary summary;
        try
        {
            summary = await new FolderOptimizer(options).RunAsync(progress, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelled. Files already written are complete; the rest was not processed.");
            return Pause(interactive, 130);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return Pause(interactive, 2);
        }

        PrintSummary(summary, options.DryRun);
        return Pause(interactive, summary.Count(FileOutcome.Failed) > 0 ? 1 : 0);
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
        if (result.Outcome is FileOutcome.Copied or FileOutcome.Unchanged && result.Notes.Count == 0)
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

    private static string Describe(TextureInfo info) =>
        $"{info.Width}x{info.Height} {info.Compression.Replace("D3DFMT_", "")} ({info.MipMapCount} mips)";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{bytes / 1024.0:0.#} KB"
    };

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
