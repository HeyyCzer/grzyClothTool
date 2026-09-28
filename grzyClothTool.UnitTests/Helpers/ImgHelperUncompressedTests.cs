using CodeWalker.GameFiles;
using grzyClothTool.Helpers;

namespace grzyClothTool.UnitTests.Helpers;

public class ImgHelperUncompressedTests
{
    // Every pixel is stored as the bytes [10, 80, 160, 240]; what they mean depends on the format.
    // Expected values follow the D3D definitions (and the channel masks CodeWalker writes in DDS headers).
    // ImageMagick's DDS reader ignores those masks for A8B8G8R8 and swaps red/blue, which is why the
    // reference is spelled out here instead of decoding through DDS.
    [Theory]
    [InlineData(TextureFormat.D3DFMT_A8R8G8B8, new byte[] { 160, 80, 10, 240 })] // bytes are B, G, R, A
    [InlineData(TextureFormat.D3DFMT_X8R8G8B8, new byte[] { 160, 80, 10, 255 })] // bytes are B, G, R, X
    [InlineData(TextureFormat.D3DFMT_A8B8G8R8, new byte[] { 10, 80, 160, 240 })] // bytes are R, G, B, A
    public void GetImage_ReadsUncompressedYtdChannelsInFormatOrder(TextureFormat format, byte[] expectedRgba)
    {
        const int size = 4;
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 10;
            pixels[i + 1] = 80;
            pixels[i + 2] = 160;
            pixels[i + 3] = 240;
        }

        var texture = new Texture
        {
            Name = "test",
            NameHash = JenkHash.GenHash("test"),
            Width = size,
            Height = size,
            Depth = 1,
            Levels = 1,
            Stride = size * 4,
            Format = format,
            Data = new TextureData { FullData = pixels }
        };

        var ytd = new YtdFile { TextureDict = new TextureDictionary() };
        ytd.TextureDict.BuildFromTextureList([texture]);

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ytd");
        File.WriteAllBytes(path, ytd.Save());
        try
        {
            using var image = ImgHelper.GetImage(path);

            Assert.Equal((uint)size, image.Width);
            Assert.Equal((uint)size, image.Height);
            Assert.Equal(expectedRgba, image.GetPixels().ToByteArray(0, 0, 1, 1, "RGBA"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
