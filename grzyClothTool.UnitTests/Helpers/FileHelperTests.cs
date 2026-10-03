using grzyClothTool.Helpers;
using grzyClothTool.Constants;

namespace grzyClothTool.UnitTests.Helpers;

public class FileHelperTests
{
    [Fact]
    public async Task CopyAsync_CopiesFileAndReadAllBytesAsyncReadsContents()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.FilePath("source.ydd");
        var destinationPath = temp.FilePath("nested", "copy.ydd");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var expectedBytes = new byte[] { 1, 7, 3, 9, 5 };
        await File.WriteAllBytesAsync(sourcePath, expectedBytes);

        await FileHelper.CopyAsync(sourcePath, destinationPath);

        Assert.True(File.Exists(destinationPath));
        Assert.Equal(expectedBytes, await FileHelper.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public async Task CopyAsync_FailsWhenDestinationAlreadyExists()
    {
        using var temp = new TestTempDirectory();
        var sourcePath = temp.FilePath("source.ydd");
        var destinationPath = temp.FilePath("copy.ydd");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        await File.WriteAllBytesAsync(destinationPath, [9, 9, 9]);

        await Assert.ThrowsAsync<IOException>(() => FileHelper.CopyAsync(sourcePath, destinationPath));

        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(destinationPath));
    }

    [Theory]
    [InlineData("jbib_012_u.ydd", 12)]
    [InlineData("jbib_012_u_1.ydd", 12)]
    [InlineData("lowr_127_r.yld", 127)]
    [InlineData("JBIB_001_U.YDD", 1)]
    [InlineData("p_head_009.ydd", 9)]
    [InlineData("not-a-drawable.ydd", null)]
    // Built-resource names (as produced by the tool's own build output) must parse to the same
    // number, so re-importing a built resource keeps every item at its original index.
    [InlineData("mp_m_freemode_01_myproject^jbib_005_u.ydd", 5)]
    [InlineData("mp_m_freemode_01_myproject^jbib_005_u_1.ydd", 5)]
    [InlineData("mp_f_freemode_01_myproject^accs_010_r.ydd", 10)]
    [InlineData("mp_m_freemode_01_p_myproject^p_head_003.ydd", 3)]
    [InlineData("mp_m_freemode_01_myproject^lowr_007_u.yld", 7)]
    public void GetDrawableNumberFromFileName_ParsesSupportedDrawableNames(string fileName, int? expected)
    {
        Assert.Equal(expected, FileHelper.GetDrawableNumberFromFileName(fileName));
    }

    [Theory]
    [InlineData("jbib_000_u.ydd", false, 11)]
    [InlineData("addon^jbib_000_u.ydd", false, 11)]
    [InlineData(@"C:\tmp\mp_m_freemode_01^lowr_010_r.ydd", false, 4)]
    [InlineData("p_head_000.ydd", true, 0)]
    [InlineData("addon^p_eyes_003.ydd", true, 1)]
    [InlineData("unknown_000_u.ydd", null, null)]
    public void TryResolveDrawableTypeFromFileName_UsesComponentAndPropPrefixes(
        string fileName,
        bool? expectedIsProp,
        int? expectedDrawableType)
    {
        var result = FileHelper.TryResolveDrawableTypeFromFileName(fileName);

        if (expectedIsProp is null)
        {
            Assert.Null(result);
            return;
        }

        Assert.NotNull(result);
        Assert.Equal(expectedIsProp.Value, result.Value.IsProp);
        Assert.Equal(expectedDrawableType!.Value, result.Value.DrawableType);
    }

