using CodeWalker.GameFiles;
using grzyClothTool.Optimization.Lods;

namespace grzyClothTool.UnitTests.Helpers;

public class LodValidatorTests
{
    [Fact]
    public void Validate_AcceptsAValidDrawable()
    {
        var issues = LodValidator.Validate(LoadReserved());

        Assert.DoesNotContain(issues, i => i.Severity == LodIssueSeverity.Error);
    }

    [Fact]
    public void Validate_FlagsIndicesPastTheLastVertex()
    {
        var ydd = LoadReserved();
        var geometry = Geometry(ydd, d => d.DrawableModels.Med);
        geometry.IndexBuffer.Indices[0] = (ushort)geometry.VertexData.VertexCount;

        var issue = Assert.Single(Errors(ydd));

        Assert.Equal("Medium", issue.Level);
        Assert.Contains("past the last vertex", issue.Message);
    }

    [Fact]
    public void Validate_FlagsVertexDataShorterThanItsDeclaration()
    {
        var ydd = LoadReserved();
        var data = Geometry(ydd, d => d.DrawableModels.Low).VertexData;
        data.VertexBytes = data.VertexBytes[..^data.Info.Stride];

        Assert.Contains(Errors(ydd), i => i.Level == "Low" && i.Message.Contains("vertex data is"));
    }

    [Fact]
    public void Validate_FlagsWeightedBlendIndicesPastTheBoneTable()
    {
        var ydd = LoadReserved();
        var geometry = Geometry(ydd, d => d.DrawableModels.Med);
        geometry.BoneIds = [0];

        Assert.Contains(Errors(ydd), i => i.Level == "Medium" && i.Message.Contains("past its bone table"));
    }

    [Fact]
    public void Validate_OnlyWarnsAboutSkinningOfLodsNotGenerated()
    {
        var ydd = LoadReserved();
        Geometry(ydd, d => d.DrawableModels.Med).BoneIds = [0];

        var issues = LodValidator.Validate(ydd);

        Assert.DoesNotContain(issues, i => i.Severity == LodIssueSeverity.Error);
        Assert.Contains(issues, i => i.Message.Contains("past its bone table"));
    }

    [Fact]
    public void Validate_FlagsLodsSkinnedToBonesTheHighModelDoesNotUse()
    {
        var ydd = LoadReserved();
        var geometry = Geometry(ydd, d => d.DrawableModels.Med);
        geometry.BoneIds = geometry.BoneIds.Select(b => (ushort)(b + 500)).ToArray();

        Assert.Contains(Errors(ydd), i => i.Level == "Medium" && i.Message.Contains("bones the High model does not use"));
    }

    [Fact]
    public void Validate_FlagsALayoutMissingComponentsOfTheHighGeometry()
    {
        var ydd = LoadReserved();
        var geometry = Geometry(ydd, d => d.DrawableModels.Low);
        var high = Geometry(ydd, d => d.DrawableModels.High);
        geometry.ShaderID = high.ShaderID;
        RemoveComponent(geometry, VertexSemantics.Normal);

        Assert.Contains(Errors(ydd), i => i.Level == "Low" && i.Message.Contains("lacks Normal"));
    }

    [Fact]
    public void Graft_RejectsALodThatFailsValidation()
    {
        var ydd = LoadReserved();
        var drawable = ydd.DrawableDict.Drawables.data_items[0];
        drawable.DrawableModels.Med = null;
        drawable.DrawableModels.Low = null;
        var missing = LodGrafter.FindMissing(ydd);
        var generated = LoadReserved();
        var broken = Geometry(generated, d => d.DrawableModels.Med);
        broken.IndexBuffer.Indices[0] = ushort.MaxValue;
        var notes = new List<string>();

        var changes = LodGrafter.Graft(ydd, missing, generated, notes);

        Assert.Equal([LodLevel.Low], changes.Select(c => c.Level));
        Assert.Contains(notes, n => n.Contains("Medium LOD failed validation"));
        Assert.Null(drawable.DrawableModels.Med);
    }

    /// <summary>Medium and Low are treated as generated, so skinning problems are errors.</summary>
    private static List<LodIssue> Errors(YddFile ydd)
    {
        var name = LodGrafter.Describe(ydd)[0].Name;
        return LodValidator.Validate(ydd, new HashSet<(string, string)> { (name, "Medium"), (name, "Low") }).Where(i => i.Severity == LodIssueSeverity.Error).ToList();
    }

    private static DrawableGeometry Geometry(YddFile ydd, Func<Drawable, DrawableModel[]> level) =>
        level(ydd.DrawableDict.Drawables.data_items[0])[0].Geometries[0];

    /// <summary>Drops a vertex component consistently (declaration, stride and data), like an exporter leaving it out.</summary>
    private static void RemoveComponent(DrawableGeometry geometry, VertexSemantics semantic)
    {
        var data = geometry.VertexData;
        var oldInfo = data.Info;
        int offset = oldInfo.GetComponentOffset((int)semantic);
        int size = VertexComponentTypes.GetSizeInBytes(oldInfo.GetComponentType((int)semantic));

        var info = new VertexDeclaration { Flags = oldInfo.Flags & ~(1u << (int)semantic), Types = oldInfo.Types };
        info.UpdateCountAndStride();

        var bytes = new byte[data.VertexCount * info.Stride];
        for (int v = 0; v < data.VertexCount; v++)
        {
            int from = v * oldInfo.Stride, to = v * info.Stride;
            Buffer.BlockCopy(data.VertexBytes, from, bytes, to, offset);
            Buffer.BlockCopy(data.VertexBytes, from + offset + size, bytes, to + offset, oldInfo.Stride - offset - size);
        }

        data.Info = info;
        data.VertexStride = info.Stride;
        data.VertexBytes = bytes;
        geometry.VertexBuffer.Info = info;
        geometry.VertexBuffer.VertexStride = info.Stride;
    }

    private static YddFile LoadReserved()
    {
        var ydd = new YddFile();
        ydd.Load(File.ReadAllBytes(Path.Combine(FindRepoRoot(), "grzyClothTool", "Resources", "reservedDrawable.ydd")));
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
