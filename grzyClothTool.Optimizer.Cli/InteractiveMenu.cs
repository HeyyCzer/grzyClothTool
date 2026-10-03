using grzyClothTool.Optimization.Lods;

namespace grzyClothTool.Optimizer.Cli;

/// <summary>
/// Terminal questions shown when a folder is dragged onto grzyOptimizer.exe (or it is double-clicked).
/// Each question shows its default in parentheses and an empty answer keeps it. Defaults come from the
/// previous run (<see cref="OptimizerSettings"/>), and the answers are saved back for the next one.
/// </summary>
internal static class InteractiveMenu
{
    public static CliOptions Ask(string folder, OptimizerSettings settings)
    {
        var defaults = new CliOptions();
        var fullPath = Path.GetFullPath(folder).TrimEnd('\\', '/');

        Console.WriteLine();
        Console.WriteLine($"Folder: {fullPath}");
        Console.WriteLine("Press Enter to keep the value in parentheses.");
        Console.WriteLine();

        var options = new CliOptions { InputFolder = folder };

        options.InPlace = AskYesNo(
            $"Overwrite the original files? (no = write a copy to \"{Path.GetFileName(fullPath)}_optimized\")",
            settings.InPlace ?? defaults.InPlace);
        options.DiffuseLimit = Ask("Max diffuse resolution", settings.DiffuseLimit ?? defaults.DiffuseLimit,
            v => v.ToString(), s => CliOptions.ParseSize(s, "The resolution"));
        options.NormalLimit = Ask("Max normal map resolution", settings.NormalLimit ?? defaults.NormalLimit,
            v => v.ToString(), s => CliOptions.ParseSize(s, "The resolution"));
        options.SpecularLimit = Ask("Max specular resolution", settings.SpecularLimit ?? defaults.SpecularLimit,
            v => v.ToString(), s => CliOptions.ParseSize(s, "The resolution"));
        options.MaxHighTriangles = Ask("Report clothes whose High LOD has more triangles than", settings.MaxHighTriangles ?? defaults.MaxHighTriangles,
            v => v.ToString(), s => CliOptions.ParseInt(s, "The triangle count", min: 1));

        options.Lods = AskYesNo("Generate the missing LODs with Blender + Sollumz?", settings.Lods ?? defaults.Lods);
        if (options.Lods)
        {
            AskLods(options, settings, defaults);
        }

        settings.InPlace = options.InPlace;
        settings.DiffuseLimit = options.DiffuseLimit;
        settings.NormalLimit = options.NormalLimit;
        settings.SpecularLimit = options.SpecularLimit;
        settings.MaxHighTriangles = options.MaxHighTriangles;
        settings.Lods = options.Lods;
        if (settings.Save() is { } error)
        {
            WriteColored(error, ConsoleColor.Yellow);
        }

        Console.WriteLine();
        return options;
    }

    private static void AskLods(CliOptions options, OptimizerSettings settings, CliOptions defaults)
    {
        // Blender: the saved path, else whatever auto-detection finds. Accepting the detected one saves nothing,
        // so a later Blender upgrade is still picked up.
        var saved = settings.BlenderPath is { } path && TryResolveBlender(path) != null ? path : null;
        var detected = saved ?? BlenderLocator.Find();
        if (detected == null)
        {
            WriteColored("Blender was not found automatically.", ConsoleColor.Yellow);
        }
        var blender = AskOptional("blender.exe (or its folder)", detected, s =>
            TryResolveBlender(s) ?? throw new ArgumentException($"Blender not found at '{s}'."));
        if (blender == null)
        {
            WriteColored("No Blender - LODs will not be generated.", ConsoleColor.Yellow);
            options.Lods = false;
            return;
        }
        options.BlenderPath = blender == detected && saved == null ? null : blender;

        options.Sollumz = Ask("Sollumz: installed (add-on enabled in your Blender), bundled (shipped with grzyOptimizer) or an add-on folder",
            settings.Sollumz ?? defaults.Sollumz, v => v, ParseSollumz);
        options.LodMediumRatio = Ask("Medium LOD size, fraction of the High LOD", settings.LodMediumRatio ?? defaults.LodMediumRatio,
            v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), s => CliOptions.ParseRatio(s, "The Medium LOD size"));

