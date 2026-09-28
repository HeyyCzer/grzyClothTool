using System.Collections.ObjectModel;
using grzyClothTool.Helpers;
using grzyClothTool.Models;
using grzyClothTool.Models.Drawable;
using grzyClothTool.Models.Texture;
using static grzyClothTool.Enums;

namespace grzyClothTool.UnitTests.Helpers;

public class BuildReporterTests
{
    [Fact]
    public void GetSnapshot_ReportsWeightedPercentage()
    {
        var reporter = new BuildReporter();
        reporter.Start(200);

        reporter.Complete(50);
        reporter.Complete(50);

        var snapshot = reporter.GetSnapshot();
        Assert.Equal(50, snapshot.Percent, precision: 3);
        Assert.Equal(100, snapshot.DoneWork);
        Assert.Equal(2, snapshot.DoneItems);
    }

    [Fact]
    public void GetSnapshot_NeverReaches100BeforeFinish()
    {
        var reporter = new BuildReporter();
        reporter.Start(10);
        reporter.Complete(10);

        Assert.Equal(99, reporter.GetSnapshot().Percent);

        reporter.Finish();

        var finished = reporter.GetSnapshot();
        Assert.Equal(100, finished.Percent);
        Assert.Equal(TimeSpan.Zero, finished.Remaining);
    }

    [Fact]
    public void GetSnapshot_HasNoEtaRightAfterStart()
    {
        var reporter = new BuildReporter();
        reporter.Start(1000);
        reporter.Complete(500);

        Assert.Null(reporter.GetSnapshot().Remaining);
    }

    [Fact]
    public void DrainPending_ReturnsEachLineOnceAndCountsIssues()
    {
        var reporter = new BuildReporter();
        reporter.Start(1);
        reporter.Info("hello");
        reporter.Warning("careful");
        reporter.Error("broken");

        var first = reporter.DrainPending();
        var second = reporter.DrainPending();

        Assert.Contains(first, e => e.Message == "hello");
        Assert.Contains(first, e => e.IsWarning && e.Message.Contains("careful"));
        Assert.Contains(first, e => e.IsError && e.Message.Contains("broken"));
        Assert.Empty(second);

        var snapshot = reporter.GetSnapshot();
        Assert.Equal(1, snapshot.Warnings);
        Assert.Equal(1, snapshot.Errors);
        Assert.Contains("broken", reporter.GetFullLog());
    }

    [Fact]
    public async Task Complete_IsThreadSafe()
    {
        var reporter = new BuildReporter();
        reporter.Start(10_000);

        await Task.WhenAll(Enumerable.Range(0, 10_000).Select(_ => Task.Run(() => reporter.Complete(1, "x"))));

        var snapshot = reporter.GetSnapshot();
        Assert.Equal(10_000, snapshot.DoneWork);
        Assert.Equal(10_000, snapshot.DoneItems);
        Assert.Equal(10_001, reporter.DrainPending().Count); // + "Build started" line
    }

    [Fact]
    public void EstimateWork_WeightsOptimizedAndConvertedTextures()
    {
        var plain = CreateTexture(".ytd", number: 0);
        var optimized = CreateTexture(".ytd", number: 1);
        optimized.IsOptimizedDuringBuild = true;
        var png = CreateTexture(".png", number: 2);

        var drawable = new GDrawable(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ydd"),
            SexType.male,
            isProp: false,
            typeNumeric: 11,
            number: 0,
            hasSkin: false,
            new ObservableCollection<GTexture> { plain, optimized, png });

        var addon = new Addon("test");
        addon.Drawables.Add(drawable);

        long drawableWeight = BuildReporter.WeightCopy;
        Assert.Equal(
            drawableWeight + BuildReporter.WeightCopy + BuildReporter.WeightTextureOptimize + BuildReporter.WeightTextureConvert,
            BuildResourceHelper.EstimateWork([addon], BuildResourceType.FiveM));

        // AltV/Singleplayer copy jpg/png/dds sources as they are.
        Assert.Equal(
            drawableWeight + BuildReporter.WeightCopy + BuildReporter.WeightTextureOptimize + BuildReporter.WeightCopy,
            BuildResourceHelper.EstimateWork([addon], BuildResourceType.AltV));
    }

    private static GTexture CreateTexture(string extension, int number) =>
        new(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}"), 11, 0, number, false, false);
}
