using CodeWalker.GameFiles;

namespace grzyClothTool.UnitTests.Helpers;

public class ResourceDecompressTests
{
    [Theory]
    [InlineData(0)]     // exact size from the header
    [InlineData(-100)]  // header announces less than there is
    [InlineData(100)]   // header announces more than there is
    public void Decompress_WithExpectedSize_MatchesUnsizedDecompress(int sizeError)
    {
        var original = new byte[200_000];
        new Random(42).NextBytes(original.AsSpan(0, 50_000)); // mix of random and compressible data
        var compressed = ResourceBuilder.Compress(original);

        var result = ResourceBuilder.Decompress(compressed, original.Length + sizeError);

        Assert.Equal(original, result);
    }

    [Fact]
    public void Decompress_WithoutExpectedSize_FallsBackToStreaming()
    {
        var original = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();

        Assert.Equal(original, ResourceBuilder.Decompress(ResourceBuilder.Compress(original), 0));
    }
}