        var lowDefault = settings.LodLowRatio ?? defaults.LodLowRatio;
        if (lowDefault >= options.LodMediumRatio)
        {
            lowDefault = options.LodMediumRatio / 2;
        }
        options.LodLowRatio = Ask("Low LOD size, fraction of the High LOD", lowDefault,
            v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), s =>
            {
                var ratio = CliOptions.ParseRatio(s, "The Low LOD size");
                return ratio < options.LodMediumRatio
                    ? ratio
                    : throw new ArgumentException($"The Low LOD size must be smaller than the Medium one ({options.LodMediumRatio.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}).");
            });
        options.LodWorkers = Ask("Blender processes at the same time", settings.LodWorkers ?? defaults.LodWorkers,
            v => v.ToString(), s => CliOptions.ParseInt(s, "The number of Blender processes", min: 1));

        settings.BlenderPath = options.BlenderPath;
        settings.Sollumz = options.Sollumz;
        settings.LodMediumRatio = options.LodMediumRatio;
        settings.LodLowRatio = options.LodLowRatio;
        settings.LodWorkers = options.LodWorkers;
    }

    private static string ParseSollumz(string value)
    {
        if (string.Equals(value, "installed", StringComparison.OrdinalIgnoreCase))
        {
            return "installed";
        }
        if (string.Equals(value, "bundled", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(Path.Combine(CliOptions.BundledSollumzFolder, "__init__.py"))
                ? "bundled"
                : throw new ArgumentException($"The bundled Sollumz is missing ({CliOptions.BundledSollumzFolder}).");
        }
        var folder = Path.GetFullPath(value);
        return File.Exists(Path.Combine(folder, "__init__.py"))
            ? folder
            : throw new ArgumentException($"No Sollumz add-on in '{folder}' (type installed, bundled or the add-on folder).");
    }

    private static string? TryResolveBlender(string path)
    {
        try
        {
            return BlenderLocator.Resolve(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static bool AskYesNo(string question, bool defaultValue) =>
        Ask(question, defaultValue, v => v ? "y" : "n", s => s.ToLowerInvariant() switch
        {
            "y" or "yes" or "s" or "sim" => true,
            "n" or "no" or "nao" or "não" => false,
            _ => throw new ArgumentException("Answer y or n.")
        });

    /// <summary>Asks until the answer parses; <paramref name="parse"/> rejects it by throwing ArgumentException.</summary>
    private static T Ask<T>(string question, T defaultValue, Func<T, string> format, Func<string, T> parse) =>
        AskCore(question, format(defaultValue), parse) is { } answer ? answer.Value : defaultValue;

    /// <summary>Like <see cref="Ask{T}"/> but there may be no default: then an empty answer returns null.</summary>
    private static string? AskOptional(string question, string? defaultValue, Func<string, string> parse) =>
        AskCore(question, defaultValue ?? "none", parse) is { } answer ? answer.Value : defaultValue;

    /// <returns>Null when the answer is empty (keep the default).</returns>
    private static Answer<T>? AskCore<T>(string question, string defaultText, Func<string, T> parse)
    {
        while (true)
        {
            Console.Write($"{question} ({defaultText}): ");
            var line = Console.ReadLine()?.Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }
            try
            {
                return new Answer<T>(parse(line));
            }
            catch (ArgumentException ex)
            {
                WriteColored($"  {ex.Message}", ConsoleColor.Red);
            }
        }
    }

    private sealed record Answer<T>(T Value);

    private static void WriteColored(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
