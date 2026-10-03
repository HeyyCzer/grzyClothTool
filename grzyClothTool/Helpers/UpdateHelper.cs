using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using grzyClothTool.Shared.Updates;

namespace grzyClothTool.Helpers;

public static class UpdateHelper
{
    private static readonly string _exeLocation;
    private static readonly string _updateFolder;
    private static Mutex _appMutex;

    static UpdateHelper()
    {
        _exeLocation = GetExeLocation();
        _updateFolder = Path.Combine(Path.GetTempPath(), "grzyclothtool_update");
        
        _appMutex = new Mutex(true, "grzyClothTool_SingleInstance", out bool createdNew);
        
        if (createdNew)
        {
            ReleaseUpdates.CleanupUpdateFolder(_updateFolder);
        }
    }

    private static string GetExeLocation()
    {
        string assemblyName = Assembly.GetEntryAssembly().GetName().Name;
        var assemblyLocation = Path.Join(AppContext.BaseDirectory, $"{assemblyName}.exe");

        return assemblyLocation;
    }

    public static string GetCurrentVersion()
    {
        return ReleaseUpdates.GetCurrentVersion();
    }

    public async static Task CheckForUpdates()
    {
        string[] args = Environment.GetCommandLineArgs();
        
        if (args.Contains("--skipUpdate"))
        {
            var removeTempFilesArg = args.FirstOrDefault(arg => arg.StartsWith("--removeTempFiles"));
            if (removeTempFilesArg != null)
            {
                App.splashScreen.AddMessage("Completing update...");
                
                var oldExePath = removeTempFilesArg.Split('=')[1].Trim('"');
                
                RemoveTempFilesAndRestart(oldExePath);
                
                await Task.Delay(Timeout.Infinite);
            }

            return;
        }

        CancellationTokenSource cts = null;
        try
        {
            cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            
            string currentVersion = GetCurrentVersion();
                
            App.splashScreen.AddMessage("Checking for updates...");
            var latestVersion = await ReleaseUpdates.GetLatestVersionAsync(cts.Token);

            if (latestVersion is null)
            {
                App.splashScreen.AddMessage("Could not check for updates.");
                await Task.Delay(500, cts.Token);
                return;
            }

            if (!ReleaseUpdates.IsNewer(latestVersion, currentVersion))
            {
                App.splashScreen.AddMessage("You're up to date!");
                await Task.Delay(500, cts.Token);
                return;
            }

            App.splashScreen.AddMessage($"Downloading v{latestVersion}...");
            
            await DownloadUpdate(latestVersion, cts.Token);
        }
        catch (OperationCanceledException)
        {
            App.splashScreen.AddMessage("Update check timed out.");
            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            App.splashScreen.AddMessage("Update check failed.");
            try
            {
                await File.WriteAllTextAsync("update_check_failed.log", $"[{DateTime.Now}]\n{ex}");
            }
            catch { }
            await Task.Delay(1000);
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private static async Task DownloadUpdate(string version, CancellationToken cancellationToken)
    {
        string downloadZip = Path.Combine(_updateFolder, "grzyClothTool.zip");

        try
        {
            await ReleaseUpdates.DownloadAssetAsync(version, "grzyClothTool.zip", downloadZip, cancellationToken);
            
            App.splashScreen.AddMessage("Download complete. Installing...");
            await Task.Delay(500, cancellationToken);
            
            ExtractAndRunUpdatedApp();
        }
        catch (OperationCanceledException)
        {
            App.splashScreen.AddMessage("Download cancelled.");
            await Task.Delay(1500);
        }
        catch(Exception ex)
        {
            try
            {
                await File.WriteAllTextAsync("download_failed.log", $"[{DateTime.Now}]\n{ex}");
            }
            catch { }

            App.splashScreen.AddMessage("Download failed. Please try again later.");
            await Task.Delay(2000);
        }
    }

    private static void ExtractAndRunUpdatedApp()
    {
        try
        {
            string downloadZip = Path.Combine(_updateFolder, "grzyClothTool.zip");

            string extractFolder = Path.Combine(_updateFolder, $"extract_{DateTime.Now:yyyyMMddHHmmss}");
            ReleaseUpdates.ExtractZip(downloadZip, extractFolder);
            
            ReleaseUpdates.SafeDeleteFile(downloadZip);

            var newExeLocation = Path.Combine(extractFolder, "grzyClothTool.exe");
            
            if (!File.Exists(newExeLocation))
            {
                throw new FileNotFoundException("Updated executable not found in package.");
            }

            _appMutex?.ReleaseMutex();
            _appMutex?.Dispose();

            // Run exe with args
            ProcessStartInfo startInfo = new()
            {
                FileName = newExeLocation,
                ArgumentList = { "--skipUpdate", $"--removeTempFiles=\"{_exeLocation}\"" },
                UseShellExecute = true,
                WorkingDirectory = extractFolder
            };
            
            Process.Start(startInfo);

            Thread.Sleep(500);
            
            App.splashScreen?.Shutdown();
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText("extract_failed.log", $"[{DateTime.Now}]\n{ex}");
            }
            catch { }
            
            App.splashScreen?.AddMessage("Installation failed. Please update manually.");
            Task.Delay(2500).Wait();
        }
    }

    private static void RemoveTempFilesAndRestart(string oldExeLocation)
    {
        try
        {
            // Kill all other grzyClothTool processes to ensure no file locks
            KillAllOtherInstances();
            
            Thread.Sleep(2000);
            
            var oldDir = Path.GetDirectoryName(oldExeLocation);
            if (string.IsNullOrEmpty(oldDir) || !Directory.Exists(oldDir))
            {
                ForceShutdown();
                return;
            }

            // Remove old .exe and .dll.config from oldExeLocation
            string[] fileExtensions = [".exe", ".dll.config"];
            foreach (var extension in fileExtensions)
            {
                var pattern = $"grzyClothTool{extension}";
                var filesToDelete = Directory.GetFiles(oldDir, pattern);
                foreach (var file in filesToDelete)
                {
                    ReleaseUpdates.SafeDeleteFile(file);
                }
            }

            string[] files = Directory.GetFiles(AppContext.BaseDirectory);
            int filesMoved = 0;
            foreach (var file in files)
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    var destPath = Path.Combine(oldDir, fileName);
                    
                    if (string.Equals(file, destPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    
                    ReleaseUpdates.SafeDeleteFile(destPath);
                    
                    for (int attempt = 0; attempt < ReleaseUpdates.MaxRetries; attempt++)
                    {
                        try
                        {
                            File.Move(file, destPath);
                            filesMoved++;
                            break;
                        }
                        catch (IOException) when (attempt < ReleaseUpdates.MaxRetries - 1)
                        {
                            Thread.Sleep(ReleaseUpdates.InitialRetryDelayMs * (1 << attempt));
                        }
                    }
                }
                catch
                {
                    // Continue with next file even if one fails
                }
            }

            if (filesMoved > 0)
            {
                var finalExePath = Path.Combine(oldDir, "grzyClothTool.exe");
                if (File.Exists(finalExePath))
                {
                    ProcessStartInfo startInfo = new()
                    {
                        FileName = finalExePath,
                        UseShellExecute = true,
                        WorkingDirectory = oldDir
                    };
                    
                    Process.Start(startInfo);
                    
                    Thread.Sleep(500);
                }
            }

            ReleaseUpdates.CleanupUpdateFolder(_updateFolder);

            ForceShutdown();
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "grzyclothtool_cleanup_error.log"), 
                    $"[{DateTime.Now}]\n{ex}");
            }
            catch { }
            
            ForceShutdown();
        }
    }

    private static void KillAllOtherInstances()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var currentProcessId = currentProcess.Id;
            
            var processes = Process.GetProcessesByName("grzyClothTool");
            
            foreach (var process in processes)
            {
                try
                {
                    // Skip the current process (the temp updater instance)
                    if (process.Id == currentProcessId)
                    {
                        continue;
                    }
                    
                    if (!process.HasExited)
                    {
                        process.Kill();
                        
                        process.WaitForExit(2000);
                    }
                    
                    process.Dispose();
                }
                catch
                {
                    // Continue with other processes even if one fails
                }
            }
        }
        catch
        {
            // Continue update even if we can't kill processes
        }
    }

    private static void ForceShutdown()
    {
        try
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                try
                {
                    App.splashScreen?.Shutdown();
                }
                catch { }
                
                try
                {
                    Application.Current?.Shutdown();
                }
                catch { }
            });
            
            Thread.Sleep(200);
        }
        catch { }
        
        try
        {
            Environment.Exit(0);
        }
        catch { }
    }
}
