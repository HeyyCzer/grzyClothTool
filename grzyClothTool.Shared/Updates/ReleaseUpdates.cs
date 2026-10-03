using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

namespace grzyClothTool.Shared.Updates;

/// <summary>
/// GitHub release plumbing shared by the app updater and grzyOptimizer's self-update.
/// The latest version is &lt;FileVersion&gt; of grzyClothTool.csproj on master (bumped by scripts/bump-version.ps1);
/// the release workflow publishes every asset of that version under the tag v&lt;version&gt;.
/// </summary>
public static class ReleaseUpdates
{
    public const string Repository = "heyyczer/grzyClothTool";
    public const string VersionFileUrl = $"https://raw.githubusercontent.com/{Repository}/master/grzyClothTool/grzyClothTool.csproj";
    public const string ReleasesUrl = $"https://github.com/{Repository}/releases";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static string AssetUrl(string version, string assetName) => $"{ReleasesUrl}/download/v{version}/{assetName}";

    /// <summary>FileVersion of the running exe (works for single-file publishes too).</summary>
    public static string? GetCurrentVersion() =>
        Environment.ProcessPath is { } exe ? FileVersionInfo.GetVersionInfo(exe).FileVersion : null;

    /// <returns>The version on master, or null when it could not be read (offline, GitHub down, ...).</returns>
    public static async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await RetryAsync(() => HttpClient.GetStringAsync(VersionFileUrl, cancellationToken), maxAttempts: 2, cancellationToken);
            return XDocument.Parse(content).Root?.Element("PropertyGroup")?.Element("FileVersion")?.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="latest"/> is a higher version than <paramref name="current"/>.
    /// Unparsable versions fall back to "different = newer", so a broken version string still gets fixed by an update.
    /// </summary>
    public static bool IsNewer(string? latest, string? current)
    {
        if (string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }
        if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c))
        {
            return Normalize(l) > Normalize(c);
        }
        return !string.Equals(latest.Trim(), current?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>1.5 and 1.5.0.0 compare equal: missing parts count as 0.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Downloads the release asset of <paramref name="version"/> to <paramref name="destinationPath"/>.</summary>
    public static async Task DownloadAssetAsync(string version, string assetName, string destinationPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        SafeDeleteFile(destinationPath, maxAttempts: 3);

        var url = AssetUrl(version, assetName);
        await RetryAsync(async () =>
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            await response.Content.CopyToAsync(fileStream, cancellationToken);
            await fileStream.FlushAsync(cancellationToken);
            return true;
        }, maxAttempts: 3, cancellationToken);
    }

    /// <summary>Extracts <paramref name="zipPath"/> into <paramref name="folder"/>, retrying while antivirus scanners hold the zip.</summary>
    public static void ExtractZip(string zipPath, string folder)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("Update package not found.", zipPath);
        }

        Directory.CreateDirectory(folder);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                ZipFile.ExtractToDirectory(zipPath, folder, overwriteFiles: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(500);
            }
        }
    }

    public const int MaxRetries = 5;
    public const int InitialRetryDelayMs = 100;

    private static int RetryDelay(int attempt) => InitialRetryDelayMs * (1 << attempt);

    public static async Task<T> RetryAsync<T>(Func<Task<T>> operation, int maxAttempts = MaxRetries, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception) when (attempt < maxAttempts - 1 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RetryDelay(attempt), cancellationToken);
            }
        }
    }

    /// <summary>Deletes a file, clearing read-only and retrying while it is locked. Never throws.</summary>
    public static void SafeDeleteFile(string filePath, int maxAttempts = MaxRetries)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                    File.Delete(filePath);
                }
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (attempt < maxAttempts - 1)
                {
                    Thread.Sleep(RetryDelay(attempt));
                }
            }
        }
    }

    /// <summary>Deletes a folder tree, clearing read-only and retrying while it is locked. Throws after the last attempt.</summary>
    public static void SafeDeleteDirectory(string path, int maxAttempts = MaxRetries)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    SetAttributesNormal(new DirectoryInfo(path));
                    Directory.Delete(path, true);
                }
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && attempt < maxAttempts - 1)
            {
                Thread.Sleep(RetryDelay(attempt));
            }
        }
    }

    /// <summary>Deletes every "extract_*" folder in <paramref name="updateFolder"/>, then the folder itself if empty. Never throws.</summary>
    public static void CleanupUpdateFolder(string updateFolder)
    {
        try
        {
            if (!Directory.Exists(updateFolder))
            {
                return;
            }
            foreach (var folder in Directory.GetDirectories(updateFolder, "extract_*"))
            {
                try
                {
                    SafeDeleteDirectory(folder, maxAttempts: 2);
                }
                catch
                {
                    // Still locked; the next start retries.
                }
            }
            if (Directory.GetFileSystemEntries(updateFolder).Length == 0)
            {
                Directory.Delete(updateFolder);
            }
        }
        catch
        {
        }
    }

    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        try
        {
            foreach (var subDir in dir.GetDirectories())
            {
                SetAttributesNormal(subDir);
            }
            foreach (var file in dir.GetFiles())
            {
                file.Attributes = FileAttributes.Normal;
            }
        }
        catch
        {
        }
    }
}
