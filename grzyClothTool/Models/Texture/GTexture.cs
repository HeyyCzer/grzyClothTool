using CodeWalker.GameFiles;
using CodeWalker.Utils;
using grzyClothTool.Helpers;
using ImageMagick;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Text.Json.Serialization;

namespace grzyClothTool.Models.Texture;

#nullable enable

public class GTexture : INotifyPropertyChanged, IJsonOnDeserialized
{
    private readonly static SemaphoreSlim _semaphore = new(3);

    public event PropertyChangedEventHandler PropertyChanged;

    public Guid Id { get; set; }
    public string FilePath { get; set; }
    public string Extension { get; set; }

    [JsonIgnore]
    public string FullFilePath => FileHelper.ResolveFilePath(FilePath);

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get { return _displayName; }
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    private int _number;
    public int Number
    {
        get => _number;
        set
        {
            _number = value;
            OnPropertyChanged(nameof(Number));
            UpdateDisplayName();
        }
    }

    private int _txtNumber;
    public int TxtNumber
    {
        get => _txtNumber;
        set
        {
            _txtNumber = value;
            
            OnPropertyChanged();
            OnPropertyChanged("BuildName");
            OnPropertyChanged("TxtLetter");
            UpdateDisplayName();
        }
    }

    public char TxtLetter
    {
        get => (char)('a' + TxtNumber);
    }

    public int TypeNumeric { get; set; }
    public string TypeName => EnumHelper.GetName(TypeNumeric, IsProp);

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

    public GTextureDetails TxtDetails { get; set; }

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
    public bool IsProp { get; set; }
    public bool HasSkin { get; set; }

    private bool _isOptimizedDuringBuild;
    public bool IsOptimizedDuringBuild
    {
        get => _isOptimizedDuringBuild;
        set
        {
            if (_isOptimizedDuringBuild != value)
            {
                _isOptimizedDuringBuild = value;
                OnPropertyChanged(nameof(IsOptimizedDuringBuild));
            }
        }
    }
    private GTextureDetails _optimizeDetails;
    public GTextureDetails OptimizeDetails
    {
        get => _optimizeDetails;
        set
        {
            if (_optimizeDetails != value)
            {
                _optimizeDetails = value;
                OnPropertyChanged(nameof(OptimizeDetails));
            }
        }
    }

    public bool IsPreviewDisabled { get; set; }

    public GTexture(Guid id, string filePath, int typeNumeric, int number, int txtNumber, bool hasSkin, bool isProp)
        : this(id, filePath, typeNumeric, number, txtNumber, hasSkin, isProp, null)
    {
    }

