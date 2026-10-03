using CodeWalker.GameFiles;
using grzyClothTool.Models.Texture;
using grzyClothTool.Optimization;
using ImageMagick;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace grzyClothTool.Helpers;

public static class ImgHelper
{
    static ImgHelper()
    {
        MagickNET.Initialize();
    }

    public static MagickImage GetImage(string path)
    {
        string ext = Path.GetExtension(path);

        try
        {
            if(ext == ".ytd")
            {
                var ytd = CWHelper.GetYtdFile(path);

                if (ytd.TextureDict.Textures.Count == 0)
                {
                    return null;
                }
                return TextureCodec.Decode(ytd.TextureDict.Textures[0]);
            }
            else
            {
                return new MagickImage(path);
            }
        }
        catch (Exception e) when (e.Message.Contains("Invalid slice pitch"))
        {
            TelemetryHelper.CaptureExceptionWithAttachment(e, path);
            throw;
        }
    }

    /// <summary>
    /// Limits how many texture previews/thumbnails decode at once. Selecting a drawable queues one per texture
    /// and each still has to load and decompress its whole .ytd, so running them all in parallel spiked memory.
    /// </summary>
    public static readonly SemaphoreSlim PreviewDecodeGate = new(2);

    /// <summary>
    /// Like <see cref="GetImage"/>, but for display only: .ytd/.dds textures decode the smallest mip that is
    /// still at least <paramref name="minSize"/> px instead of the full-resolution top level.
    /// </summary>
    public static MagickImage? GetPreviewImage(string path, int minSize)
    {
        string ext = Path.GetExtension(path);
        if (ext == ".ytd")
        {
            var ytd = CWHelper.GetYtdFile(path);
            return ytd.TextureDict.Textures.Count == 0 ? null : TextureCodec.DecodePreview(ytd.TextureDict.Textures[0], minSize);
        }

        if (ext == ".dds")
        {
            try
            {
                var texture = CodeWalker.Utils.DDSIO.GetTexture(File.ReadAllBytes(path));
                if (texture != null)
                {
                    return TextureCodec.DecodePreview(texture, minSize);
                }
            }
            catch
            {
                // Partially supported DDS files: let ImageMagick read them below.
            }
        }

        return GetImage(path);
    }

    /// <summary>Copies the image's pixels into a frozen (cross-thread usable) BGRA bitmap.</summary>
    public static BitmapSource ToBitmapSource(MagickImage img)
    {
        int w = (int)img.Width;
        int h = (int)img.Height;
        byte[] pixels = img.ToByteArray(MagickFormat.Bgra);
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        source.Freeze();
        return source;
    }

    public static int GetCorrectMipMapAmount(int width, int height) => TextureRules.GetCorrectMipMapAmount(width, height);

    public static (int, int) CheckPowerOfTwo(int width, int height)
    {
        if ((width & (width - 1)) != 0 || (height & (height - 1)) != 0)
        {
            int newWidth = (int)Math.Pow(2, Math.Ceiling(Math.Log(width) / Math.Log(2)));
            int newHeight = (int)Math.Pow(2, Math.Ceiling(Math.Log(height) / Math.Log(2)));

            return (newWidth, newHeight);
        }
        return (width, height);
    }

    public static async Task<byte[]?> Optimize(GTexture gtxt, bool shouldSkipOptimization = false)
    {
        try
        {
            if (shouldSkipOptimization && gtxt.Extension == ".dds")
            {
                return GetDDSBytes(gtxt);
            }

            var ytd = new YtdFile
            {
                TextureDict = new TextureDictionary()
            };

            using var img = GetImage(gtxt.FullFilePath);
            img.Format = MagickFormat.Dds;

            // Skip optimization (I think this is best way to not duplicate code, and reuse this for jpg/png textures that don't need optimization)
            var newDds = shouldSkipOptimization
                ? img.ToByteArray()
                : TextureCodec.EncodeDds(img, ToTextureInfo(gtxt.OptimizeDetails));
            var newTxt = CodeWalker.Utils.DDSIO.GetTexture(newDds);
            newTxt.Name = gtxt.DisplayName;
            ytd.TextureDict.BuildFromTextureList([newTxt]);

            var bytes = ytd.Save();
            return bytes;
        }
        catch (MagickCorruptImageErrorException)
        {
            // Image is corrupted and cannot be processed
            return null;
        }
    }

    public static async Task<byte[]?> Optimize(byte[] imgBytes, GTextureDetails optimizeDetails)
    {
        try
        {
            using var img = new MagickImage(imgBytes);
            return TextureCodec.EncodeDds(img, ToTextureInfo(optimizeDetails));
        }
        catch (MagickCorruptImageErrorException)
        {
            // Image is corrupted and cannot be processed
            return null;
        }
    }

    public static byte[] GetDDSBytes(GTexture gtxt)
    {
        var ytd = new YtdFile
        {
            TextureDict = new TextureDictionary()
        };

        byte[] ddsBytes = [];

        if (gtxt.Extension == ".dds")
        {
            ddsBytes = File.ReadAllBytes(gtxt.FullFilePath);
        } 
        else if (gtxt.Extension == ".jpg" || gtxt.Extension == ".png")
        {
            using var img = GetImage(gtxt.FullFilePath);
            img.Format = MagickFormat.Dds;

            var stream = new MemoryStream();
            img.Write(stream);

            ddsBytes = stream.ToArray();
        }

        var newTxt = CodeWalker.Utils.DDSIO.GetTexture(ddsBytes);

        newTxt.Name = gtxt.DisplayName;
        ytd.TextureDict.BuildFromTextureList([newTxt]);

        return ytd.Save();
    }

    public static byte[] GetDDSFileBytes(GTexture gtxt)
    {
        if (gtxt.Extension == ".dds")
        {
            return File.ReadAllBytes(gtxt.FullFilePath);
        }

        if (gtxt.Extension == ".ytd")
        {
            var ytd = new YtdFile();
            ytd.Load(File.ReadAllBytes(gtxt.FullFilePath));
            if (ytd.TextureDict.Textures.Count == 0)
            {
                return [];
            }

            return CodeWalker.Utils.DDSIO.GetDDSFile(ytd.TextureDict.Textures[0]);
        }

        if (gtxt.Extension == ".jpg" || gtxt.Extension == ".png")
        {
            using var img = GetImage(gtxt.FullFilePath);
            img.Format = MagickFormat.Dds;

            var stream = new MemoryStream();
            img.Write(stream);

            return stream.ToArray();
        }

        throw new NotSupportedException($"Unsupported file extension: {gtxt.Extension}");
    }

    private static TextureInfo ToTextureInfo(GTextureDetails details) =>
        new(details.Width, details.Height, details.MipMapCount, details.Compression ?? string.Empty);
}
