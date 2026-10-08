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
    /// <summary>D3DFMT_A16B16G16R16 (16 bits per channel), missing from CodeWalker's <see cref="TextureFormat"/>.</summary>
    public const TextureFormat A16B16G16R16 = (TextureFormat)36;

    public static TextureInfo Describe(Texture texture) =>
        new(texture.Width, texture.Height, texture.Levels, texture.Format.ToString());

    public static MagickImage Decode(Texture texture) =>
        IsTruncated(texture)
            ? DecodeTruncated(texture)
            : TryReadUncompressedPixels(texture) ?? new MagickImage(DDSIO.GetDDSFile(texture));

    /// <summary>
    /// True when the data is shorter than the top mip level its size and format need. The game hands the buffer to
    /// the GPU driver as is, and the driver reads past its end: a crash in the NVIDIA driver (memcpy) as soon as the
    /// item is worn, or garbage when the memory after it happens to be mapped. Seen in packs as A16B16G16R16
    /// textures with data sized for 1 byte per pixel. Only the top level is checked: mips under a 4x4 block stored
    /// as 4 or 1 bytes instead of a whole block are common in working packs.
    /// </summary>
    public static bool IsTruncated(Texture texture)
    {
        var data = texture.Data?.FullData;
        long needed = GetTopLevelSizeAnyFormat(texture.Format, texture.Width, texture.Height);
        return data != null && needed > 0 && data.Length < needed;
    }

    /// <summary>
    /// Decodes the complete rows of the top level that a truncated texture does have, stretched back to its size
    /// (the rows the GPU read past the end were garbage anyway). A16B16G16R16 is converted here because DDSIO
    /// cannot read it; formats that cannot be read at all become mid grey.
    /// </summary>
    public static MagickImage DecodeTruncated(Texture texture)
    {
        var data = texture.Data?.FullData ?? [];
        int width = texture.Width;
        int height = texture.Height;
        MagickImage? image = null;

        if (texture.Format == A16B16G16R16)
        {
            // Little-endian 16-bit R, G, B, A per pixel; keep the high byte of each channel.
            int rows = Math.Min(height, data.Length / (width * 8));
            if (rows > 0)
            {
                var rgba = new byte[width * rows * 4];
                for (int i = 0; i < rgba.Length; i++)
                {
                    rgba[i] = data[i * 2 + 1];
                }
                image = new MagickImage();
                image.ReadPixels(rgba, new PixelReadSettings((uint)width, (uint)rows, StorageType.Char, "RGBA"));
            }
        }
        else if (GetLevelSize(texture.Format, width, height, out int rowPitch) > 0)
        {
            bool blockCompressed = IsBlockCompressed(texture.Format.ToString());
            int rowHeight = blockCompressed ? 4 : 1;
            int rows = Math.Min((height + rowHeight - 1) / rowHeight, data.Length / rowPitch);
            if (rows > 0)
            {
                var top = new byte[rows * rowPitch];
                Buffer.BlockCopy(data, 0, top, 0, top.Length);
                image = Decode(new Texture
                {
                    Name = texture.Name,
                    Width = (ushort)width,
                    Height = (ushort)Math.Min(height, rows * rowHeight),
                    Depth = 1,
                    Levels = 1,
                    Stride = (ushort)rowPitch,
                    Format = texture.Format,
                    Data = new TextureData { FullData = top },
                });
            }
        }

        image ??= new MagickImage(MagickColors.Gray, 4, 4);
        image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        return image;
    }

    /// <summary>Bytes of the top level for every format seen in GTA textures, 0 when unknown.</summary>
    private static long GetTopLevelSizeAnyFormat(TextureFormat format, int width, int height)
    {
        long size = GetLevelSize(format, width, height, out _);
        if (size > 0)
        {
            return size;
        }

        int bytesPerPixel = (int)format switch
        {
            34 or 112 or 114 => 4,  // G16R16, G16R16F, R32F
            36 or 113 or 115 => 8,  // A16B16G16R16, A16B16G16R16F, G32R32F
            116 => 16,              // A32B32G32R32F
            111 => 2,               // R16F
            _ => 0
        };
        return (long)Math.Max(1, width) * Math.Max(1, height) * bytesPerPixel;
    }

    /// <summary>
    /// Decodes the smallest mip level that is still at least <paramref name="minSize"/> pixels on its longer
    /// side, for previews and thumbnails. A 90px thumbnail of a 4096² texture then decodes a 128² mip (~16 KB)
    /// instead of the full 64 MB image plus its DDS copies. Falls back to the top level when the texture has
    /// no usable mips or an unknown format.
    /// </summary>
    public static MagickImage DecodePreview(Texture texture, int minSize)
    {
        int level = PickPreviewLevel(texture, minSize);
        var small = level > 0 ? TryExtractMip(texture, level) : TrySampleTopLevel(texture, minSize);
        return Decode(small ?? texture);
    }

    /// <summary>
    /// For textures without (usable) mips: builds a smaller texture by taking every n-th 4x4 block (block
    /// formats) or pixel (uncompressed) of the top level, so a preview doesn't decode e.g. a full 4096² DXT1
    /// (~1s in ImageMagick). Nearest-neighbour quality, fine for thumbnails. Returns null when the texture is
    /// already small enough or the format/layout isn't handled.
    /// </summary>
    public static Texture? TrySampleTopLevel(Texture texture, int minSize)
    {
        var data = texture.Data?.FullData;
        int width = texture.Width;
        int height = texture.Height;
        int step = Math.Max(width, height) / Math.Max(1, minSize);
        if (data == null || step < 2)
        {
            return null;
        }

        if (GetLevelSize(texture.Format, width, height, out int tightPitch) <= 0)
        {
            return null;
        }

        bool blockCompressed = IsBlockCompressed(texture.Format.ToString());
        int unit = blockCompressed ? 4 : 1; // pixels per sampled unit along each axis
        int unitsWide = (width + unit - 1) / unit;
        int unitsHigh = (height + unit - 1) / unit;
        int unitBytes = tightPitch / unitsWide;
        // Uncompressed rows may be padded (Stride); block rows are tightly packed.
        int rowPitch = !blockCompressed && texture.Stride > tightPitch ? texture.Stride : tightPitch;
        if ((long)rowPitch * unitsHigh > data.Length)
        {
            return null;
        }
        int outUnitsWide = Math.Max(1, unitsWide / step);
        int outUnitsHigh = Math.Max(1, unitsHigh / step);

        var pixels = new byte[outUnitsWide * outUnitsHigh * unitBytes];
        for (int y = 0; y < outUnitsHigh; y++)
        {
            int srcRow = y * step * rowPitch;
            for (int x = 0; x < outUnitsWide; x++)
            {
                Buffer.BlockCopy(data, srcRow + x * step * unitBytes, pixels, (y * outUnitsWide + x) * unitBytes, unitBytes);
            }
        }

        return new Texture
        {
            Name = texture.Name,
            NameHash = texture.NameHash,
            Width = (ushort)(outUnitsWide * unit),
            Height = (ushort)(outUnitsHigh * unit),
            Depth = 1,
            Stride = (ushort)(outUnitsWide * unitBytes),
            Format = texture.Format,
            Levels = 1,
            Data = new TextureData { FullData = pixels },
        };
    }

    /// <summary>
    /// Crops away the fully transparent border of a preview image. Clothing textures are often atlases with the
    /// garment in one corner (e.g. 2048x4096 with the content in the bottom-left), which made thumbnails look
    /// empty. Works on the alpha channel only: transparent DXT5 pixels keep arbitrary RGB, so a colour-based
    /// trim wouldn't find the border. Does nothing for opaque images or when nothing is visible.
    /// </summary>
    public static void TrimTransparentBorder(MagickImage image)
    {
        if (!image.HasAlpha)
        {
            return;
        }

        using var alpha = (MagickImage)image.Separate(Channels.Alpha).First();
        alpha.ColorFuzz = new Percentage(2);
        var box = alpha.BoundingBox;
        var corner = alpha.GetPixels().GetPixel(0, 0).GetChannel(0);
        if (corner > Quantum.Max / 50 || box == null || box.Width == 0 || box.Height == 0
            || (box.Width == image.Width && box.Height == image.Height))
        {
            return; // opaque border, nothing visible, or nothing to trim
        }

        image.Crop(box);
        image.ResetPage();
    }

    public static int PickPreviewLevel(Texture texture, int minSize)
    {
        int level = 0;
        bool blockCompressed = IsBlockCompressed(texture.Format.ToString());
        while (level + 1 < texture.Levels)
        {
            int w = texture.Width >> (level + 1);
            int h = texture.Height >> (level + 1);
            if (Math.Max(w, h) < minSize || (blockCompressed && (w < 4 || h < 4)))
            {
                break;
            }
            level++;
        }
        return level;
    }

    /// <summary>
    /// Returns a single-level texture holding mip <paramref name="level"/> of <paramref name="texture"/>, or null
    /// for level 0 / unsupported formats / truncated data. Mips are stored tightly packed one after another,
    /// the same layout DDSIO reads.
    /// </summary>
    public static Texture? TryExtractMip(Texture texture, int level)
    {
        var data = texture.Data?.FullData;
        if (level <= 0 || level >= texture.Levels || data == null)
        {
            return null;
        }

        int offset = 0;
        for (int i = 0; i < level; i++)
        {
            int size = GetLevelSize(texture.Format, texture.Width >> i, texture.Height >> i, out _);
            if (size <= 0) return null;
            offset += size;
        }

        int width = texture.Width >> level;
        int height = texture.Height >> level;
        int levelSize = GetLevelSize(texture.Format, width, height, out int rowPitch);
        if (levelSize <= 0 || offset + levelSize > data.Length)
        {
            return null;
        }

        var pixels = new byte[levelSize];
        Buffer.BlockCopy(data, offset, pixels, 0, levelSize);
        return new Texture
        {
            Name = texture.Name,
            NameHash = texture.NameHash,
            Width = (ushort)width,
            Height = (ushort)height,
            Depth = 1,
            Stride = (ushort)rowPitch,
            Format = texture.Format,
            Levels = 1,
            Data = new TextureData { FullData = pixels },
        };
    }

    private static int GetLevelSize(TextureFormat format, int width, int height, out int rowPitch)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        int blockBytes = format switch
        {
            TextureFormat.D3DFMT_DXT1 or TextureFormat.D3DFMT_ATI1 => 8,
            TextureFormat.D3DFMT_DXT3 or TextureFormat.D3DFMT_DXT5 or TextureFormat.D3DFMT_ATI2 or TextureFormat.D3DFMT_BC7 => 16,
            _ => 0
        };
        if (blockBytes > 0)
        {
            int blocksWide = Math.Max(1, (width + 3) / 4);
            int blocksHigh = Math.Max(1, (height + 3) / 4);
            rowPitch = blocksWide * blockBytes;
            return rowPitch * blocksHigh;
        }

        int bytesPerPixel = format switch
        {
            TextureFormat.D3DFMT_A8R8G8B8 or TextureFormat.D3DFMT_X8R8G8B8 or TextureFormat.D3DFMT_A8B8G8R8 => 4,
            TextureFormat.D3DFMT_A1R5G5B5 => 2,
            TextureFormat.D3DFMT_A8 or TextureFormat.D3DFMT_L8 => 1,
            _ => 0
        };
        rowPitch = width * bytesPerPixel;
        return rowPitch * height;
    }

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
    /// compression and <see cref="TextureInfo.MipMapCount"/> levels in total. The image is modified in place.
    /// </summary>
    public static byte[] EncodeDds(MagickImage image, TextureInfo target)
    {
        image.Format = MagickFormat.Dds;

        var compression = ResolveCompression(image, target.Compression);
        if (compression == "D3DFMT_DXT1" && image.HasAlpha)
        {
            // Opaque (or explicitly DXT1): drop the channel so the encoder never emits 1-bit-alpha blocks.
            image.HasAlpha = false;
        }

        ResizeExact(image, target.Width, target.Height, compression);
        image.Settings.SetDefine(MagickFormat.Dds, "compression", GetCompressionString(compression));
        image.Settings.SetDefine(MagickFormat.Dds, "cluster-fit", true);
        // "dds:mipmaps" counts the levels below the base image. Passing the total made every texture one level
        // longer than intended, ending in 2x2/1x1 mips smaller than a DXT block.
        image.Settings.SetDefine(MagickFormat.Dds, "mipmaps", Math.Max(0, target.MipMapCount - 1));

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

    /// <summary>
    /// Turns <see cref="TextureRules.AutoCompression"/> into DXT1 when no pixel has alpha below fully opaque
    /// (checked before resizing, so filtering can't introduce partial alpha), otherwise DXT5. Strict on
    /// purpose: a texture with even one translucent pixel keeps its alpha.
    /// </summary>
    public static string ResolveCompression(MagickImage image, string cwCompression)
    {
        if (cwCompression != TextureRules.AutoCompression)
        {
            return cwCompression;
        }

        return !image.HasAlpha || image.IsOpaque ? "D3DFMT_DXT1" : TextureRules.DefaultCompression;
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
        "D3DFMT_DXT1" or "D3DFMT_DXT3" or "D3DFMT_DXT5" or TextureRules.AutoCompression
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
