using grzyClothTool.Shared.Updates;

namespace grzyClothTool.UnitTests.Helpers;

public class ReleaseUpdatesTests
{
    [Theory]
    [InlineData("1.5.1", "1.5.0", true)]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.5.0", "1.5.0", false)]
    [InlineData("1.5.0", "1.5.0.0", false)]
    [InlineData("1.5", "1.5.0", false)]
    [InlineData("1.4.9", "1.5.0", false)]
    public void IsNewer_ComparesVersionsNumerically(string latest, string current, bool expected)
    {
        Assert.Equal(expected, ReleaseUpdates.IsNewer(latest, current));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void IsNewer_NoLatestVersion_IsFalse(string? latest)
    {
        Assert.False(ReleaseUpdates.IsNewer(latest, "1.5.0"));
    }

    [Fact]
    public void IsNewer_UnparsableCurrentVersion_UpdatesWhenDifferent()
    {
        Assert.True(ReleaseUpdates.IsNewer("1.5.0", null));
        Assert.True(ReleaseUpdates.IsNewer("1.5.0", "dev"));
    }

    [Fact]
    public void AssetUrl_PointsAtTheVersionTag()
    {
        Assert.Equal("https://github.com/heyyczer/grzyClothTool/releases/download/v1.5.0/grzyOptimizer.zip",
            ReleaseUpdates.AssetUrl("1.5.0", "grzyOptimizer.zip"));
    }
}
