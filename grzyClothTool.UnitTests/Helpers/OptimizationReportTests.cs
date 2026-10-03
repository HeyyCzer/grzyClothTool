using grzyClothTool.Optimization;
using grzyClothTool.Optimization.Lods;

namespace grzyClothTool.UnitTests.Helpers;

public class OptimizationReportTests
{
    private static readonly OptimizationReportOptions Options = new()
    {
        Settings = [new("Input", @"C:\pack"), new("Max diffuse", "1024px")],
        HighTriangleLimit = 10000
    };

    [Fact]
    public void BuildReview_NothingToReview()
    {
        var summary = Summary(
            new FileResult("fxmanifest.lua", FileOutcome.Copied, [], [], 10, 10),
            Ydd("stream/light.ydd", FileOutcome.Unchanged, new DrawableStats("light", 5000, 2500, 1200, 0)));

        var report = OptimizationReport.BuildReview(summary, Options);

        Assert.Contains(@"Input:", report);
        Assert.Contains(@"C:\pack", report);
        Assert.Contains("Nothing needs manual review.", report);
    }

    [Fact]
    public void BuildReview_ListsEverythingThatNeedsAPerson()
    {
        var summary = Summary(
            new FileResult("stream/broken.ytd", FileOutcome.Skipped, [], ["could not be read (bad header); kept as-is"], 4, 4),
            new FileResult("stream/crash.ydd", FileOutcome.Failed, [], [], 4, 4, "boom"),
            Ydd("stream/heavy.ydd", FileOutcome.Unchanged, new DrawableStats("heavy", 42000, 0, 0, 0)),
            new FileResult("stream/bc7.ytd", FileOutcome.Unchanged, [], ["tex (4096x4096 BC7) exceeds 1024px but its format cannot be re-encoded"], 4, 4));

        var report = OptimizationReport.BuildReview(summary, Options);

        Assert.Contains("5 item(s) to review.", report);
        Assert.Contains("stream/crash.ydd", report);
        Assert.Contains("error: boom", report);
        Assert.Contains("stream/broken.ytd", report);
        Assert.Contains("42000 tris  stream/heavy.ydd", report);
        Assert.Contains("stream/heavy.ydd: no Medium/Low LOD", report);
        Assert.Contains("cannot be re-encoded", report);
    }

    [Fact]
    public void BuildReview_GeneratedLodsAreNotMissing()
    {
        var summary = Summary(Ydd("stream/top.ydd", FileOutcome.Optimized, new DrawableStats("top", 8000, 0, 0, 0),
            new LodChange("top", LodLevel.Medium, 8000, 4000), new LodChange("top", LodLevel.Low, 8000, 2000)));

        var report = OptimizationReport.BuildReview(summary, Options);

        Assert.Contains("Nothing needs manual review.", report);
    }

    [Fact]
    public void BuildReview_LodsOfAFailedFileAreStillMissing()
    {
        var file = Ydd("stream/top.ydd", FileOutcome.Failed, new DrawableStats("top", 8000, 0, 0, 0),
            new LodChange("top", LodLevel.Medium, 8000, 4000)) with { Error = "save failed" };

        var report = OptimizationReport.BuildReview(Summary(file), Options);

        Assert.Contains("stream/top.ydd: no Medium/Low LOD", report);
    }

    [Fact]
    public void BuildLog_HasSettingsEveryFileAndSummary()
    {
        var before = new TextureInfo(2048, 2048, 1, "D3DFMT_A8R8G8B8");
        var after = new TextureInfo(1024, 1024, 11, "D3DFMT_DXT5");
        var summary = Summary(
            new FileResult("fxmanifest.lua", FileOutcome.Copied, [], [], 10, 10),
            new FileResult("stream/a.ytd", FileOutcome.Optimized,
                [new TextureChange("a_diff", TextureKind.Diffuse, before, after, ["resized"])], [], 4 << 20, 1 << 20));

        var log = OptimizationReport.BuildLog(summary, Options with { Cancelled = true });

        Assert.Contains("CANCELLED", log);
        Assert.Contains("[COPIED] fxmanifest.lua", log);
        Assert.Contains("[OPTIMIZED] stream/a.ytd  4 MB -> 1 MB", log);
        Assert.Contains("texture a_diff [Diffuse] 2048x2048 A8R8G8B8 (1 mips) -> 1024x1024 DXT5 (11 mips)", log);
        Assert.Contains("SUMMARY", log);
        Assert.True(log.IndexOf("Max diffuse", StringComparison.Ordinal) < log.IndexOf("FILES", StringComparison.Ordinal));
    }

    private static FolderOptimizationSummary Summary(params FileResult[] files) => new(files, TimeSpan.FromSeconds(3));

    private static FileResult Ydd(string path, FileOutcome outcome, DrawableStats drawable, params LodChange[] lods) =>
        new(path, outcome, [], [], 100, 100, null, lods, [drawable]);
}
