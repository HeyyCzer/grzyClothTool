using CodeWalker.GameFiles;
using grzyClothTool.Optimization;
using grzyClothTool.Optimization.Lods;

namespace grzyClothTool.UnitTests.Helpers;

public class LodGrafterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"grzyLodTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void FindMissing_ReturnsNothingWhenAllLodsExist()
    {
        Assert.Empty(LodGrafter.FindMissing(LoadReserved()));
    }

    [Fact]
    public void FindMissing_ListsTheMissingLevels()
    {
        var ydd = LoadReserved();
        Drawable(ydd).DrawableModels.Low = null;

        var missing = Assert.Single(LodGrafter.FindMissing(ydd));

        Assert.Equal([LodLevel.Low], missing.Levels);
        Assert.Equal(2, missing.HighTriangles);
    }

    [Fact]
    public void Graft_AddsLodsPointingToTheOriginalShadersWithTheHighSkinBinding()
    {
        var ydd = WithoutLods(LoadReserved());
        var missing = LodGrafter.FindMissing(ydd);
        var generated = SimulateSollumzExport(LoadReserved());
        var notes = new List<string>();

        var changes = LodGrafter.Graft(ydd, missing, generated, notes);

        Assert.Empty(notes);
        Assert.Equal([LodLevel.Medium, LodLevel.Low], changes.Select(c => c.Level));

        // Reload the saved file: what the game would read.
        var saved = new YddFile();
        saved.Load(ydd.Save());
        var models = Drawable(saved).DrawableModels;
        var high = Assert.Single(models.High);
        var med = Assert.Single(models.Med);
        var low = Assert.Single(models.Low);

        // Shader order was reversed in the "export": the geometries must point to the original shaders again.
        Assert.Equal(1, Assert.Single(med.Geometries).ShaderID);
        Assert.Equal(2, Assert.Single(low.Geometries).ShaderID);

        Assert.Equal(high.SkeletonBinding, med.SkeletonBinding);
        Assert.Equal(1, low.HasSkin);
        Assert.Equal(Enumerable.Range(0, 128).Select(i => (ushort)i), low.Geometries[0].BoneIds);
        Assert.Equal(Drawable(saved).RenderMaskHigh, Drawable(saved).RenderMaskLow);
    }

    [Fact]
    public void Graft_SkipsLodsWhoseShaderIsNotInTheOriginal()
    {
        var ydd = WithoutLods(LoadReserved());
        var missing = LodGrafter.FindMissing(ydd);
        var generated = LoadReserved();
        var lowShader = Drawable(generated).ShaderGroup.Shaders.data_items[2];
        lowShader.ParametersList.Parameters.First(p => p.Data is TextureBase).Data = new TextureBase { Name = "something_else" };
        var notes = new List<string>();

        var changes = LodGrafter.Graft(ydd, missing, generated, notes);

        Assert.Empty(changes);
        Assert.Contains(notes, n => n.Contains("changed the shaders"));
        Assert.Null(Drawable(ydd).DrawableModels.Med);
    }

    [Fact]
    public async Task FolderOptimizer_DryRunListsMissingLodsWithoutStartingBlender()
    {
        var input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "jbib_000_u.ydd"), WithoutLods(LoadReserved()).Save());

        var summary = await new FolderOptimizer(new FolderOptimizerOptions
        {
            InputFolder = input,
            OutputFolder = Path.Combine(_root, "out"),
            DryRun = true,
            Lods = new LodGenerationOptions { BlenderPath = Path.Combine(_root, "no-blender.exe") }
        }).RunAsync();

        var file = Assert.Single(summary.Files);
        Assert.Equal(FileOutcome.Optimized, file.Outcome);
        Assert.Equal(2, summary.LodsGenerated);
        Assert.All(file.Lods, lod => Assert.Null(lod.Triangles));
        Assert.False(Directory.Exists(Path.Combine(_root, "out")));
    }

    [Fact]
    public async Task FolderOptimizer_WithoutBlenderKeepsTheFileAndSaysWhy()
    {
        var input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        var bytes = WithoutLods(LoadReserved()).Save();
        File.WriteAllBytes(Path.Combine(input, "jbib_000_u.ydd"), bytes);
        var output = Path.Combine(_root, "out");

        var summary = await new FolderOptimizer(new FolderOptimizerOptions
        {
            InputFolder = input,
            OutputFolder = output,
            Lods = new LodGenerationOptions { BlenderPath = Path.Combine(_root, "no-blender.exe") }
        }).RunAsync();

        var file = Assert.Single(summary.Files);
        Assert.Equal(FileOutcome.Unchanged, file.Outcome);
        Assert.Contains(file.Notes, n => n.Contains("Blender not found"));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(output, "jbib_000_u.ydd")));
    }

    [Fact]
    public async Task FolderOptimizer_SkipsClothesWithPhysics()
    {
        var input = Path.Combine(_root, "in");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "jbib_000_u.ydd"), WithoutLods(LoadReserved()).Save());
        File.WriteAllBytes(Path.Combine(input, "jbib_000_u.yld"), [0]);

        var summary = await new FolderOptimizer(new FolderOptimizerOptions
        {
            InputFolder = input,
            DryRun = true,
            Lods = new LodGenerationOptions()
        }).RunAsync();

        var ydd = summary.Files.Single(f => f.RelativePath.EndsWith(".ydd"));
        Assert.Empty(ydd.Lods);
        Assert.Contains(ydd.Notes, n => n.Contains(".yld"));
    }

    private static YddFile LoadReserved()
    {
        var ydd = new YddFile();
        ydd.Load(File.ReadAllBytes(Path.Combine(FindRepoRoot(), "grzyClothTool", "Resources", "reservedDrawable.ydd")));
        return ydd;
    }

    private static Drawable Drawable(YddFile ydd) => ydd.DrawableDict.Drawables.data_items[0];

    private static YddFile WithoutLods(YddFile ydd)
    {
        var drawable = Drawable(ydd);
        drawable.DrawableModels.Med = null;
        drawable.DrawableModels.Low = null;
        drawable.BuildRenderMasks();
        drawable.BuildAllModels();
        return ydd;
    }

    /// <summary>What Sollumz gives back for ped components: other shader order, no skin binding, no bone table.</summary>
    private static YddFile SimulateSollumzExport(YddFile ydd)
    {
        var drawable = Drawable(ydd);
        var shaders = drawable.ShaderGroup.Shaders.data_items;
        Array.Reverse(shaders);

        foreach (var model in drawable.AllModels)
        {
            model.SkeletonBinding = 0;
            for (int i = 0; i < model.Geometries.Length; i++)
            {
                var geometry = model.Geometries[i];
                geometry.ShaderID = (ushort)(shaders.Length - 1 - geometry.ShaderID);
                geometry.BoneIds = null;
                model.ShaderMapping[i] = geometry.ShaderID;
            }
        }

        return ydd;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "grzyClothTool.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
