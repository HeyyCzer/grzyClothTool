using CodeWalker.GameFiles;
using grzyClothTool.Optimization;

namespace grzyClothTool.UnitTests.Helpers;

public class FolderOptimizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"grzyOptimizerTests_{Guid.NewGuid():N}");
    private readonly string _input;

    public FolderOptimizerTests()
    {
        _input = Path.Combine(_root, "resource");
        Directory.CreateDirectory(Path.Combine(_input, "stream", "sub"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("mp_f_freemode_01^hair_001_u.ydd", true)]
    [InlineData("mp_m_freemode_01_clothes_x_01^hair_diff_025_a_uni.ytd", true)]
    [InlineData("mp_fm_body_hair_001.ytd", true)]
    [InlineData("mp_f_freemode_01_mp_f_gunrunning_hair_01^jbib_000_u.ydd", false)]
    [InlineData("mp_f_freemode_01^jbib_diff_000_a_uni.ytd", false)]
    [InlineData("mp_f_freemode_01^berd_000_u.ydd", false)]
    public void IsHairFile_MatchesHairComponentsAndOverlaysOnly(string name, bool expected)
    {
        Assert.Equal(expected, FolderOptimizer.IsHairFile(Path.Combine("stream", name)));
    }

    [Theory]
    [InlineData("mp_f_freemode_01^berd_002_u.ydd", "mp_f_freemode_01^berd_diff_002_a_uni.ytd", true)]
    [InlineData("mp_f_freemode_01^berd_002_r.ydd", "mp_f_freemode_01^berd_diff_002_b_whi.ytd", true)]
    [InlineData("mp_m_freemode_01_x_01^hats_010.ydd", "mp_m_freemode_01_x_01^hats_diff_010_a.ytd", true)]
    [InlineData("p_head_000.ydd", "p_head_diff_000_a.ytd", true)]
    [InlineData("mp_f_freemode_01^berd_002_u.ydd", "mp_f_freemode_01^berd_diff_003_a_uni.ytd", false)]
    [InlineData("mp_f_freemode_01^berd_002_u.ydd", "mp_f_freemode_01^jbib_diff_002_a_uni.ytd", false)]
    [InlineData("mp_f_freemode_01^berd_002_u.ydd", "mp_m_freemode_01^berd_diff_002_a_uni.ytd", false)]
    public void DrawableKey_PairsAModelWithItsTextures(string model, string textures, bool expected)
    {
        var modelKey = FolderOptimizer.DrawableKey(Path.Combine(_input, "stream", model));
        var textureKey = FolderOptimizer.DrawableKey(Path.Combine(_input, "stream", textures));

        Assert.NotNull(modelKey);
        Assert.NotNull(textureKey);
        Assert.Equal(expected, string.Equals(modelKey, textureKey, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DrawableKey_KeepsFoldersApart()
    {
        Assert.NotEqual(
            FolderOptimizer.DrawableKey(Path.Combine(_input, "a", "x^berd_002_u.ydd")),
            FolderOptimizer.DrawableKey(Path.Combine(_input, "b", "x^berd_diff_002_a_uni.ytd")));
    }

    [Theory]
    [InlineData(32, 32)]
    [InlineData(64, 16)]
    [InlineData(16, 128)]
    [InlineData(8, 8)]
    public void EncodeDds_WritesExactlyTheTargetMipChain(int width, int height)
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, (uint)width, (uint)height);
        var target = new TextureInfo(width, height, TextureRules.GetExpectedMipMapCount(width, height), "D3DFMT_DXT5");

        var texture = CodeWalker.Utils.DDSIO.GetTexture(TextureCodec.EncodeDds(image, target));

        Assert.Equal(target.MipMapCount, texture.Levels);
        int last = texture.Levels - 1;
        Assert.True(Math.Min(width >> last, height >> last) >= 4, "last mip smaller than a DXT block");
    }

    [Fact]
    public async Task RunAsync_CopiesHairUnchangedUnlessIncluded()
    {
        var hairPath = Path.Combine(_input, "stream", "mp_f_freemode_01^hair_diff_000_a_uni.ytd");
        WriteUncompressedYtd(hairPath, "hair_diff_000_a_uni", size: 64);
        var originalBytes = File.ReadAllBytes(hairPath);
        var output = Path.Combine(_root, "out");

        var skipped = await new FolderOptimizer(new FolderOptimizerOptions
        {
            InputFolder = _input,
            OutputFolder = output,
            DiffuseLimit = 32
        }).RunAsync();

        Assert.Equal(1, skipped.Count(FileOutcome.Excluded));
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(output, "stream", "mp_f_freemode_01^hair_diff_000_a_uni.ytd")));

        var included = await new FolderOptimizer(new FolderOptimizerOptions
        {
            InputFolder = _input,
            DiffuseLimit = 32,
            DryRun = true,
            SkipHair = false
        }).RunAsync();

        Assert.Equal(1, included.Count(FileOutcome.Optimized));
    }

    [Fact]
    public async Task RunAsync_WritesOptimizedCopyAndLeavesInputUntouched()
    {
        var ytdPath = Path.Combine(_input, "stream", "sub", "jbib_diff_000_a_uni.ytd");
        WriteUncompressedYtd(ytdPath, "jbib_diff_000_a_uni", size: 64);
        File.WriteAllText(Path.Combine(_input, "fxmanifest.lua"), "fx_version 'cerulean'");
        var originalBytes = File.ReadAllBytes(ytdPath);
        var output = Path.Combine(_root, "out");

        var summary = await new FolderOptimizer(new FolderOptimizerOptions
        {
            InputFolder = _input,
            OutputFolder = output,
            DiffuseLimit = 32
        }).RunAsync();

        Assert.Equal(1, summary.Count(FileOutcome.Optimized));
        Assert.Equal(1, summary.Count(FileOutcome.Copied));
        Assert.Equal(originalBytes, File.ReadAllBytes(ytdPath));
        Assert.True(File.Exists(Path.Combine(output, "fxmanifest.lua")));

        var texture = ReadSingleTexture(Path.Combine(output, "stream", "sub", "jbib_diff_000_a_uni.ytd"));
        Assert.Equal("jbib_diff_000_a_uni", texture.Name);
        Assert.Equal(32, texture.Width);
        Assert.Equal(32, texture.Height);
        Assert.Equal(TextureFormat.D3DFMT_DXT5, texture.Format);
        // Mip chain stops at 4x4 (smallest DXT block); 32x32 -> 32, 16, 8, 4.
        Assert.Equal(TextureRules.GetExpectedMipMapCount(32, 32), texture.Levels);
        Assert.Equal(4, texture.Levels);
    }

    [Fact]
    public async Task RunAsync_InPlaceReplacesTheFile()
    {
        var ytdPath = Path.Combine(_input, "tex.ytd");
        WriteUncompressedYtd(ytdPath, "tex", size: 64);

        await new FolderOptimizer(new FolderOptimizerOptions { InputFolder = _input, DiffuseLimit = 1024 }).RunAsync();

        var texture = ReadSingleTexture(ytdPath);
        Assert.Equal(64, texture.Width);
        Assert.Equal(TextureFormat.D3DFMT_DXT5, texture.Format);
        Assert.Empty(Directory.GetFiles(_input, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RunAsync_DryRunWritesNothing()
    {
        var ytdPath = Path.Combine(_input, "tex.ytd");
        WriteUncompressedYtd(ytdPath, "tex", size: 64);
        var originalBytes = File.ReadAllBytes(ytdPath);
        var output = Path.Combine(_root, "out");

        var summary = await new FolderOptimizer(new FolderOptimizerOptions
        {
            InputFolder = _input,
            OutputFolder = output,
            DryRun = true
        }).RunAsync();

        Assert.Equal(1, summary.TexturesOptimized);
        Assert.Equal(originalBytes, File.ReadAllBytes(ytdPath));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task RunAsync_KeepsUnreadableTextureFiles()
    {
        File.WriteAllBytes(Path.Combine(_input, "broken.ytd"), [1, 2, 3, 4]);
        var output = Path.Combine(_root, "out");

        var summary = await new FolderOptimizer(new FolderOptimizerOptions { InputFolder = _input, OutputFolder = output }).RunAsync();

        Assert.Equal(1, summary.Count(FileOutcome.Skipped));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(output, "broken.ytd")));
    }

    [Fact]
    public async Task RunAsync_RealDrawableStaysLoadable()
    {
        var source = Path.Combine(FindRepoRoot(), "grzyClothTool", "Resources", "reservedDrawable.ydd");
        File.Copy(source, Path.Combine(_input, "reserved.ydd"));
        var output = Path.Combine(_root, "out");

        var summary = await new FolderOptimizer(new FolderOptimizerOptions
        {
            InputFolder = _input,
            OutputFolder = output,
            DiffuseLimit = 4,
            NormalLimit = 4,
            SpecularLimit = 4
        }).RunAsync();

        Assert.Equal(0, summary.Count(FileOutcome.Failed));
        var ydd = new YddFile();
        ydd.Load(File.ReadAllBytes(Path.Combine(output, "reserved.ydd")));
        Assert.NotEmpty(ydd.Drawables);
    }

    [Theory]
    [InlineData("jbib_diff_000_a_uni", TextureKind.Diffuse)]
    [InlineData("helmet000_n", TextureKind.Normal)]
    [InlineData("helmet000_s", TextureKind.Specular)]
    [InlineData("body_normal", TextureKind.Normal)]
    public void ClassifyByName_FollowsGtaNaming(string name, TextureKind expected)
    {
        Assert.Equal(expected, FolderOptimizer.ClassifyByName(name));
    }

    private static void WriteUncompressedYtd(string path, string name, int size)
    {
        var pixels = new byte[size * size * 4];
        Random.Shared.NextBytes(pixels);

        var texture = new Texture
        {
            Name = name,
            NameHash = JenkHash.GenHash(name),
            Width = (ushort)size,
            Height = (ushort)size,
            Depth = 1,
            Levels = 1,
            Stride = (ushort)(size * 4),
            Format = TextureFormat.D3DFMT_A8R8G8B8,
            Data = new TextureData { FullData = pixels }
        };

        var ytd = new YtdFile { TextureDict = new TextureDictionary() };
        ytd.TextureDict.BuildFromTextureList([texture]);
        File.WriteAllBytes(path, ytd.Save());
    }

    private static Texture ReadSingleTexture(string path)
    {
        var ytd = new YtdFile();
        ytd.Load(File.ReadAllBytes(path));
        return Assert.Single(ytd.TextureDict.Textures.data_items);
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
