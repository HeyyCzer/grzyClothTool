namespace grzyClothTool.Optimization;

/// <summary>Size, mip count and CodeWalker format name (e.g. "D3DFMT_DXT5") of a texture.</summary>
public sealed record TextureInfo(int Width, int Height, int MipMapCount, string Compression);

public enum TextureKind
{
    Diffuse,
    Normal,
    Specular
}

/// <summary>
/// Pure rules deciding what a texture should look like to stay within performance limits.
/// Used by the grzyClothTool warnings/auto optimizer and by the standalone CLI.
/// </summary>
public static class TextureRules
{
    public const int MinTextureSize = 4;
    public const string DefaultCompression = "D3DFMT_DXT5";

    /// <summary>
    /// Target compression decided from the pixels at encode time (<see cref="TextureCodec.ResolveCompression"/>):
    /// DXT1 when every pixel is opaque (half the size of DXT5, and DXT1 samples alpha as 1 exactly like an
    /// opaque DXT5), otherwise <see cref="DefaultCompression"/>. Sized as DXT5 in estimates.
    /// </summary>
    public const string AutoCompression = "AUTO";

    // Formats whose alpha channel may turn out to be unused, so re-encoding them can pick DXT1.
    private static readonly HashSet<string> AlphaCapableFormats =
    [
        "D3DFMT_DXT3",
        "D3DFMT_DXT5",
        "D3DFMT_A8R8G8B8",
        "D3DFMT_A8B8G8R8",
        "UNKNOWN",
        "",
    ];

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

    /// <summary>Mip levels grzyClothTool generates: down to 4px on the smaller side.</summary>
    public static int GetCorrectMipMapAmount(int width, int height)
    {
        int size = Math.Min(width, height);
        return (int)Math.Log(size, 2) - 1;
    }

    public static int GetExpectedMipMapCount(int width, int height) => Math.Max(1, GetCorrectMipMapAmount(width, height));

    /// <summary>
    /// Returns what the texture should be optimized to, or null when it has no performance issue
    /// the optimizer can fix (or its format cannot be re-encoded).
    /// </summary>
    public static TextureInfo? ComputeTarget(TextureInfo current, int resolutionLimit)
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

        // Re-encoded anyway: let the encoder drop to DXT1 if the alpha channel turns out to be unused.
        if (AlphaCapableFormats.Contains(current.Compression))
        {
            compression = AutoCompression;
        }
        else if (current.Compression == "D3DFMT_X8R8G8B8")
        {
            compression = "D3DFMT_DXT1"; // no alpha channel at all
        }

        return new TextureInfo(width, height, expectedMipMaps, compression);
    }

    /// <summary>
    /// Target for a texture whose data is truncated (<see cref="TextureCodec.IsTruncated"/>): always re-encoded,
    /// whatever its format, as a power of two within the limit with full mips.
    /// </summary>
    public static TextureInfo ComputeRepairTarget(TextureInfo current, int resolutionLimit)
    {
        resolutionLimit = Math.Max(resolutionLimit, MinTextureSize);
        int width = NearestPowerOfTwo(Math.Max(current.Width, MinTextureSize));
        int height = NearestPowerOfTwo(Math.Max(current.Height, MinTextureSize));
        while (Math.Max(width, height) > resolutionLimit)
        {
            width = Math.Max(MinTextureSize, width / 2);
            height = Math.Max(MinTextureSize, height / 2);
        }

        return new TextureInfo(width, height, GetExpectedMipMapCount(width, height), AutoCompression);
    }

    /// <summary>Human readable reasons for the difference between <paramref name="current"/> and <paramref name="target"/>.</summary>
    public static List<string> DescribeChanges(TextureInfo current, TextureInfo target)
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

        if (IsUncompressed(current.Compression))
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

    public static long EstimateSizeBytes(TextureInfo info) =>
        EstimateSizeBytes(info.Width, info.Height, info.MipMapCount, info.Compression);

    private static long BlockBytes(int width, int height, int blockSize) =>
        (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockSize;
}
