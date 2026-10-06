using grzyClothTool.Models;
using grzyClothTool.Models.Drawable;
using grzyClothTool.Models.Texture;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace grzyClothTool.Helpers;
#nullable enable

public class TextureOptimizationCandidate : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string AddonName { get; init; }
    public required GDrawable Drawable { get; init; }

    /// <summary>Either a <see cref="GTexture"/> or a <see cref="GTextureEmbedded"/>.</summary>
    public required object Texture { get; init; }
    public required string TextureName { get; init; }
    public required string Kind { get; init; }
    public required GTextureDetails Current { get; init; }
    public required GTextureDetails Target { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }

    public long CurrentSizeBytes => TextureOptimizer.EstimateSizeBytes(Current);
    public long TargetSizeBytes => TextureOptimizer.EstimateSizeBytes(Target);
    public long SavedBytes => CurrentSizeBytes - TargetSizeBytes;

    public string DrawableName => Drawable.Name;
    public string CurrentText => Describe(Current);
    public string TargetText => Describe(Target);
    public string ReasonsText => string.Join(", ", Reasons);

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

    private static string Describe(GTextureDetails d) =>
        $"{d.Width}x{d.Height} {FormatCompression(d.Compression)} ({d.MipMapCount} mips)";

    private static string FormatCompression(string compression) => compression switch
    {
        null or "" or "UNKNOWN" => "IMG",
        TextureOptimizer.AutoCompression => "DXT1 if opaque, else DXT5",
        _ => compression.Replace("D3DFMT_", "")
    };
}

public class DrawablePerformanceIssue
{
    public required string AddonName { get; init; }
    public required GDrawable Drawable { get; init; }
    public required string Message { get; init; }

    public string DrawableName => Drawable.Name;
}

public class AutoOptimizerScanResult
{
    public List<TextureOptimizationCandidate> Candidates { get; } = [];

    /// <summary>Warnings the optimizer cannot fix automatically (polygons, missing LODs, unsupported formats...).</summary>
    public List<DrawablePerformanceIssue> ManualIssues { get; } = [];

    /// <summary>Textures whose details were still loading when the scan ran.</summary>
    public int PendingTextures { get; set; }
}

/// <summary>
/// Scans the project for texture performance warnings and marks the fixable ones to be
/// optimized during build, reusing the same pipeline as the manual "Optimize texture" window.
/// </summary>
public static class AutoOptimizer
{
    private const string TextureWarningsTooltip = "Some textures have warnings. Check texture details.";

    public static AutoOptimizerScanResult Scan(IEnumerable<Addon> addons)
    {
        var result = new AutoOptimizerScanResult();

        foreach (var addon in addons)
        {
            foreach (var drawable in addon.Drawables.ToList())
            {
                if (drawable.IgnoreWarnings || drawable.IsReserved)
                {
                    continue;
                }

                ScanTextures(addon, drawable, result);
                ScanEmbeddedTextures(addon, drawable, result);
                CollectManualIssues(addon, drawable, result);
            }
        }

        return result;
    }

    public static int Apply(IEnumerable<TextureOptimizationCandidate> candidates)
    {
        int applied = 0;

        foreach (var candidate in candidates)
        {
            var target = new GTextureDetails
            {
                Width = candidate.Target.Width,
                Height = candidate.Target.Height,
                MipMapCount = candidate.Target.MipMapCount,
                Compression = candidate.Target.Compression,
                Name = candidate.Target.Name,
                Type = candidate.Target.Type,
                IsOptimizeNeeded = false
            };

            switch (candidate.Texture)
            {
                case GTexture texture:
                    texture.OptimizeDetails = target;
                    texture.IsOptimizedDuringBuild = true;
                    break;
                case GTextureEmbedded embedded:
                    embedded.OptimizeDetails = target;
                    embedded.IsOptimizedDuringBuild = true;
                    break;
                default:
                    continue;
            }

            applied++;
        }

        if (applied > 0)
        {
            LogHelper.Log($"Auto optimizer: {applied} texture(s) will be optimized during resource build");
        }

        return applied;
    }

    private static void ScanTextures(Addon addon, GDrawable drawable, AutoOptimizerScanResult result)
    {
        if (drawable.Textures == null)
        {
            return;
        }

        foreach (var texture in drawable.Textures.ToList())
        {
            if (texture.IsOptimizedDuringBuild)
            {
                continue;
            }

            if (texture.TxtDetails == null)
            {
                if (texture.IsLoading)
                {
                    result.PendingTextures++;
                }
                continue;
            }

            if (!texture.TxtDetails.IsOptimizeNeeded)
            {
                continue;
            }

            TryAddCandidate(addon, drawable, texture, texture.DisplayName, "Texture", texture.TxtDetails, result);
        }
    }

    private static void ScanEmbeddedTextures(Addon addon, GDrawable drawable, AutoOptimizerScanResult result)
    {
        var embeddedTextures = drawable.Details?.EmbeddedTextures;
        if (embeddedTextures == null || drawable.IsEncrypted)
        {
            return;
        }

        foreach (var (type, embedded) in embeddedTextures)
        {
            if (embedded == null || !embedded.HasOriginalTexture || embedded.IsOptimizedDuringBuild || !embedded.Details.IsOptimizeNeeded)
            {
                continue;
            }

            TryAddCandidate(addon, drawable, embedded, embedded.Details.Name ?? type.ToString(), type.ToString(), embedded.Details, result);
        }
    }

    private static void TryAddCandidate(Addon addon, GDrawable drawable, object texture, string name, string kind, GTextureDetails current, AutoOptimizerScanResult result)
    {
        var target = TextureOptimizer.ComputeTarget(current, TextureOptimizer.GetResolutionLimit(current.Type));
        if (target == null)
        {
            var reason = TextureOptimizer.CanAutoOptimize(current.Compression)
                ? "has warnings that cannot be fixed automatically"
                : $"uses {current.Compression}, which cannot be re-encoded automatically";

            result.ManualIssues.Add(new DrawablePerformanceIssue
            {
                AddonName = addon.Name,
                Drawable = drawable,
                Message = $"{kind} '{name}' {reason}. Optimize it manually."
            });
            return;
        }

        result.Candidates.Add(new TextureOptimizationCandidate
        {
            AddonName = addon.Name,
            Drawable = drawable,
            Texture = texture,
            TextureName = name,
            Kind = kind,
            Current = current,
            Target = target,
            Reasons = TextureOptimizer.DescribeChanges(current, target)
        });
    }

    private static void CollectManualIssues(Addon addon, GDrawable drawable, AutoOptimizerScanResult result)
    {
        var tooltip = drawable.Details?.Tooltip;
        if (string.IsNullOrEmpty(tooltip))
        {
            return;
        }

        var seen = new HashSet<string>();

        foreach (var line in tooltip.Split('\n'))
        {
            // Texture warnings are already covered by the candidates list. Contains (not equality) because
            // concurrent drawable validations can leave the tooltip line duplicated without a separator.
            var message = line.Trim();
            if (message.Length == 0 || message.Contains(TextureWarningsTooltip) || !seen.Add(message))
            {
                continue;
            }

            result.ManualIssues.Add(new DrawablePerformanceIssue
            {
                AddonName = addon.Name,
                Drawable = drawable,
                Message = message
            });
        }
    }
}
