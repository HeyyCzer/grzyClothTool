using CodeWalker.GameFiles;
using grzyClothTool.Optimization;

namespace grzyClothTool.UnitTests.Helpers;

public class TexturePreviewTests
{
    // Each mip level is filled with its own colour, so decoding the wrong offset shows up as the wrong colour.
    private static readonly (byte R, byte G, byte B)[] LevelColours =
    [
        (255, 0, 0),
        (0, 255, 0),
        (0, 0, 255),
        (255, 255, 0),
        (0, 255, 255),
        (255, 0, 255),
    ];

    [Fact]
    public void DecodePreview_Uncompressed_DecodesSmallestMipAtLeastMinSize()
    {
        var texture = CreateUncompressed(256, levels: 4);

        Assert.Equal(2, TextureCodec.PickPreviewLevel(texture, 60));

        using var image = TextureCodec.DecodePreview(texture, 60);

        Assert.Equal(64u, image.Width);
        Assert.Equal(64u, image.Height);
        AssertPixel(image, LevelColours[2]);
    }

    [Theory]
    [InlineData(TextureFormat.D3DFMT_DXT1)]
    [InlineData(TextureFormat.D3DFMT_DXT5)]
    public void DecodePreview_BlockCompressed_UsesDdsioMipLayout(TextureFormat format)
    {
        var texture = CreateBlockCompressed(format, 64, levels: 5); // 64, 32, 16, 8, 4

        using var image = TextureCodec.DecodePreview(texture, 16);

        Assert.Equal(16u, image.Width);
        AssertPixel(image, LevelColours[2]);
    }

    [Fact]
    public void DecodePreview_NeverPicksMipsSmallerThanABlock()
    {
        var texture = CreateBlockCompressed(TextureFormat.D3DFMT_DXT1, 64, levels: 6); // ..., 4, 2

        Assert.Equal(4, TextureCodec.PickPreviewLevel(texture, 1));
    }

    [Fact]
    public void DecodePreview_WithoutMips_SmallTexture_DecodesTopLevel()
    {
        var texture = CreateUncompressed(128, levels: 1);

        using var image = TextureCodec.DecodePreview(texture, 100);

        Assert.Equal(128u, image.Width);
        AssertPixel(image, LevelColours[0]);
    }

    [Theory]
    [InlineData(TextureFormat.D3DFMT_A8R8G8B8)]
    [InlineData(TextureFormat.D3DFMT_DXT1)]
    [InlineData(TextureFormat.D3DFMT_DXT5)]
    public void DecodePreview_WithoutMips_SamplesTopLevelDown(TextureFormat format)
    {
        var texture = format == TextureFormat.D3DFMT_A8R8G8B8
            ? CreateUncompressed(256, levels: 1)
            : CreateBlockCompressed(format, 256, levels: 1);

        using var image = TextureCodec.DecodePreview(texture, 64);

        Assert.Equal(64u, image.Width);
        Assert.Equal(64u, image.Height);
        AssertPixel(image, LevelColours[0]);
    }

    [Fact]
    public void TrySampleTopLevel_KeepsQuadrantsInPlace()
    {
        // 64x64 uncompressed: left half red, right half blue. Sampled to 16x16 the halves must stay put.
        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = (y * size + x) * 4;
            pixels[i] = (byte)(x < size / 2 ? 0 : 255);   // B
            pixels[i + 2] = (byte)(x < size / 2 ? 255 : 0); // R
            pixels[i + 3] = 255;
        }
        var texture = new Texture
        {
            Width = size, Height = size, Depth = 1, Levels = 1, Stride = size * 4,
            Format = TextureFormat.D3DFMT_A8R8G8B8, Data = new TextureData { FullData = pixels },
        };

        var sampled = TextureCodec.TrySampleTopLevel(texture, 16)!;

        Assert.Equal(16, sampled.Width);
        Assert.Equal(255, sampled.Data.FullData[2]);                 // (0,0) red
        Assert.Equal(255, sampled.Data.FullData[15 * 4]);            // (15,0) blue
    }

    [Fact]
    public void TryExtractMip_ReturnsNullForTruncatedData()
    {
        var texture = CreateUncompressed(64, levels: 3);
        texture.Data.FullData = texture.Data.FullData[..(64 * 64 * 4 + 10)];

        Assert.Null(TextureCodec.TryExtractMip(texture, 2));
    }

    private static Texture CreateUncompressed(int size, int levels)
    {
        var data = new List<byte>();
        for (int level = 0; level < levels; level++)
        {
            int s = size >> level;
            var (r, g, b) = LevelColours[level];
            for (int i = 0; i < s * s; i++)
            {
                data.AddRange([b, g, r, 255]); // A8R8G8B8 is stored B, G, R, A
            }
        }

        return new Texture
        {
            Name = "test",
            Width = (ushort)size,
            Height = (ushort)size,
            Depth = 1,
            Levels = (byte)levels,
            Stride = (ushort)(size * 4),
            Format = TextureFormat.D3DFMT_A8R8G8B8,
            Data = new TextureData { FullData = data.ToArray() },
        };
    }

    private static Texture CreateBlockCompressed(TextureFormat format, int size, int levels)
    {
        bool dxt5 = format == TextureFormat.D3DFMT_DXT5;
        var data = new List<byte>();
        for (int level = 0; level < levels; level++)
        {
            int blocks = Math.Max(1, (size >> level) / 4);
            var (r, g, b) = LevelColours[level];
            ushort colour = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
            for (int i = 0; i < blocks * blocks; i++)
            {
                if (dxt5)
                {
                    data.AddRange([255, 255, 0, 0, 0, 0, 0, 0]); // alpha block: opaque, all indices 0
                }
                // colour block: color0 == color1 and all indices 0 -> every pixel is color0
                data.AddRange(BitConverter.GetBytes(colour));
                data.AddRange(BitConverter.GetBytes(colour));
                data.AddRange([0, 0, 0, 0]);
            }
        }

        return new Texture
        {
            Name = "test",
            Width = (ushort)size,
            Height = (ushort)size,
            Depth = 1,
            Levels = (byte)levels,
            Stride = (ushort)((size / 4) * (dxt5 ? 16 : 8)),
            Format = format,
            Data = new TextureData { FullData = data.ToArray() },
        };
    }

    private static void AssertPixel(ImageMagick.MagickImage image, (byte R, byte G, byte B) expected)
    {
        var rgb = image.GetPixels().ToByteArray(0, 0, 1, 1, "RGB")!;
        // 565 quantisation loses a few bits per channel.
        Assert.InRange(rgb[0], expected.R - 8, expected.R + 8);
        Assert.InRange(rgb[1], expected.G - 8, expected.G + 8);
        Assert.InRange(rgb[2], expected.B - 8, expected.B + 8);
    }
}