    /// <summary>
    /// Used by the save file deserializer. <paramref name="txtDetails"/> are the details stored in the save; they
    /// are reused as-is when the texture file wasn't modified after the save was written, instead of loading
    /// and decompressing the whole texture again just to read its size and format.
    /// </summary>
    [JsonConstructor]
    public GTexture(Guid id, string filePath, int typeNumeric, int number, int txtNumber, bool hasSkin, bool isProp, GTextureDetails? txtDetails)
    {
        IsLoading = true;

        Id = id;
        if (Id == Guid.Empty)
        {
            Id = Guid.NewGuid();
        }

        FilePath = filePath;
        Extension = Path.GetExtension(filePath);
        Number = number;
        TxtNumber = txtNumber;
        TypeNumeric = typeNumeric;
        IsProp = isProp;
        HasSkin = hasSkin;
        DisplayName = GetBuildName();
        TxtDetails = txtDetails!;

        if (filePath != null)
        {
            try
            {
                var fullPath = FileHelper.ResolveFilePath(filePath);

                if (txtDetails != null && FileHelper.IsUnchangedSinceLoadedSave(fullPath))
                {
                    TxtDetails.Validate();
                    _detailsRestoredFromSave = true;
                    IsLoading = false;
                    return;
                }

                Task<GTextureDetails?> _textureDetailsTask = LoadTextureDetailsWithConcurrencyControl(fullPath).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        LogHelper.Log($"Failed to load texture details for '{DisplayName}': {t.Exception?.InnerException?.Message ?? t.Exception?.Message}", Views.LogType.Warning);
                        IsLoading = false;
                        return null;
                    }

                    IsLoading = false; // Loading finished
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    if (t.Result == null)
                    {
                        IsPreviewDisabled = true;
                        return null;
                    }

                    TxtDetails = t.Result;
                    OnPropertyChanged(nameof(TxtDetails));

                    TxtDetails.Validate();
                }

                return t.Result;
            });
            }
            catch (Exception ex)
            {
                LogHelper.Log($"Could not load texture '{DisplayName}': {ex.Message}", Views.LogType.Warning);
                IsLoading = false;
                IsPreviewDisabled = true;
            }
        }
    }

    private bool _detailsRestoredFromSave;

    void IJsonOnDeserialized.OnDeserialized()
    {
        // IsLoading is serialized too; a save written mid-load must not leave the texture "loading".
        if (_detailsRestoredFromSave)
        {
            IsLoading = false;
        }
    }

    public const int ThumbnailSize = 90;

    private bool _isThumbnailLoading;

    /// <summary>
    /// Generates <see cref="ImageThumbnail"/> once, on demand (when the owning drawable gets selected).
    /// Decodes a small mip instead of the full texture and goes through <see cref="ImgHelper.PreviewDecodeGate"/>.
    /// </summary>
    public async void LoadThumbnailAsync()
    {
        if (ImageThumbnail != null || _isThumbnailLoading)
            return;

        try
        {
            if (FilePath == null || !File.Exists(FullFilePath))
                return;
        }
        catch (Exception ex)
        {
            LogHelper.Log($"Could not find texture '{DisplayName}': {ex.Message}", Views.LogType.Warning);
            return;
        }

        _isThumbnailLoading = true;
        await ImgHelper.PreviewDecodeGate.WaitAsync();
        try
        {
            var thumbnail = await Task.Run(() =>
            {
                using MagickImage? img = ImgHelper.GetPreviewImage(FullFilePath, ThumbnailSize);
                if (img == null)
                    return null;

                img.Resize(ThumbnailSize, ThumbnailSize);
                return ImgHelper.ToBitmapSource(img);
            });

            if (thumbnail != null)
            {
                ImageThumbnail = thumbnail;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Image thumbnail generation failed: {ex.Message}");
            LogHelper.Log($"Could not generate image thumbnail for {DisplayName}");
        }
        finally
        {
            ImgHelper.PreviewDecodeGate.Release();
            _isThumbnailLoading = false;
        }
    }



    public string GetBuildName()
    {
        string name = $"{TypeName}_diff_{Number:D3}_{TxtLetter}";
        return IsProp ? name : $"{name}_{(HasSkin ? "whi" : "uni")}";
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public async Task LoadDetails()
    {
        if (File.Exists(FullFilePath))
        {
            var result = await LoadTextureDetailsWithConcurrencyControl(FullFilePath);
            if (result != null)
            {
                TxtDetails = result;
                OnPropertyChanged(nameof(TxtDetails));
                TxtDetails.Validate();
            }
        }

        IsLoading = false;
    }

    private void UpdateDisplayName()
    {
        DisplayName = GetBuildName();
    }

    private static async Task<GTextureDetails?> LoadTextureDetailsWithConcurrencyControl(string path)
    {
        await _semaphore.WaitAsync();
        try
        {
            return await GetTextureDetailsAsync(path);
        }
        finally
        {
            _semaphore.Release();
        }
    }
    private static async Task<GTextureDetails?> GetTextureDetailsAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var extension = Path.GetExtension(path);

        if (extension == ".ytd")
        {
            var ytdFile = new YtdFile();
            await ytdFile.LoadAsync(bytes);

            if (ytdFile.TextureDict.Textures.Count == 0)
            {
                return null;
            }

            var txt = ytdFile.TextureDict.Textures[0];

            return new GTextureDetails
            {
                MipMapCount = txt.Levels,
                Compression = txt.Format.ToString(),
                Width = txt.Width,
                Height = txt.Height,
                Name = txt.Name,
                Type = "diffuse"
            };
        }
        else if (extension == ".dds")
        {
            try
            {
                var txt = DDSIO.GetTexture(bytes);
                if (txt != null)
                {
                    return new GTextureDetails
                    {
                        MipMapCount = txt.Levels,
                        Compression = txt.Format.ToString(),
                        Width = txt.Width,
                        Height = txt.Height,
                        Name = Path.GetFileNameWithoutExtension(path),
                        Type = "diffuse"
                    };
                }
            }
            catch
            {
                // Fall back to ImageMagick so partially supported DDS files can still show basic details.
            }
        }

        if (extension == ".jpg" || extension == ".png" || extension == ".dds")
        {
            using var img = new MagickImage(bytes);

            return new GTextureDetails
            {
                Width = (int)img.Width,
                Height = (int)img.Height,
                MipMapCount = ImgHelper.GetCorrectMipMapAmount((int)img.Width, (int)img.Height),
                Compression = "UNKNOWN",
                Name = img.FileName,
                Type = "diffuse"
            };
        }

        return null;
    }
}
