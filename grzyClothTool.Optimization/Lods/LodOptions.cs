namespace grzyClothTool.Optimization.Lods;

public enum LodLevel
{
    Medium,
    Low
}

/// <summary>A LOD level generated (or, in a dry run, to be generated) for one drawable of a .ydd.</summary>
public sealed record LodChange(string Drawable, LodLevel Level, int HighTriangles, int? Triangles);

/// <summary>Which Sollumz the Blender workers use.</summary>
public enum SollumzSource
{
    /// <summary>The Sollumz add-on/extension enabled in the user's Blender.</summary>
    Installed,
    /// <summary>A Sollumz folder (the bundled submodule copy or any other) loaded into a clean Blender.</summary>
    Folder
}

public sealed record LodGenerationOptions
{
    /// <summary>blender.exe to run. Null looks for an installed Blender.</summary>
    public string? BlenderPath { get; init; }

    public SollumzSource Sollumz { get; init; } = SollumzSource.Installed;

    /// <summary>Sollumz add-on folder, used with <see cref="SollumzSource.Folder"/>.</summary>
    public string? SollumzFolder { get; init; }

    /// <summary>Where a Sollumz loaded from a folder keeps its Python dependencies (installed on first use).</summary>
    public string DataFolder { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "grzyClothTool", "sollumz-data");

    /// <summary>Medium LOD triangles as a fraction of the High LOD.</summary>
    public double MediumRatio { get; init; } = 0.5;

    /// <summary>Low LOD triangles as a fraction of the High LOD.</summary>
    public double LowRatio { get; init; } = 0.25;

    /// <summary>Blender processes running at the same time (each one takes a few hundred MB of memory).</summary>
    public int MaxWorkers { get; init; } = 2;

    /// <summary>Longest time a single file may take inside Blender before its worker is killed.</summary>
    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
