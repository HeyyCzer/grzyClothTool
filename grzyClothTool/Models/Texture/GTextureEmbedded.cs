using CodeWalker.GameFiles;
using CodeWalker.Utils;
using grzyClothTool.Helpers;
using grzyClothTool.Views;
using grzyClothTool.Optimization;
using ImageMagick;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace grzyClothTool.Models.Texture;

#nullable enable

public class GTextureEmbedded : INotifyPropertyChanged
{
    private readonly SemaphoreSlim _textureDataSemaphore = new(1, 1);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OriginalName { get; set; }
    public bool HasOriginalTexture { get; set; }
    public string? SourceDrawablePath { get; set; }

    public GTextureDetails Details { get; set; } = new GTextureDetails();
    public GTextureDetails? OptimizeDetails { get; set; }

    [JsonIgnore]
    public CodeWalker.GameFiles.Texture? TextureData { get; set; }

    private CodeWalker.GameFiles.Texture? _replacementTextureData;
    [JsonIgnore]
    public CodeWalker.GameFiles.Texture? ReplacementTextureData
    {
        get => _replacementTextureData;
        set
        {
            _replacementTextureData = value;
            
            if (value != null)
            {
                IsOptimizedDuringBuild = false;
                OptimizeDetails = null;
            }
            
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReplacement));
            OnPropertyChanged(nameof(DisplayTextureData));
            OnPropertyChanged(nameof(IsOptimizedDuringBuild));
            OnPropertyChanged(nameof(IsPreviewDisabled));
        }
    }

    /// <summary>
    /// Relative path (in project assets) to the replacement image the user chose. Serialized so
    /// the replacement survives save/load - the in-memory <see cref="ReplacementTextureData"/> is
    /// [JsonIgnore] and is rebuilt from this file on demand via <see cref="EnsureReplacementLoaded"/>.
    /// </summary>
    public string? ReplacementFilePath { get; set; }

    [JsonIgnore]
    public bool HasReplacement => _replacementTextureData != null || !string.IsNullOrEmpty(ReplacementFilePath);

    [JsonIgnore]
    public CodeWalker.GameFiles.Texture? DisplayTextureData => _replacementTextureData ?? TextureData;

    private bool _isOptimizedDuringBuild;
    public bool IsOptimizedDuringBuild
    {
        get => _isOptimizedDuringBuild;
        set
        {
            _isOptimizedDuringBuild = value;
            OnPropertyChanged();
        }
    }

    private BitmapSource? _imageThumbnail;
    [JsonIgnore]
    public BitmapSource? ImageThumbnail
    {
        get => _imageThumbnail;
        set
        {
            if (_imageThumbnail != value)
            {
                _imageThumbnail = value;
                OnPropertyChanged(nameof(ImageThumbnail));
            }
        }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            _isLoading = value;
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    /// <summary>
    /// Set once a thumbnail was decoded. The thumbnail no longer keeps the full texture data in memory
    /// (that held every viewed embedded texture with all its mips for the whole session), so this is what
    /// tells the UI the texture can be previewed.
    /// </summary>
    private bool _hasPreviewableData;

    public bool IsPreviewDisabled => !_hasPreviewableData && (DisplayTextureData?.Data?.FullData == null || DisplayTextureData.Data.FullData.Length == 0);

    [JsonIgnore]
    public string PreviewDisabledTooltip => IsPreviewDisabled ? "Encrypted drawable" : string.Empty;

    // Parameterless constructor for JSON deserialization
    public GTextureEmbedded()
    {
        OriginalName = string.Empty;
        Details = new GTextureDetails();
    }

    public GTextureEmbedded(CodeWalker.GameFiles.Texture? textureData, string type, string? sourceDrawablePath = null, bool keepTextureData = false)
    {
        SourceDrawablePath = sourceDrawablePath;
        TextureData = keepTextureData ? textureData : CreateMetadataOnlyTexture(textureData);
        HasOriginalTexture = textureData != null;

        if (textureData == null)
        {
            OriginalName = "Missing texture";
            Details.Name = "Missing texture";
            Details.Type = type;
            Details.Width = 0;
            Details.Height = 0;
            Details.MipMapCount = 0;
            Details.Compression = "N/A";
        }
        else
        {
            OriginalName = textureData.Name;

            Details.Name = textureData.Name;
            Details.Type = type;
            Details.Width = textureData.Width;
            Details.Height = textureData.Height;
            Details.MipMapCount = textureData.Levels;
            Details.Compression = textureData.Format.ToString();
            
            Details.Validate();
        }
    }

    private static CodeWalker.GameFiles.Texture? CreateMetadataOnlyTexture(CodeWalker.GameFiles.Texture? textureData)
    {
        if (textureData == null)
        {
            return null;
        }

        return new CodeWalker.GameFiles.Texture
        {
            Name = textureData.Name,
            NameHash = textureData.NameHash,
            Width = textureData.Width,
            Height = textureData.Height,
            Depth = textureData.Depth,
            Stride = textureData.Stride,
            Format = textureData.Format,
            Levels = textureData.Levels
        };
    }

    public async Task<bool> EnsureTextureDataLoadedAsync()
    {
        if (DisplayTextureData?.Data?.FullData?.Length > 0)
        {
            return true;
        }

        // A replacement (only its file path survives save/load) takes priority over
        // re-reading the original texture from the source drawable.
        if (!string.IsNullOrEmpty(ReplacementFilePath) && await Task.Run(EnsureReplacementLoaded))
        {
            OnPropertyChanged(nameof(DisplayTextureData));
            OnPropertyChanged(nameof(IsPreviewDisabled));
            return true;
        }

        if (!HasOriginalTexture || string.IsNullOrWhiteSpace(SourceDrawablePath) || !File.Exists(SourceDrawablePath))
        {
            return false;
        }

        await _textureDataSemaphore.WaitAsync();
        try
        {
            if (DisplayTextureData?.Data?.FullData?.Length > 0)
            {
                return true;
            }

            var texture = await ReadOriginalTextureAsync();
            if (texture == null)
            {
                return false;
            }

            TextureData = texture;
            OnPropertyChanged(nameof(DisplayTextureData));
            OnPropertyChanged(nameof(IsPreviewDisabled));
            return true;
        }
        catch (Exception ex)
        {
            LogHelper.Log($"Could not load embedded texture data for {Details.Name}: {ex.Message}", LogType.Warning);
            return false;
        }
        finally
        {
            _textureDataSemaphore.Release();
        }
    }

    /// <summary>
    /// Returns the texture to display (replacement, or the original read from the source drawable) without
    /// keeping the original's pixel data on this object - use for thumbnails/previews that only need it once.
    /// </summary>
    public async Task<CodeWalker.GameFiles.Texture?> LoadDisplayTextureAsync()
    {
        if (DisplayTextureData?.Data?.FullData?.Length > 0)
        {
            return DisplayTextureData;
        }

        if (!string.IsNullOrEmpty(ReplacementFilePath) && await Task.Run(EnsureReplacementLoaded))
        {
            return _replacementTextureData;
        }

        if (!HasOriginalTexture || string.IsNullOrWhiteSpace(SourceDrawablePath) || !File.Exists(SourceDrawablePath))
        {
            return null;
        }

        try
        {
            return await ReadOriginalTextureAsync();
        }
        catch (Exception ex)
        {
            LogHelper.Log($"Could not load embedded texture data for {Details.Name}: {ex.Message}", LogType.Warning);
            return null;
        }
    }

    private async Task<CodeWalker.GameFiles.Texture?> ReadOriginalTextureAsync()
    {
        var fileBytes = await FileHelper.ReadAllBytesAsync(SourceDrawablePath!);
        var yddFile = new YddFile();
        await yddFile.LoadAsync(fileBytes);

        return yddFile.Drawables?
            .FirstOrDefault()?
            .ShaderGroup?
            .TextureDictionary?
            .Textures?
            .data_items?
            .FirstOrDefault(x => x?.Name == OriginalName);
    }

    public void SetReplacementTexture(CodeWalker.GameFiles.Texture newTexture, string? replacementFilePath = null)
    {
        // Persist the source file path so the replacement survives save/load and reaches the build.
        ReplacementFilePath = replacementFilePath;
        ReplacementTextureData = newTexture;

        Details.Name = newTexture.Name;
        Details.Width = newTexture.Width;
        Details.Height = newTexture.Height;
        Details.MipMapCount = newTexture.Levels;
        Details.Compression = newTexture.Format.ToString();
        Details.Validate();

        OnPropertyChanged(nameof(Details));

        ImageThumbnail = null;
        LoadThumbnailAsync();
    }

    /// <summary>
    /// Ensures <see cref="ReplacementTextureData"/> is populated from <see cref="ReplacementFilePath"/>
    /// (rebuilding it after a save/load where only the path was persisted). Returns true if a
    /// replacement texture is available in memory afterwards.
    /// </summary>
    public bool EnsureReplacementLoaded()
    {
        if (_replacementTextureData != null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(ReplacementFilePath))
        {
            return false;
        }

        try
        {
            var fullPath = FileHelper.ResolveFilePath(ReplacementFilePath);
            if (!File.Exists(fullPath))
            {
                LogHelper.Log($"Replacement image for embedded texture '{Details?.Name}' not found: '{fullPath}'.", LogType.Warning);
                return false;
            }

            // Assign the backing field directly - going through the property setter would reset
            // IsOptimizedDuringBuild/OptimizeDetails, which are persisted and must be preserved.
            _replacementTextureData = LoadReplacementFromFile(fullPath);
            return true;
        }
        catch (Exception ex)
        {
            LogHelper.Log($"Could not load replacement for embedded texture '{Details?.Name}': {ex.Message}", LogType.Warning);
            return false;
        }
    }

    private static CodeWalker.GameFiles.Texture LoadReplacementFromFile(string fullPath)
    {
        if (string.Equals(Path.GetExtension(fullPath), ".dds", StringComparison.OrdinalIgnoreCase))
        {
            var ddsTxt = DDSIO.GetTexture(File.ReadAllBytes(fullPath));
            ddsTxt.Name = Path.GetFileNameWithoutExtension(fullPath);
            return ddsTxt;
        }

        using var img = ImgHelper.GetImage(fullPath)
            ?? throw new Exception("Failed to load replacement image from the specified file.");
        img.Format = MagickFormat.Dds;
        var txt = DDSIO.GetTexture(img.ToByteArray());
        txt.Name = Path.GetFileNameWithoutExtension(fullPath);
        return txt;
    }

    public void RenameTexture(string newName)
    {
        // Embedded textures restored from the save file carry no TextureData until loaded, so check the flags.
        if (string.IsNullOrWhiteSpace(newName) || (!HasOriginalTexture && !HasReplacement))
            return;

        Details.Name = newName;
        OnPropertyChanged(nameof(Details));
    }

    private bool _isThumbnailLoading;

    public async void LoadThumbnailAsync()
    {
        if (ImageThumbnail != null || _isThumbnailLoading)
            return;

        _isThumbnailLoading = true;
        IsLoading = true;

        await ImgHelper.PreviewDecodeGate.WaitAsync();
        try
        {
            var thumbnail = await Task.Run(async () =>
            {
                // Prefers a persisted replacement (rebuilt from ReplacementFilePath after a reload)
                // so the thumbnail shows the replacement, not the original.
                var textureData = await LoadDisplayTextureAsync();
                if (textureData?.Data?.FullData == null || textureData.Data.FullData.Length == 0)
                    return null;

                using var img = TextureCodec.DecodePreview(textureData, GTexture.ThumbnailSize);
                img.Resize(GTexture.ThumbnailSize, GTexture.ThumbnailSize);
                return ImgHelper.ToBitmapSource(img);
            });

            if (thumbnail != null)
            {
                _hasPreviewableData = true;
                ImageThumbnail = thumbnail;
                OnPropertyChanged(nameof(IsPreviewDisabled));
                OnPropertyChanged(nameof(PreviewDisabledTooltip));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Embedded texture thumbnail generation failed: {ex.Message}");
            LogHelper.Log($"Could not generate embedded texture thumbnail for {Details.Name}");
        }
        finally
        {
            ImgHelper.PreviewDecodeGate.Release();
            _isThumbnailLoading = false;
            IsLoading = false;
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