    [Fact]
    public void FindMatchingTextures_ReturnsComponentTexturesWithMatchingTypeAndNumber()
    {
        using var temp = new TestTempDirectory();
        var drawablePath = temp.FilePath("jbib_005_u.ydd");
        Touch(drawablePath);
        var firstTexture = temp.FilePath("jbib_diff_005_a_uni.ytd");
        var secondTexture = temp.FilePath("jbib_diff_005_b_uni.ytd");
        Touch(firstTexture);
        Touch(secondTexture);
        Touch(temp.FilePath("jbib_diff_006_a_uni.ytd"));
        Touch(temp.FilePath("lowr_diff_005_a_uni.ytd"));

        var matches = FileHelper.FindMatchingTextures(drawablePath, "jbib", isProp: false);

        Assert.Equal([firstTexture, secondTexture], matches.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void FindMatchingTextures_ReturnsAddonPrefixedTextures()
    {
        using var temp = new TestTempDirectory();
        var drawablePath = temp.FilePath("shop^jbib_005_u.ydd");
        Touch(drawablePath);
        var texturePath = temp.FilePath("shop^jbib_diff_005_a_uni.ytd");
        Touch(texturePath);
        Touch(temp.FilePath("other^jbib_diff_005_a_uni.ytd"));

        var matches = FileHelper.FindMatchingTextures(drawablePath, "jbib", isProp: false);

        Assert.Single(matches);
        Assert.Equal(texturePath, matches[0]);
    }

    [Fact]
    public void FindMatchingTextures_EscapesAddonPrefixRegexCharacters()
    {
        using var temp = new TestTempDirectory();
        var drawablePath = temp.FilePath("shop.v1^jbib_005_u.ydd");
        Touch(drawablePath);
        var texturePath = temp.FilePath("shop.v1^jbib_diff_005_a_uni.ytd");
        Touch(texturePath);
        Touch(temp.FilePath("shopXv1^jbib_diff_005_a_uni.ytd"));

        var matches = FileHelper.FindMatchingTextures(drawablePath, "jbib", isProp: false);

        Assert.Single(matches);
        Assert.Equal(texturePath, matches[0]);
    }

    [Fact]
    public void FindMatchingTextures_ReturnsSimpleNameTexturesForNonStandardDrawableNames()
    {
        using var temp = new TestTempDirectory();
        var drawablePath = temp.FilePath("5.ydd");
        Touch(drawablePath);
        var directTexture = temp.FilePath("5.ytd");
        var raceTexture = temp.FilePath("5_r.ytd");
        Touch(directTexture);
        Touch(raceTexture);
        Touch(temp.FilePath("15.ytd"));

        var matches = FileHelper.FindMatchingTextures(drawablePath, "ignored", isProp: false);

        Assert.Equal([directTexture, raceTexture], matches.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void ResolveFilePath_ReturnsExistingAbsolutePathUnchanged()
    {
        using var temp = new TestTempDirectory();
        var filePath = temp.FilePath("asset.ydd");
        Touch(filePath);

        Assert.Equal(filePath, FileHelper.ResolveFilePath(filePath));
    }

    [Fact]
    public void ResolveFilePath_ReturnsOriginalRelativePathWhenItCannotResolve()
    {
        FileHelper.ClearLoadContext();

        Assert.Equal("missing.ydd", FileHelper.ResolveFilePath("missing.ydd"));
    }

    [Fact]
    public void ResolveFilePath_ReturnsEmptyPathUnchanged()
    {
        Assert.Equal(string.Empty, FileHelper.ResolveFilePath(string.Empty));
    }

    [Fact]
    public void FindMatchingTextures_ReturnsPropTexturesWithMatchingAnchorAndNumber()
    {
        using var temp = new TestTempDirectory();
        var drawablePath = temp.FilePath("p_head_003.ydd");
        Touch(drawablePath);
        var texturePath = temp.FilePath("p_head_diff_003_a.ytd");
        Touch(texturePath);
        Touch(temp.FilePath("p_eyes_diff_003_a.ytd"));

        var matches = FileHelper.FindMatchingTextures(drawablePath, "p_head", isProp: true);

        Assert.Single(matches);
        Assert.Equal(texturePath, matches[0]);
    }

    [Fact]
    public void ResolveFilePath_UsesLoadContextProjectAssetsForRelativePath()
    {
        using var temp = new TestTempDirectory();
        var projectFolder = temp.FilePath("Project");
        var assetsFolder = Path.Combine(projectFolder, GlobalConstants.ASSETS_FOLDER_NAME);
        Directory.CreateDirectory(assetsFolder);
        var assetPath = Path.Combine(assetsFolder, "drawable.ydd");
        Touch(assetPath);
        var saveFilePath = Path.Combine(projectFolder, SaveHelper.AutoSaveFileName);
        Touch(saveFilePath);

        FileHelper.SetLoadContext(saveFilePath);
        try
        {
            Assert.Equal(assetPath, FileHelper.ResolveFilePath("drawable.ydd"));
        }
        finally
        {
            FileHelper.ClearLoadContext();
        }
    }

    [Fact]
    public void IsUnchangedSinceLoadedSave_ComparesWithSaveFileWriteTime()
    {
        using var temp = new TestTempDirectory();
        var older = temp.FilePath("older.ytd");
        var newer = temp.FilePath("newer.ytd");
        var saveFilePath = temp.FilePath(SaveHelper.AutoSaveFileName);
        Touch(older);
        Touch(saveFilePath);
        Touch(newer);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(saveFilePath, DateTime.UtcNow.AddMinutes(-1));

        Assert.False(FileHelper.IsUnchangedSinceLoadedSave(older)); // outside of a load

        FileHelper.SetLoadContext(saveFilePath);
        try
        {
            Assert.True(FileHelper.IsUnchangedSinceLoadedSave(older));
            Assert.False(FileHelper.IsUnchangedSinceLoadedSave(newer));
            Assert.False(FileHelper.IsUnchangedSinceLoadedSave(temp.FilePath("missing.ytd")));
        }
        finally
        {
            FileHelper.ClearLoadContext();
        }
    }

    [Fact]
    public void LoadingSave_ReusesStoredDetailsOfUnchangedFiles()
    {
        using var temp = new TestTempDirectory();
        var ytdPath = temp.FilePath("texture.ytd");
        var yddPath = temp.FilePath("drawable.ydd");
        var saveFilePath = temp.FilePath(SaveHelper.AutoSaveFileName);
        Touch(ytdPath); // not valid files: reusing the details means they are never read
        Touch(yddPath);
        Touch(saveFilePath);
        File.SetLastWriteTimeUtc(ytdPath, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(yddPath, DateTime.UtcNow.AddMinutes(-2));

        var json = $$"""
        {
          "Id": "{{Guid.NewGuid()}}", "FilePath": {{System.Text.Json.JsonSerializer.Serialize(yddPath)}},
          "Sex": 0, "IsProp": false, "TypeNumeric": 11, "Number": 0, "HasSkin": false, "IsLoading": true,
          "Details": {
            "AllModels": { "High": { "PolyCount": 4242 }, "Med": null, "Low": null },
            "EmbeddedTextures": {
              "Specular": { "OriginalName": "specular", "HasOriginalTexture": true, "SourceDrawablePath": "C:\\moved\\away.ydd",
                            "Details": { "Width": 512, "Height": 512, "MipMapCount": 10, "Compression": "D3DFMT_DXT1", "Name": "specular", "Type": "Specular" } }
            }
          },
          "Textures": [{
            "Id": "{{Guid.NewGuid()}}", "FilePath": {{System.Text.Json.JsonSerializer.Serialize(ytdPath)}},
            "TypeNumeric": 11, "Number": 0, "TxtNumber": 0, "HasSkin": false, "IsProp": false, "IsLoading": true,
            "TxtDetails": { "Width": 1234, "Height": 1234, "MipMapCount": 1, "Compression": "D3DFMT_DXT5", "Name": "t", "Type": "diffuse" }
          }]
        }
        """;

        FileHelper.SetLoadContext(saveFilePath);
        grzyClothTool.Models.Drawable.GDrawable drawable;
        try
        {
            drawable = System.Text.Json.JsonSerializer.Deserialize<grzyClothTool.Models.Drawable.GDrawable>(json, SaveHelper.SerializerOptions)!;
        }
        finally
        {
            FileHelper.ClearLoadContext();
        }

        Assert.False(drawable.IsLoading);
        Assert.Equal(4242, drawable.Details.AllModels[grzyClothTool.Models.Drawable.GDrawableDetails.DetailLevel.High]!.PolyCount);
        Assert.Equal(yddPath, drawable.Details.EmbeddedTextures[grzyClothTool.Models.Drawable.GDrawableDetails.EmbeddedTextureType.Specular]!.SourceDrawablePath);

        var texture = Assert.Single(drawable.Textures);
        Assert.False(texture.IsLoading);
        Assert.Equal(1234, texture.TxtDetails.Width);
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }
}
