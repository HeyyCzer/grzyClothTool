using grzyClothTool.Helpers;
using grzyClothTool.Models.Texture;

namespace grzyClothTool.UnitTests.Helpers;

public class TextureOptimizerTests
{
    private static GTextureDetails Details(int width, int height, int mipMaps, string compression = "D3DFMT_DXT5", string type = "diffuse") => new()
    {
        Width = width,
        Height = height,
        MipMapCount = mipMaps,
        Compression = compression,
        Type = type
    };

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(300, 256)]
    [InlineData(384, 512)]
    [InlineData(1030, 1024)]
    [InlineData(1500, 1024)]
    [InlineData(1600, 2048)]
    [InlineData(2048, 2048)]
    public void NearestPowerOfTwo_RoundsToClosestPowerOfTwo(int value, int expected)
    {
        Assert.Equal(expected, TextureOptimizer.NearestPowerOfTwo(value));
    }

    [Fact]
    public void ComputeTarget_ReturnsNullForTextureWithoutWarnings()
    {
        var current = Details(1024, 1024, TextureOptimizer.GetExpectedMipMapCount(1024, 1024));

        Assert.Null(TextureOptimizer.ComputeTarget(current, 1024));
    }

    [Fact]
    public void ComputeTarget_DownscalesToLimitKeepingAspectRatio()
    {
        var target = TextureOptimizer.ComputeTarget(Details(4096, 2048, 11), 1024);

        Assert.NotNull(target);
        Assert.Equal(1024, target.Width);
        Assert.Equal(512, target.Height);
        Assert.Equal(TextureOptimizer.GetExpectedMipMapCount(1024, 512), target.MipMapCount);
        Assert.Equal(TextureOptimizer.AutoCompression, target.Compression);
    }

    [Fact]
    public void ComputeTarget_FixesNonPowerOfTwoWithoutUpscaling()
    {
        var target = TextureOptimizer.ComputeTarget(Details(1030, 1000, 9), 2048);

        Assert.NotNull(target);
        Assert.Equal(1024, target.Width);
        Assert.Equal(1024, target.Height);
    }

    [Fact]
    public void ComputeTarget_GeneratesMissingMipMaps()
    {
        var target = TextureOptimizer.ComputeTarget(Details(512, 512, 1, "D3DFMT_DXT1"), 1024);

        Assert.NotNull(target);
        Assert.Equal(512, target.Width);
        Assert.Equal("D3DFMT_DXT1", target.Compression);
        Assert.Equal(TextureOptimizer.GetExpectedMipMapCount(512, 512), target.MipMapCount);
    }

    [Fact]
    public void ComputeTarget_CompressesUncompressedTextures()
    {
        var target = TextureOptimizer.ComputeTarget(Details(512, 512, 8, "D3DFMT_A8R8G8B8"), 1024);

        Assert.NotNull(target);
        Assert.Equal(TextureOptimizer.AutoCompression, target.Compression);
        Assert.Contains("Compress", TextureOptimizer.DescribeChanges(Details(512, 512, 8, "D3DFMT_A8R8G8B8"), target));
    }

    [Theory]
    [InlineData("D3DFMT_ATI2")]
    [InlineData("D3DFMT_BC7")]
    public void ComputeTarget_SkipsFormatsThatCannotBeReencoded(string compression)
    {
        Assert.Null(TextureOptimizer.ComputeTarget(Details(4096, 4096, 1, compression, "normal"), 1024));
    }

    [Fact]
    public void ComputeTarget_KeepsVeryWideTexturesAboveMinimumSize()
    {
        var target = TextureOptimizer.ComputeTarget(Details(4096, 4, 1), 1024);

        Assert.NotNull(target);
        Assert.Equal(1024, target.Width);
        Assert.Equal(TextureOptimizer.MinTextureSize, target.Height);
    }

    [Fact]
    public void ComputeTarget_TargetPassesValidation()
    {
        var target = TextureOptimizer.ComputeTarget(Details(3000, 1500, 1, "D3DFMT_A8R8G8B8"), 1024)!;

        var check = Details(target.Width, target.Height, target.MipMapCount, target.Compression);
        check.Validate();

        Assert.False(check.IsOptimizeNeeded, check.IsOptimizeNeededTooltip);
    }

    [Fact]
    public void EstimateSizeBytes_UsesFormatBitsPerPixel()
    {
        Assert.Equal(1024 * 1024 / 2, TextureOptimizer.EstimateSizeBytes(1024, 1024, 1, "D3DFMT_DXT1"));
        Assert.Equal(1024 * 1024, TextureOptimizer.EstimateSizeBytes(1024, 1024, 1, "D3DFMT_DXT5"));
        Assert.Equal(1024 * 1024 * 4, TextureOptimizer.EstimateSizeBytes(1024, 1024, 1, "D3DFMT_A8R8G8B8"));
    }

    [Fact]
    public void EstimateSizeBytes_IncludesMipChain()
    {
        long single = TextureOptimizer.EstimateSizeBytes(1024, 1024, 1, "D3DFMT_DXT5");
        long withMips = TextureOptimizer.EstimateSizeBytes(1024, 1024, 9, "D3DFMT_DXT5");

        Assert.True(withMips > single);
        Assert.True(withMips < single * 4 / 3 + 1024);
    }

    [Fact]
    public void Validate_WarnsForUncompressedTexture()
    {
        var details = Details(512, 512, 8, "D3DFMT_A8R8G8B8");

        details.Validate();

        Assert.True(details.IsOptimizeNeeded);
        Assert.Contains("uncompressed", details.IsOptimizeNeededTooltip);
    }
}
