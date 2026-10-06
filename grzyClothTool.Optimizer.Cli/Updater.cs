using System.Diagnostics;
using System.Reflection;
using grzyClothTool.Shared.Updates;

namespace grzyClothTool.Optimizer.Cli;

/// <summary>
/// Self-update of grzyOptimizer.exe from the GitHub release grzyOptimizer.zip.
/// A running exe can be renamed but not overwritten on Windows, so every file/folder of the install is renamed to
/// "*.old" and the new one moved in its place; the leftovers are deleted on the next start.
/// The update is extracted next to the exe (same volume, so folders can be moved, and a read-only install fails
/// before anything is touched).
/// </summary>
internal static class Updater
{
    public const string AssetName = "grzyOptimizer.zip";
    private const string ExeName = "grzyOptimizer.exe";
    private const string OldSuffix = ".old";
    private const string StagingFolderName = ".grzyOptimizer_update";
    /// <summary>Set for the relaunched process (and usable by scripts) to skip the update check.</summary>
    public const string SkipEnvironmentVariable = "GRZYOPTIMIZER_SKIP_UPDATE";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    private static string InstallFolder => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>Only a published single-file exe replaces itself; a dev build in bin/ only gets the notice.</summary>
    private static bool CanSelfUpdate => string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);

    public static bool SkipRequested => Environment.GetEnvironmentVariable(SkipEnvironmentVariable) is "1" or "true";

    /// <summary>Deletes the "*.old" files left by the previous update. Never throws.</summary>
    public static void CleanupPreviousUpdate()
    {
        try
        {
            // EndsWith: on Windows "*.old" also matches "*.older" (8.3 extension rule).
            foreach (var path in Directory.EnumerateFileSystemEntries(InstallFolder, "*" + OldSuffix)
                         .Where(p => p.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                if (Directory.Exists(path))
                {
                    try
                    {
                        ReleaseUpdates.SafeDeleteDirectory(path, maxAttempts: 2);
                    }
                    catch
                    {
                        // Still in use (Blender running the bundled Sollumz); the next start retries.
                    }
                }
                else
                {
                    ReleaseUpdates.SafeDeleteFile(path, maxAttempts: 2);
                }
            }
            ReleaseUpdates.SafeDeleteDirectory(Path.Combine(InstallFolder, StagingFolderName), maxAttempts: 2);
        }
        catch
        {
        }
    }

    /// <summary>Quick startup check (short timeout, silent when offline).</summary>
    /// <returns>The newer version, or null when up to date or the check failed.</returns>
    public static async Task<string?> FindNewerVersionAsync()
    {
        using var timeout = new CancellationTokenSource(CheckTimeout);
        try
        {
            var latest = await ReleaseUpdates.GetLatestVersionAsync(timeout.Token);
            return ReleaseUpdates.IsNewer(latest, ReleaseUpdates.GetCurrentVersion()) ? latest : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Startup flow. Interactive runs are asked whether to update and, if so, the new exe is run with the same
    /// arguments; command line runs only get a notice so scripted runs are never changed under them.
    /// </summary>
    /// <returns>The exit code of the relaunched new version, or null to continue with this one.</returns>
    public static async Task<int?> CheckOnStartupAsync(string[] args, bool interactive)
    {
        var latest = await FindNewerVersionAsync();
        if (latest == null)
        {
            return null;
        }

        var current = ReleaseUpdates.GetCurrentVersion();
        if (!interactive || !CanSelfUpdate || Console.IsInputRedirected)
        {
            Console.WriteLine(CanSelfUpdate
                ? $"grzyOptimizer v{latest} is available (this is v{current}). Run \"grzyOptimizer --update\" to install it."
                : $"grzyOptimizer v{latest} is available (this is v{current}): {ReleaseUpdates.ReleasesUrl}");
            Console.WriteLine();
            return null;
        }

        Console.Write($"grzyOptimizer v{latest} is available (this is v{current}). Update now? [Y/n] ");
        var answer = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(answer) && !answer.StartsWith('y') && !answer.StartsWith('Y') && !answer.StartsWith('s') && !answer.StartsWith('S'))
        {
            Console.WriteLine();
            return null;
        }

        if (!await InstallAsync(latest))
        {
            Console.WriteLine("Continuing with the current version.");
            Console.WriteLine();
            return null;
        }

        return Relaunch(args);
    }

    /// <summary>"--update": checks and installs without running anything else.</summary>
    /// <returns>Process exit code.</returns>
    public static async Task<int> UpdateCommandAsync()
    {
        var current = ReleaseUpdates.GetCurrentVersion();
        Console.WriteLine($"grzyOptimizer v{current} - checking for updates...");

        var latest = await ReleaseUpdates.GetLatestVersionAsync();
        if (latest == null)
        {
            Console.Error.WriteLine($"Error: could not check for updates. Download manually: {ReleaseUpdates.ReleasesUrl}");
            return 1;
        }
        if (!ReleaseUpdates.IsNewer(latest, current))
        {
            Console.WriteLine("You're up to date.");
            return 0;
        }
        if (!CanSelfUpdate)
        {
            Console.Error.WriteLine($"v{latest} is available, but this is a development build; it is not replaced. Download: {ReleaseUpdates.ReleasesUrl}");
            return 1;
        }

        return await InstallAsync(latest) ? 0 : 1;
    }

    /// <returns>True when the new version is in place.</returns>
    private static async Task<bool> InstallAsync(string version)
    {
        var updateFolder = Path.Combine(Path.GetTempPath(), "grzyoptimizer_update");
        var zip = Path.Combine(updateFolder, AssetName);
        var staging = Path.Combine(InstallFolder, StagingFolderName);

        try
        {
            Console.WriteLine($"Downloading v{version}...");
            await ReleaseUpdates.DownloadAssetAsync(version, AssetName, zip);

            ReleaseUpdates.SafeDeleteDirectory(staging);
            ReleaseUpdates.ExtractZip(zip, staging);
            if (!File.Exists(Path.Combine(staging, ExeName)))
            {
                throw new FileNotFoundException($"{ExeName} not found in the update package.");
            }

            PreloadAssemblies();
            ReplaceInstall(staging);
            Console.WriteLine($"Updated to v{version}.");
            return true;
        }
        catch (Exception ex)
        {
            var reason = ex is UnauthorizedAccessException
                ? $"no write access to {InstallFolder}"
                : ex.Message;
            Console.Error.WriteLine($"Update failed: {reason}");
            Console.Error.WriteLine($"Download manually: {ReleaseUpdates.AssetUrl(version, AssetName)}");
            return false;
        }
        finally
        {
            ReleaseUpdates.SafeDeleteFile(zip, maxAttempts: 2);
            ReleaseUpdates.CleanupUpdateFolder(updateFolder);
            try
            {
                ReleaseUpdates.SafeDeleteDirectory(staging, maxAttempts: 2);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Moves every top-level entry of <paramref name="staging"/> into the install folder, renaming what it replaces
    /// to "*.old". Any failure puts the previous files back before rethrowing, so the install is never half updated.
    /// User files (settings, logs/) are not in the package and are left alone.
    /// </summary>
    private static void ReplaceInstall(string staging)
    {
        var done = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var source in Directory.EnumerateFileSystemEntries(staging))
            {
                var target = Path.Combine(InstallFolder, Path.GetFileName(source));
                bool isDirectory = Directory.Exists(source);

                string? backup = null;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    backup = target + OldSuffix;
                    DeletePath(backup);
                    MovePath(target, backup);
                }
                // Recorded before the move in: a failed move still restores the backup.
                done.Add((target, backup));

                if (isDirectory)
                {
                    Directory.Move(source, target);
                }
                else
                {
                    File.Move(source, target);
                }
            }
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var (target, backup) = done[i];
                try
                {
                    if (backup == null)
                    {
                        DeletePath(target);
                    }
                    else if (File.Exists(backup) || Directory.Exists(backup))
                    {
                        DeletePath(target);
                        MovePath(backup, target);
                    }
                }
                catch
                {
                    // Best effort: the ".old" copy stays next to the exe for a manual restore.
                }
            }
            throw;
        }

        // Everything but the running exe can usually go now; what is still locked goes on the next start.
        foreach (var (_, backup) in done)
        {
            if (backup == null)
            {
                continue;
            }
            try
            {
                if (Directory.Exists(backup))
                {
                    ReleaseUpdates.SafeDeleteDirectory(backup, maxAttempts: 1);
                }
                else
                {
                    ReleaseUpdates.SafeDeleteFile(backup, maxAttempts: 1);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// A single-file exe loads bundled assemblies lazily, reopening the bundle by its path. Once the install is
    /// replaced that path is the new exe (different offsets), so anything first used afterwards (e.g.
    /// System.Diagnostics.Process in <see cref="Relaunch"/>) fails with FileNotFoundException. Loads every assembly
    /// reachable from the entry assembly while the path still points to this exe.
    /// </summary>
    private static void PreloadAssemblies()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry == null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<Assembly>();
        pending.Push(entry);
        seen.Add(entry.GetName().Name ?? "");
        while (pending.TryPop(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!seen.Add(reference.Name ?? ""))
                {
                    continue;
                }
                try
                {
                    pending.Push(Assembly.Load(reference));
                }
                catch
                {
                    // Optional/platform-specific reference that is not shipped; never used by this process either.
                }
            }
        }
    }

    private static void MovePath(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            ReleaseUpdates.SafeDeleteDirectory(path);
        }
        else if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    /// <summary>Runs the new exe in this console with the same arguments and waits for it.</summary>
    private static int Relaunch(string[] args)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(InstallFolder, ExeName))
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        startInfo.Environment[SkipEnvironmentVariable] = "1";

        Console.WriteLine();
        // Ctrl+C reaches both processes: let the new one handle it, this one only waits.
        ConsoleCancelEventHandler ignore = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += ignore;
        try
        {
            using var process = Process.Start(startInfo)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= ignore;
        }
    }
}
