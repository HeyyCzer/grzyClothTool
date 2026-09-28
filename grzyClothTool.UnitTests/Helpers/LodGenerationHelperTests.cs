using grzyClothTool.Helpers;
using grzyClothTool.Optimization.Lods;

namespace grzyClothTool.UnitTests.Helpers;

public class LodGenerationHelperTests
{
    private static LodGeneratorSettings ValidSettings(string blenderPath) => new()
    {
        BlenderPath = blenderPath,
        SollumzMode = LodGenerationHelper.SollumzInstalled,
        MediumRatio = 0.5,
        LowRatio = 0.25,
        Workers = 2
    };

    private static string CreateFakeBlender(TestTempDirectory temp)
    {
        var path = Path.Combine(temp.Path, "blender.exe");
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void CreateOptions_MapsInstalledSollumzAndRatios()
    {
        using var temp = new TestTempDirectory();
        var blender = CreateFakeBlender(temp);

        var options = LodGenerationHelper.CreateOptions(ValidSettings(blender));

        Assert.Equal(blender, options.BlenderPath);
        Assert.Equal(SollumzSource.Installed, options.Sollumz);
        Assert.Null(options.SollumzFolder);
        Assert.Equal(0.5, options.MediumRatio);
        Assert.Equal(0.25, options.LowRatio);
        Assert.Equal(2, options.MaxWorkers);
    }

    [Fact]
    public void CreateOptions_AcceptsBlenderFolder()
    {
        using var temp = new TestTempDirectory();
        CreateFakeBlender(temp);

        // The locator accepts the folder too; the path is passed through as typed.
        var options = LodGenerationHelper.CreateOptions(ValidSettings(temp.Path));

        Assert.Equal(temp.Path, options.BlenderPath);
    }

    [Theory]
    [InlineData(0, 0.25)]
    [InlineData(1, 0.25)]
    [InlineData(0.5, 0)]
    [InlineData(0.25, 0.5)] // Low bigger than Medium
    public void CreateOptions_RejectsInvalidRatios(double medium, double low)
    {
        using var temp = new TestTempDirectory();
        var settings = ValidSettings(CreateFakeBlender(temp));
        settings.MediumRatio = medium;
        settings.LowRatio = low;

        Assert.Throws<ArgumentException>(() => LodGenerationHelper.CreateOptions(settings));
    }

    [Fact]
    public void CreateOptions_RejectsMissingBlender()
    {
        using var temp = new TestTempDirectory();

        var ex = Assert.Throws<ArgumentException>(() =>
            LodGenerationHelper.CreateOptions(ValidSettings(Path.Combine(temp.Path, "missing", "blender.exe"))));
        Assert.Contains("Blender not found", ex.Message);
    }

    [Fact]
    public void CreateOptions_FolderModeRequiresSollumzAddon()
    {
        using var temp = new TestTempDirectory();
        var settings = ValidSettings(CreateFakeBlender(temp));
        settings.SollumzMode = LodGenerationHelper.SollumzFolder;
        settings.SollumzFolder = Path.Combine(temp.Path, "sollumz");
        Directory.CreateDirectory(settings.SollumzFolder);

        Assert.Throws<ArgumentException>(() => LodGenerationHelper.CreateOptions(settings));

        File.WriteAllText(Path.Combine(settings.SollumzFolder, "__init__.py"), "");
        var options = LodGenerationHelper.CreateOptions(settings);

        Assert.Equal(SollumzSource.Folder, options.Sollumz);
        Assert.Equal(settings.SollumzFolder, options.SollumzFolder);
    }

    [Fact]
    public void CreateOptions_ClampsWorkers()
    {
        using var temp = new TestTempDirectory();
        var settings = ValidSettings(CreateFakeBlender(temp));
        settings.Workers = 50;

        Assert.Equal(8, LodGenerationHelper.CreateOptions(settings).MaxWorkers);
    }
}
