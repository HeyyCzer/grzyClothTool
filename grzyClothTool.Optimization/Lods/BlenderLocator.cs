namespace grzyClothTool.Optimization.Lods;

/// <summary>Finds blender.exe: an explicit path, the default install folders, or the PATH.</summary>
public static class BlenderLocator
{
    public static string Resolve(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            // Accept the Blender folder as well as the executable itself.
            if (Directory.Exists(explicitPath))
            {
                var inFolder = Path.Combine(explicitPath, ExecutableName);
                if (File.Exists(inFolder))
                {
                    return inFolder;
                }
            }
            if (File.Exists(explicitPath))
            {
                return explicitPath;
            }
            throw new FileNotFoundException($"Blender not found at '{explicitPath}'.");
        }

        return Find() ?? throw new FileNotFoundException(
            "Blender not found. Install Blender 4.2 or newer, or pass its path with --blender.");
    }

    public static string? Find()
    {
        var candidates = new List<string>();

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            var foundation = Path.Combine(root, "Blender Foundation");
            if (!string.IsNullOrEmpty(root) && Directory.Exists(foundation))
            {
                // "Blender 4.2", "Blender 4.5"... newest first.
                candidates.AddRange(Directory.GetDirectories(foundation, "Blender*")
                    .OrderByDescending(ParseVersion)
                    .Select(d => Path.Combine(d, ExecutableName)));
            }
        }

        var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Blender", ExecutableName);
        candidates.Add(steam);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim('"'), ExecutableName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "blender.exe" : "blender";

    private static Version ParseVersion(string directory)
    {
        var name = Path.GetFileName(directory).Replace("Blender", "", StringComparison.OrdinalIgnoreCase).Trim();
        return Version.TryParse(name, out var version) ? version : new Version(0, 0);
    }
}
