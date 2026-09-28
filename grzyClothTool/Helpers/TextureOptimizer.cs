using grzyClothTool.Models.Texture;
using System;
using System.Collections.Generic;

namespace grzyClothTool.Helpers;
#nullable enable

/// <summary>
/// Pure texture optimization rules shared by the texture warnings (<see cref="GTextureDetails.Validate"/>)
/// and the automatic optimizer. Decides what a texture should look like to stay within the configured limits.
/// </summary>
public static class TextureOptimizer
{
    public const int MinTextureSize = 4;
    public const string DefaultCompression = "D3DFMT_DXT5";

    private static readonly HashSet<string> UncompressedFormats =
    [
        "D3DFMT_A8R8G8B8",
        "D3DFMT_X8R8G8B8",
        "D3DFMT_A8B8G8R8",
    ];

    // Formats ImageMagick can write back to (UNKNOWN/empty = jpg/png sources, converted during build).
    private static readonly HashSet<string> AutoOptimizableFormats =
    [
        "D3DFMT_DXT1",
        "D3DFMT_DXT3",
        "D3DFMT_DXT5",
        "D3DFMT_A8R8G8B8",
        "D3DFMT_X8R8G8B8",
        "D3DFMT_A8B8G8R8",
        "UNKNOWN",
        "",
    ];

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

    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    public static bool IsUncompressed(string? compression) => compression != null && UncompressedFormats.Contains(compression);

    /// <summary>
    /// Whether the optimizer can safely re-encode this format. Formats like ATI2 (BC5 normal maps)
    /// or BC7 cannot be written back by ImageMagick, so re-encoding them would change their layout.
    /// </summary>
    public static bool CanAutoOptimize(string? compression) => AutoOptimizableFormats.Contains(compression ?? string.Empty);

    /// <summary>
    /// Rounds to the closest power of two (ties go up), so small overshoots like 1030px become 1024
    /// instead of being upscaled to 2048.
    /// </summary>
    public static int NearestPowerOfTwo(int value)
    {
        if (value <= MinTextureSize)
        {
            return MinTextureSize;
        }

        int upper = 1;
        while (upper < value)
        {
            upper <<= 1;
        }

        int lower = upper >> 1;
        return value - lower < upper - value ? lower : upper;
    }

    public static int GetExpectedMipMapCount(int width, int height) => Math.Max(1, ImgHelper.GetCorrectMipMapAmount(width, height));

    /// <summary>
    /// Returns the details the texture should be optimized to, or null when the texture has no
    /// performance warning the optimizer can fix.
    /// </summary>
    public static GTextureDetails? ComputeTarget(GTextureDetails current, int resolutionLimit)
    {
        if (current.Width <= 0 || current.Height <= 0 || !CanAutoOptimize(current.Compression))
        {
            return null;
        }

        resolutionLimit = Math.Max(resolutionLimit, MinTextureSize);

        int width = NearestPowerOfTwo(current.Width);
        int height = NearestPowerOfTwo(current.Height);

        // Halve both sides to keep the aspect ratio intact.
        while (Math.Max(width, height) > resolutionLimit)
        {
            width = Math.Max(MinTextureSize, width / 2);
            height = Math.Max(MinTextureSize, height / 2);
        }

        var compression = IsUncompressed(current.Compression) ? DefaultCompression : current.Compression;
        var expectedMipMaps = GetExpectedMipMapCount(width, height);

        bool resized = width != current.Width || height != current.Height;
        bool recompressed = compression != current.Compression;
        bool missingMipMaps = current.MipMapCount <= 1 && expectedMipMaps > 1;

        if (!resized && !recompressed && !missingMipMaps)
        {
            return null;
        }

        return new GTextureDetails
        {
            Width = width,
            Height = height,
            MipMapCount = expectedMipMaps,
            Compression = compression,
            Name = current.Name,
            Type = current.Type,
            IsOptimizeNeeded = false
        };
    }

    /// <summary>
    /// Human readable reasons for the difference between <paramref name="current"/> and <paramref name="target"/>.
    /// </summary>
    public static List<string> DescribeChanges(GTextureDetails current, GTextureDetails target)
    {
        var reasons = new List<string>();

        if (!IsPowerOfTwo(current.Width) || !IsPowerOfTwo(current.Height))
        {
            reasons.Add("Not power of 2");
        }

        if (target.Width < current.Width || target.Height < current.Height)
        {
            reasons.Add("Downscale");
        }

        if (target.Compression != current.Compression)
        {
            reasons.Add("Compress");
        }

        if (current.MipMapCount <= 1 && target.MipMapCount > 1)
        {
            reasons.Add("Generate mip maps");
        }

        return reasons;
    }

    /// <summary>
    /// Approximate GPU memory used by a texture including its mip chain.
    /// Unknown formats (jpg/png sources) are estimated as DXT5, which is what the build produces.
    /// </summary>
    public static long EstimateSizeBytes(int width, int height, int mipMapCount, string? compression)
    {
        long total = 0;
        int levels = Math.Max(1, mipMapCount);

        for (int i = 0; i < levels; i++)
        {
            int w = Math.Max(1, width >> i);
            int h = Math.Max(1, height >> i);

            total += compression switch
            {
                "D3DFMT_DXT1" or "D3DFMT_ATI1" => BlockBytes(w, h, 8),
                "D3DFMT_A8R8G8B8" or "D3DFMT_X8R8G8B8" or "D3DFMT_A8B8G8R8" => (long)w * h * 4,
                "D3DFMT_A1R5G5B5" => (long)w * h * 2,
                "D3DFMT_A8" or "D3DFMT_L8" => (long)w * h,
                _ => BlockBytes(w, h, 16),
            };
        }

        return total;
    }

    public static long EstimateSizeBytes(GTextureDetails details) =>
        EstimateSizeBytes(details.Width, details.Height, details.MipMapCount, details.Compression);

    private static long BlockBytes(int width, int height, int blockSize) =>
        (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockSize;
}
