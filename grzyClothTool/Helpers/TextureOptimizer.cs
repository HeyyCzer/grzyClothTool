using grzyClothTool.Models.Texture;
using grzyClothTool.Optimization;
using System;
using System.Collections.Generic;

namespace grzyClothTool.Helpers;
#nullable enable

/// <summary>
/// Texture optimization rules for the texture warnings (<see cref="GTextureDetails.Validate"/>) and the
/// automatic optimizer. The rules themselves live in <see cref="TextureRules"/> (shared with the standalone
/// grzyOptimizer CLI); this adapts them to <see cref="GTextureDetails"/> and the user's settings.
/// </summary>
public static class TextureOptimizer
{
    public const int MinTextureSize = TextureRules.MinTextureSize;
    public const string DefaultCompression = TextureRules.DefaultCompression;

    public static int GetResolutionLimit(string? type)
    {
        if (type != null)
        {
            if (type.Contains("diffuse", StringComparison.OrdinalIgnoreCase))
            {
                return SettingsHelper.Instance.TextureResolutionLimitDiffuse;
            }
            if (type.Contains("normal", StringComparison.OrdinalIgnoreCase))
            {
                return SettingsHelper.Instance.TextureResolutionLimitNormal;
            }
            if (type.Contains("specular", StringComparison.OrdinalIgnoreCase))
            {
                return SettingsHelper.Instance.TextureResolutionLimitSpecular;
            }
        }

        return 2048;
    }

    public static bool IsPowerOfTwo(int value) => TextureRules.IsPowerOfTwo(value);

    public static bool IsUncompressed(string? compression) => TextureRules.IsUncompressed(compression);

    /// <inheritdoc cref="TextureRules.CanAutoOptimize"/>
    public static bool CanAutoOptimize(string? compression) => TextureRules.CanAutoOptimize(compression);

    /// <inheritdoc cref="TextureRules.NearestPowerOfTwo"/>
    public static int NearestPowerOfTwo(int value) => TextureRules.NearestPowerOfTwo(value);

    public static int GetExpectedMipMapCount(int width, int height) => TextureRules.GetExpectedMipMapCount(width, height);

    /// <summary>
    /// Returns the details the texture should be optimized to, or null when the texture has no
    /// performance warning the optimizer can fix.
    /// </summary>
    public static GTextureDetails? ComputeTarget(GTextureDetails current, int resolutionLimit)
    {
        var target = TextureRules.ComputeTarget(ToInfo(current), resolutionLimit);
        if (target == null)
        {
            return null;
        }

        return new GTextureDetails
        {
            Width = target.Width,
            Height = target.Height,
            MipMapCount = target.MipMapCount,
            Compression = target.Compression,
            Name = current.Name,
            Type = current.Type,
            IsOptimizeNeeded = false
        };
    }

    /// <inheritdoc cref="TextureRules.DescribeChanges"/>
    public static List<string> DescribeChanges(GTextureDetails current, GTextureDetails target) =>
        TextureRules.DescribeChanges(ToInfo(current), ToInfo(target));

    /// <inheritdoc cref="TextureRules.EstimateSizeBytes(int, int, int, string?)"/>
    public static long EstimateSizeBytes(int width, int height, int mipMapCount, string? compression) =>
        TextureRules.EstimateSizeBytes(width, height, mipMapCount, compression);

    public static long EstimateSizeBytes(GTextureDetails details) => TextureRules.EstimateSizeBytes(ToInfo(details));

    private static TextureInfo ToInfo(GTextureDetails details) =>
        new(details.Width, details.Height, details.MipMapCount, details.Compression ?? string.Empty);
}
