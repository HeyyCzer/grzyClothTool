using CodeWalker.GameFiles;
using CodeWalker.Utils;
using ImageMagick;

namespace grzyClothTool.Optimization;

/// <summary>
/// Converts between CodeWalker textures and ImageMagick images, and encodes optimized DDS data.
/// Every method works on its own objects, so textures can be processed in parallel.
/// </summary>
public static class TextureCodec
{
    public static TextureInfo Describe(Texture texture) =>
        new(texture.Width, texture.Height, texture.Levels, texture.Format.ToString());

    public static MagickImage Decode(Texture texture) =>
        TryReadUncompressedPixels(texture) ?? new MagickImage(DDSIO.GetDDSFile(texture));

    /// <summary>
    /// Reads the top mip level of an uncompressed texture straight from its pixel data, skipping the
    /// DDS container copy and DDS decoding. ImageMagick's DDS reader also ignores the channel masks of
    /// A8B8G8R8 (swapping red and blue), which this path gets right. Returns null for formats/layouts
    /// that need the DDS path.
    /// </summary>
    public static MagickImage? TryReadUncompressedPixels(Texture texture)
    {
        var mapping = texture.Format switch
        {
            TextureFormat.D3DFMT_A8R8G8B8 => "BGRA",
            TextureFormat.D3DFMT_X8R8G8B8 => "BGRP", // P = padding byte, no alpha
            TextureFormat.D3DFMT_A8B8G8R8 => "RGBA",
            _ => null
        };

        var data = texture.Data?.FullData;
        uint topLevelSize = (uint)texture.Width * texture.Height * 4;
        if (mapping == null || texture.Stride != texture.Width * 4 || data == null || data.Length < topLevelSize)
        {
            return null;
        }

        var image = new MagickImage();
        image.ReadPixels(data, 0, topLevelSize, new PixelReadSettings(texture.Width, texture.Height, StorageType.Char, mapping));
        return image;
    }

    /// <summary>
    /// Resizes <paramref name="image"/> to <paramref name="target"/> and encodes it as DDS with the target
    /// compression and a full mip chain. The image is modified in place.
    /// </summary>
    public static byte[] EncodeDds(MagickImage image, TextureInfo target)
    {
        image.Format = MagickFormat.Dds;

        ResizeExact(image, target.Width, target.Height, target.Compression);
        image.Settings.SetDefine(MagickFormat.Dds, "compression", GetCompressionString(target.Compression));
        image.Settings.SetDefine(MagickFormat.Dds, "cluster-fit", true);
        image.Settings.SetDefine(MagickFormat.Dds, "mipmaps", target.MipMapCount);

        // ToByteArray writes straight into one right-sized buffer (a growing MemoryStream allocated
        // several large-object-heap arrays per texture).
        return image.ToByteArray();
    }

    /// <summary>Returns a new texture with the same name as <paramref name="source"/>, optimized to <paramref name="target"/>.</summary>
    public static Texture Optimize(Texture source, TextureInfo target)
    {
        using var image = Decode(source);
        var optimized = DDSIO.GetTexture(EncodeDds(image, target));
        optimized.Name = source.Name;
        optimized.NameHash = source.NameHash != 0 ? source.NameHash : JenkHash.GenHash(source.Name?.ToLowerInvariant() ?? string.Empty);
        return optimized;
    }

    public static string GetCompressionString(string cwCompression) => cwCompression switch
    {
        "D3DFMT_DXT1" => "dxt1",
        "D3DFMT_DXT3" => "dxt3",
        "D3DFMT_DXT5" => "dxt5",
        "D3DFMT_A8R8G8B8" => "none",
        _ => "dxt5",
    };

    private static bool IsBlockCompressed(string cwCompression) => cwCompression switch
    {
        "D3DFMT_DXT1" or "D3DFMT_DXT3" or "D3DFMT_DXT5"
        or "D3DFMT_ATI1" or "D3DFMT_ATI2" or "D3DFMT_BC7" => true,
        _ => false,
    };

    private static int RoundDownToMultipleOf4(int value) => Math.Max(4, value - (value % 4));

    public static void ResizeExact(MagickImage image, int width, int height, string cwCompression)
    {
        if (IsBlockCompressed(cwCompression))
        {
            width = RoundDownToMultipleOf4(width);
            height = RoundDownToMultipleOf4(height);
        }

        var geometry = new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true };
        image.Resize(geometry);
    }
}
