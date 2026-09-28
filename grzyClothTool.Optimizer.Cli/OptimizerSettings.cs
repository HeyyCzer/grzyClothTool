using System.Text.Json;

namespace grzyClothTool.Optimizer.Cli;

/// <summary>
/// Choices remembered between runs in <c>grzyOptimizer.settings.json</c>, next to the exe.
/// The interactive menu offers them as defaults; command line runs only take the machine specific
/// ones (<see cref="BlenderPath"/>, <see cref="Sollumz"/>) so scripted runs stay reproducible.
/// Null means "not saved": the built-in default applies.
/// </summary>
internal sealed class OptimizerSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Null = auto-detect Blender on every run (so a Blender upgrade is picked up).</summary>
    public string? BlenderPath { get; set; }
    /// <summary>"installed", "bundled" or a Sollumz add-on folder.</summary>
    public string? Sollumz { get; set; }
    public bool? InPlace { get; set; }
    public int? DiffuseLimit { get; set; }
    public int? NormalLimit { get; set; }
    public int? SpecularLimit { get; set; }
    public bool? Lods { get; set; }
    public double? LodMediumRatio { get; set; }
    public double? LodLowRatio { get; set; }
    public int? LodWorkers { get; set; }

    public static string FilePath =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "grzyOptimizer.settings.json");

    /// <summary>Missing or unreadable file = empty settings; a broken file never blocks a run.</summary>
    public static OptimizerSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                return new OptimizerSettings();
            }
            return JsonSerializer.Deserialize<OptimizerSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new OptimizerSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = $"Could not read {FilePath} ({ex.Message}); using the default settings.";
            return new OptimizerSettings();
        }
    }

    /// <returns>Null when saved, otherwise why not (e.g. the exe sits in a read-only folder).</returns>
    public string? Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not save the settings to {FilePath} ({ex.Message}).";
        }
    }
}
