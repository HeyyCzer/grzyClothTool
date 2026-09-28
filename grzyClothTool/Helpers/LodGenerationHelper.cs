using CodeWalker.GameFiles;
using grzyClothTool.Models;
using grzyClothTool.Models.Drawable;
using grzyClothTool.Optimization.Lods;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static grzyClothTool.Models.Drawable.GDrawableDetails;

namespace grzyClothTool.Helpers;
#nullable enable

public class LodCandidate : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string AddonName { get; init; }
    public required GDrawable Drawable { get; init; }
    public required int HighPolygons { get; init; }
    public required IReadOnlyList<DetailLevel> MissingLevels { get; init; }

    public string DrawableName => Drawable.Name;
    public string MissingText => string.Join(", ", MissingLevels);

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }
    }
}

public class LodScanResult
{
    public List<LodCandidate> Candidates { get; } = [];

    /// <summary>Drawables missing LODs that cannot get them generated (cloth physics).</summary>
    public int SkippedClothPhysics { get; set; }

    /// <summary>Drawables whose details were still loading when the scan ran.</summary>
    public int Pending { get; set; }
}

/// <summary>
/// Generates the missing Medium/Low LODs of project drawables with Blender + Sollumz, through the same
/// <see cref="LodGenerator"/> the grzyOptimizer CLI uses. The result is written to the project assets as
/// <c>{drawable id}.ydd</c> and the drawable points to it, so files of external projects are never overwritten.
/// </summary>
public static class LodGenerationHelper
{
    public const string SollumzInstalled = "installed";
    public const string SollumzBundled = "bundled";
    public const string SollumzFolder = "folder";

    /// <summary>Where the build copies the Sollumz submodule (next to grzyClothTool.exe).</summary>
    public static string BundledSollumzFolder => Path.Combine(AppContext.BaseDirectory, "sollumz");

    public static LodScanResult Scan(IEnumerable<Addon> addons)
    {
        var result = new LodScanResult();

        foreach (var addon in addons)
        {
            foreach (var drawable in addon.Drawables.ToList())
            {
                if (drawable.IsReserved || drawable.IsEncrypted)
                {
                    continue;
                }

                var details = drawable.Details;
                if (details == null)
                {
                    if (drawable.IsLoading)
                    {
                        result.Pending++;
                    }
                    continue;
                }

                var high = details.AllModels.GetValueOrDefault(DetailLevel.High);
                if (high == null || high.PolyCount == 0)
                {
                    continue;
                }

                var missing = new[] { DetailLevel.Med, DetailLevel.Low }
                    .Where(level => details.AllModels.GetValueOrDefault(level) == null)
                    .ToList();
                if (missing.Count == 0)
                {
                    continue;
                }

                // Physically simulated clothes are bound vertex by vertex to the High model.
                if (!string.IsNullOrEmpty(drawable.ClothPhysicsPath))
                {
                    result.SkippedClothPhysics++;
                    continue;
                }

                result.Candidates.Add(new LodCandidate
                {
                    AddonName = addon.Name,
                    Drawable = drawable,
                    HighPolygons = high.PolyCount,
                    MissingLevels = missing
                });
            }
        }

        return result;
    }

    /// <summary>Turns the saved settings into generator options; throws <see cref="ArgumentException"/> when invalid.</summary>
    public static LodGenerationOptions CreateOptions(LodGeneratorSettings settings)
    {
        if (settings.MediumRatio is <= 0 or >= 1 || settings.LowRatio is <= 0 or >= 1)
        {
            throw new ArgumentException("LOD sizes must be between 0 and 100% of the High LOD.");
        }
        if (settings.LowRatio > settings.MediumRatio)
        {
            throw new ArgumentException("The Low LOD must not be bigger than the Medium LOD.");
        }

        var mode = settings.SollumzMode?.ToLowerInvariant() ?? SollumzInstalled;
        string? sollumzFolder = mode switch
        {
            SollumzInstalled => null,
            SollumzBundled => BundledSollumzFolder,
            _ => settings.SollumzFolder
        };

        if (sollumzFolder != null && !File.Exists(Path.Combine(sollumzFolder, "__init__.py")))
        {
            throw new ArgumentException(mode == SollumzBundled
                ? $"The bundled Sollumz is missing ({sollumzFolder})."
                : $"No Sollumz add-on found in '{sollumzFolder}'.");
        }

        var options = new LodGenerationOptions
        {
            BlenderPath = string.IsNullOrWhiteSpace(settings.BlenderPath) ? null : settings.BlenderPath,
            Sollumz = sollumzFolder == null ? SollumzSource.Installed : SollumzSource.Folder,
            SollumzFolder = sollumzFolder,
            DataFolder = Path.Combine(AppDataHelper.GetLocalAppDataPath(), "grzyClothTool", "sollumz-data"),
            MediumRatio = settings.MediumRatio,
            LowRatio = settings.LowRatio,
            MaxWorkers = Math.Clamp(settings.Workers, 1, 8)
        };

        try
        {
            BlenderLocator.Resolve(options.BlenderPath);
        }
        catch (FileNotFoundException ex)
        {
            throw new ArgumentException(ex.Message.Replace("pass its path with --blender", "set its path"), ex);
        }

        return options;
    }

    /// <summary>
    /// Generates the missing LODs of one drawable. Runs off the UI thread; returns the new file's path relative to
    /// the project assets (to assign to <see cref="GDrawable.FilePath"/>), or null when nothing was added.
    /// </summary>
    public static async Task<(string? RelativePath, List<LodChange> Changes, List<string> Notes)> GenerateAsync(
        LodGenerator generator, GDrawable drawable, CancellationToken cancellationToken)
    {
        var changes = new List<LodChange>();
        var notes = new List<string>();
        var source = drawable.FullFilePath;

        var ydd = new YddFile();
        await ydd.LoadAsync(await File.ReadAllBytesAsync(source, cancellationToken));

        bool added = await generator.AddMissingLodsAsync(ydd, source, changes, notes, dryRun: false, cancellationToken);
        if (!added)
        {
            return (null, changes, notes);
        }

        var bytes = ydd.Save();
        var fileName = $"{drawable.Id}.ydd";
        var destination = Path.Combine(FileHelper.GetProjectAssetsPath(), fileName);

        // Write next to the destination and swap: the destination may be the file the drawable uses right now.
        var temp = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return (fileName, changes, notes);
    }
}
