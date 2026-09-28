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
        // ImageMagick's "dds:mipmaps" counts levels below the base image, so the file holds one level more
        // than MipMapCount (down to 2x2). grzyClothTool builds have always produced the same chain.
        Assert.Equal(TextureRules.GetExpectedMipMapCount(32, 32) + 1, texture.Levels);
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
