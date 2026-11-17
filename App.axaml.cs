using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using BeatSync.ViewModels;
using BeatSync.Views;

namespace BeatSync;

public partial class App : Application
{

    // set to true to skip remote update checks during startup.
    private static bool BypassUpdateCheckerForTesting = false;

    // set to true to force the update prompt during local testing without publishing a new release.
    private static bool ForceUpdatePromptForTesting = false;

    private const string LatestReleaseEndpoint = "https://api.github.com/repos/RainzDev/BeatSyncApplication/releases/latest";
    private const string ReleaseListEndpoint = "https://api.github.com/repos/RainzDev/BeatSyncApplication/releases?per_page=8";

    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();

    private static void Log(string message)
    {
        AppLogger.Info(message);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var loadingWindow = new LoadingWindow();

            loadingWindow.Show();
            Log("Application startup began.");

            _ = Task.Run(async () =>
            {
                Log($"Update check config: bypass={BypassUpdateCheckerForTesting}, forcePrompt={ForceUpdatePromptForTesting}.");
                UpdateStatus updateStatus;
                if (BypassUpdateCheckerForTesting)
                {
                    loadingWindow.SetStatus("Update check bypassed for testing");
                    Log("Bypassing remote update check for testing.");
                    await Task.Delay(400);
                    updateStatus = UpdateStatus.UpToDate;
                }
                else if (ForceUpdatePromptForTesting)
                {
                    loadingWindow.SetStatus("Testing update prompt");
                    Log("Forcing the update prompt for local testing.");
                    await Task.Delay(400);
                    updateStatus = UpdateStatus.UpdateAvailable;
                }
                else
                {
                    Log("Starting remote update check.");
                    updateStatus = await CheckForUpdatesAsync(loadingWindow);
                    Log($"Remote update check finished with status: {updateStatus}.");
                }

                if (updateStatus == UpdateStatus.Failed)
                {
                    Log($"Update check failed with status {updateStatus}.");
                    loadingWindow.SetStatus("Could not check for updates. Starting app...");
                    await Task.Delay(900);
                }

                if (updateStatus == UpdateStatus.UpdateAvailable)
                {
                    Log("Update prompt requested.");
                    var shouldUpdate = await Dispatcher.UIThread.InvokeAsync(async () => await PromptForUpdateAsync(loadingWindow));
                    Log($"User chose to update: {shouldUpdate}.");
                    if (!shouldUpdate)
                    {
                        loadingWindow.SetStatus("Update skipped. Starting app...");
                        await Task.Delay(800);
                    }
                    else
                    {
                        try
                        {
                            Log("Starting automatic update process.");
                            await PerformAutoUpdateAsync(loadingWindow);
                        }
                        catch (Exception ex)
                        {
                            Log($"Auto-update failed: {ex}");
                            loadingWindow.SetStatus("Update failed. Starting app...");
                            await Task.Delay(1200);
                        }

                        Dispatcher.UIThread.Post(() =>
                        {
                            loadingWindow.Close();
                            desktop.Shutdown();
                        });
                        return;
                    }
                }

                Log("Application main window starting.");
                var viewModel = new MainViewModel();

                Dispatcher.UIThread.Post(() =>
                {
                    var mainWindow = new MainWindow
                    {
                        DataContext = viewModel,
                    };

                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    BeatmapHttpServer.Start();
                    mainWindow.Closed += (_, _) => BeatmapHttpServer.Stop();
                    loadingWindow.Close();
                });
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private enum UpdateStatus
    {
        UpToDate,
        UpdateAvailable,
        Failed,
    }

    private static async Task<UpdateStatus> CheckForUpdatesAsync(LoadingWindow loadingWindow)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Log($"Update check attempt {attempt}/{maxAttempts} starting.");
            loadingWindow.SetStatus(attempt == 1
                ? "Checking for updates..."
                : $"Retrying update check ({attempt}/{maxAttempts})...");

            try
            {
                var result = await TryCheckForUpdatesAsync(loadingWindow);
                Log($"Update check attempt {attempt}/{maxAttempts} returned {result}.");
                if (result == UpdateStatus.UpdateAvailable || result == UpdateStatus.UpToDate)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log($"Update check attempt {attempt} failed: {ex}");
            }

            if (attempt < maxAttempts)
            {
                var retryDelay = GetStartupRetryDelay(attempt);
                loadingWindow.SetStatus($"Could not reach GitHub. Retrying in {retryDelay.TotalSeconds:0} seconds...");
                await Task.Delay(retryDelay);
            }
        }

        loadingWindow.SetStatus("Unable to reach GitHub. Closing...");
        await Task.Delay(1200);
        return UpdateStatus.Failed;
    }

    private static async Task<UpdateStatus> TryCheckForUpdatesAsync(LoadingWindow loadingWindow)
    {
        var json = await TryGetLatestReleaseJsonAsync(UpdateHttpClient);
        if (string.IsNullOrWhiteSpace(json))
        {
            Log("Unable to fetch release metadata from GitHub after retries.");
            return UpdateStatus.Failed;
        }

        Log("GitHub release metadata received.");
        using var document = JsonDocument.Parse(json);
        if (!TryValidateReleaseMetadata(document.RootElement, out var validationMessage))
        {
            loadingWindow.SetStatus(validationMessage);
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        if (!document.RootElement.TryGetProperty("assets", out var assetsElement))
        {
            Log("GitHub release metadata had no assets array.");
            return UpdateStatus.Failed;
        }

        if (!TryGetMatchingAsset(assetsElement, out var assetElement))
        {
            Log("No matching platform release asset was found.");
            return UpdateStatus.Failed;
        }

        if (!TryGetManifestAsset(assetsElement, out var manifestAssetElement))
        {
            Log("Release manifest asset was missing from the GitHub release.");
            loadingWindow.SetStatus("Release manifest missing. Update aborted for safety.");
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        if (!TryGetAssetDigestSha256(assetElement, out var expectedAssetDigest))
        {
            Log("Release asset digest missing from GitHub metadata.");
            loadingWindow.SetStatus("Release asset digest missing. Update aborted for safety.");
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        var assetUrl = assetElement.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            return UpdateStatus.Failed;
        }

        var manifestUrl = manifestAssetElement.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            loadingWindow.SetStatus("Release manifest URL missing. Update aborted for safety.");
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        var releaseTag = document.RootElement.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? string.Empty
            : string.Empty;

        var assetName = assetElement.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return UpdateStatus.Failed;
        }

        var manifestJson = await TryGetStringWithRetriesAsync(UpdateHttpClient, manifestUrl, "release manifest");
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            loadingWindow.SetStatus("Could not download release manifest. Update aborted for safety.");
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        if (!TryValidateReleaseManifest(manifestJson, releaseTag, assetName, out var expectedManifestAssetHash, out var manifestValidationMessage))
        {
            loadingWindow.SetStatus(manifestValidationMessage);
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        var zipBytes = await TryGetByteArrayWithRetriesAsync(UpdateHttpClient, assetUrl, "release archive");
        if (zipBytes is null || zipBytes.Length == 0)
        {
            loadingWindow.SetStatus("Could not download update package. Please try again later.");
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        using var zipHashStream = new MemoryStream(zipBytes, writable: false);
        var downloadedZipHash = ComputeSha256(zipHashStream);

        if (!string.Equals(downloadedZipHash, expectedManifestAssetHash, StringComparison.OrdinalIgnoreCase))
        {
            loadingWindow.SetStatus("Release manifest hash mismatch. Update aborted for safety.");
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        if (!string.Equals(downloadedZipHash, expectedAssetDigest, StringComparison.OrdinalIgnoreCase))
        {
            loadingWindow.SetStatus("Release digest mismatch. Update aborted for safety.");
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        await using var zipStream = new MemoryStream(zipBytes, writable: false);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        var binaryName = GetCurrentBinaryFileName();
        var currentBinaryPath = GetCurrentBinaryPath(binaryName);
        if (string.IsNullOrWhiteSpace(currentBinaryPath) || !File.Exists(currentBinaryPath))
        {
            return UpdateStatus.Failed;
        }

        using var localBinaryStream = File.OpenRead(currentBinaryPath);
        var localHash = ComputeSha256(localBinaryStream);
        var releaseHash = ComputeSha256ForArchiveEntry(archive, binaryName);
        if (string.IsNullOrWhiteSpace(releaseHash))
        {
            return UpdateStatus.Failed;
        }

        if (string.Equals(localHash, releaseHash, StringComparison.OrdinalIgnoreCase))
        {
            loadingWindow.SetStatus("Setting up your beatmaps and settings");
            return UpdateStatus.UpToDate;
        }

        loadingWindow.SetStatus("Update available on GitHub");
        await Task.Delay(1200);
        return UpdateStatus.UpdateAvailable;
    }

    private static async Task PerformAutoUpdateAsync(LoadingWindow loadingWindow)
    {
        loadingWindow.SetStatus("Updating BeatSync...");

        var releaseJson = await TryGetLatestReleaseJsonAsync(UpdateHttpClient);
        if (string.IsNullOrWhiteSpace(releaseJson))
        {
            throw new InvalidOperationException("Failed to fetch latest release metadata after retries.");
        }

        using var releaseDocument = JsonDocument.Parse(releaseJson);
        if (!TryValidateReleaseMetadata(releaseDocument.RootElement, out var validationMessage))
        {
            throw new InvalidOperationException(validationMessage);
        }

        if (!releaseDocument.RootElement.TryGetProperty("assets", out var assetsElement))
        {
            throw new InvalidOperationException("Release asset list missing.");
        }

        if (!TryGetMatchingAsset(assetsElement, out var assetElement))
        {
            throw new InvalidOperationException("No matching release asset found for this platform.");
        }

        if (!TryGetManifestAsset(assetsElement, out var manifestAssetElement))
        {
            throw new InvalidOperationException("Release manifest missing.");
        }

        if (!TryGetAssetDigestSha256(assetElement, out var expectedAssetDigest))
        {
            throw new InvalidOperationException("Release asset digest missing.");
        }

        var assetUrl = assetElement.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            throw new InvalidOperationException("Release asset URL missing.");
        }

        var manifestUrl = manifestAssetElement.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            throw new InvalidOperationException("Release manifest URL missing.");
        }

        var releaseTag = releaseDocument.RootElement.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? string.Empty
            : string.Empty;

        var assetName = assetElement.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(assetName))
        {
            throw new InvalidOperationException("Release asset name missing.");
        }

        var manifestJson = await TryGetStringWithRetriesAsync(UpdateHttpClient, manifestUrl, "release manifest");
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            throw new InvalidOperationException("Failed to download release manifest.");
        }

        if (!TryValidateReleaseManifest(manifestJson, releaseTag, assetName, out var expectedManifestAssetHash, out var manifestValidationMessage))
        {
            throw new InvalidOperationException(manifestValidationMessage);
        }

        var zipBytes = await TryGetByteArrayWithRetriesAsync(UpdateHttpClient, assetUrl, "release archive");
        if (zipBytes is null || zipBytes.Length == 0)
        {
            throw new InvalidOperationException("Failed to download update package.");
        }

        using var zipHashStream = new MemoryStream(zipBytes, writable: false);
        var downloadedZipHash = ComputeSha256(zipHashStream);
        if (!string.Equals(downloadedZipHash, expectedManifestAssetHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Release manifest hash mismatch.");
        }

        if (!string.Equals(downloadedZipHash, expectedAssetDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Release digest mismatch.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "BeatSyncUpdate" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var zipPath = Path.Combine(tempRoot, "update.zip");
        await File.WriteAllBytesAsync(zipPath, zipBytes);

        var extractedPath = Path.Combine(tempRoot, "extracted");
        Directory.CreateDirectory(extractedPath);
        ZipFile.ExtractToDirectory(zipPath, extractedPath);

        var binaryName = GetCurrentBinaryFileName();
        var targetBinary = GetCurrentBinaryPath(binaryName);
        if (string.IsNullOrWhiteSpace(targetBinary))
        {
            throw new InvalidOperationException("Current binary path could not be resolved.");
        }

        var stagedRoot = GetStagedUpdateRoot(extractedPath, binaryName);
        var updaterPath = CreateRestartUpdater(targetBinary, stagedRoot, binaryName);

        if (!TryLaunchUpdaterWithElevation(updaterPath))
        {
            throw new InvalidOperationException("Administrator permission is required to install this update.");
        }

        loadingWindow.SetStatus("Restarting to finish update...");
        await Task.Delay(1200);
    }

    private static bool TryLaunchUpdaterWithElevation(string updaterPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (IsAdministrator())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = updaterPath,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Directory.GetCurrentDirectory(),
                    });
                    return true;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterPath,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Directory.GetCurrentDirectory(),
                });
                return true;
            }

            if (OperatingSystem.IsLinux())
            {
                if (IsAdministrator())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = updaterPath,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Directory.GetCurrentDirectory(),
                    });
                    return true;
                }

                var commandCandidates = new[]
                {
                    new[] { "pkexec", $"/bin/bash \"{updaterPath}\"" },
                    new[] { "sudo", $"\"{updaterPath}\"" },
                };

                foreach (var command in commandCandidates)
                {
                    if (!TryResolveExecutable(command[0], out var executablePath))
                    {
                        continue;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = command[1],
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Directory.GetCurrentDirectory(),
                    };

                    psi.Environment["DISPLAY"] = Environment.GetEnvironmentVariable("DISPLAY") ?? string.Empty;
                    psi.Environment["XAUTHORITY"] = Environment.GetEnvironmentVariable("XAUTHORITY") ?? string.Empty;

                    if (Process.Start(psi) is not null)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Log($"Update elevation request failed: {ex}");
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (identity is null)
                {
                    return false;
                }

                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (!File.Exists("/proc/self/status"))
                {
                    return false;
                }

                foreach (var line in File.ReadLines("/proc/self/status"))
                {
                    if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var uid))
                    {
                        return uid == 0;
                    }
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static bool TryResolveExecutable(string fileName, out string executablePath)
    {
        executablePath = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (File.Exists(fileName))
        {
            executablePath = fileName;
            return true;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                executablePath = candidate;
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> PromptForUpdateAsync(LoadingWindow loadingWindow)
    {
        if (OperatingSystem.IsWindows())
        {
            Log("Showing native Windows update prompt.");
            var nativeResult = ShowWindowsNativeUpdatePrompt();
            if (nativeResult.HasValue)
            {
                return nativeResult.Value;
            }

            Log("Native Windows update prompt failed. Falling back to in-app dialog.");
            return await ShowFallbackDialogAsync(loadingWindow);
        }

        if (OperatingSystem.IsLinux())
        {
            var dialogCommands = new[]
            {
                new[] { "zenity", "--question --title=\"Update available\" --text=\"A newer version is available. Would you like to download and install the update now?\" --ok-label=Yes --cancel-label=No" },
                new[] { "kdialog", "--yesno \"A newer version is available. Would you like to download and install the update now?\"" },
            };

            foreach (var command in dialogCommands)
            {
                var fileName = command[0];
                var arguments = command[1];
                var commandPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(commandPath) || !commandPath.Split(Path.PathSeparator).Any(path => File.Exists(Path.Combine(path, fileName))))
                {
                    continue;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                };

                using var process = Process.Start(psi);
                if (process is null)
                {
                    continue;
                }

                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
        }

        return await ShowFallbackDialogAsync(loadingWindow);
    }

    private static bool? ShowWindowsNativeUpdatePrompt()
    {
        try
        {
            const uint mbYesNo = 0x00000004;
            const uint mbIconQuestion = 0x00000020;
            const uint mbSystemModal = 0x00001000;
            const int idYes = 6;

            var result = MessageBoxW(
                IntPtr.Zero,
                "A newer version is available. Would you like to download and install the update now?",
                "Update available",
                mbYesNo | mbIconQuestion | mbSystemModal);

            return result == idYes;
        }
        catch (Exception ex)
        {
            Log($"Native Windows update prompt failed: {ex.Message}");
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static async Task<bool> ShowFallbackDialogAsync(LoadingWindow loadingWindow)
    {
        var dialog = new Window
        {
            Title = "Update available",
            Width = 440,
            Height = 210,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Topmost = true,
        };

        var header = new TextBlock
        {
            Text = "A newer version is available.",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var message = new TextBlock
        {
            Text = "Would you like to download and install the update now? The current version will remain in place if you choose No.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var noButton = new Button
        {
            Content = "No",
            Width = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var yesButton = new Button
        {
            Content = "Yes",
            Width = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
            Children = { noButton, yesButton },
        };

        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Children = { header, message, actions },
        };

        dialog.Content = content;
        noButton.Click += (_, _) => dialog.Close(false);
        yesButton.Click += (_, _) => dialog.Close(true);

        var result = await dialog.ShowDialog<bool?>(loadingWindow);
        return result == true;
    }

    private static bool TryValidateReleaseMetadata(JsonElement releaseElement, out string validationMessage)
    {
        validationMessage = string.Empty;

        var isImmutable = releaseElement.TryGetProperty("immutable", out var immutableElement) &&
            immutableElement.ValueKind == JsonValueKind.True;

        if (!isImmutable)
        {
            // GitHub exposes this field but does not reliably allow setting it for all release flows.
            // Continue with manifest-attestation + hash validation instead of blocking startup.
            validationMessage = "GitHub immutable flag is unavailable for this release. Continuing with manifest attestation and hash checks.";
        }

        return true;
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(12),
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncApplication/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static TimeSpan GetStartupRetryDelay(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, attempt + 1));
        var jitter = Random.Shared.NextDouble() * 2.5;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == HttpStatusCode.TooManyRequests ||
               (int)statusCode >= 500;
    }

    private static async Task<string?> TryGetLatestReleaseJsonAsync(HttpClient client)
    {
        var latestJson = await TryGetStringWithRetriesAsync(client, LatestReleaseEndpoint, "latest release metadata");
        if (!string.IsNullOrWhiteSpace(latestJson))
        {
            return latestJson;
        }

        Log("Falling back to release list endpoint to resolve latest stable release metadata.");
        var releasesJson = await TryGetStringWithRetriesAsync(client, ReleaseListEndpoint, "release list metadata");
        if (string.IsNullOrWhiteSpace(releasesJson))
        {
            return null;
        }

        try
        {
            using var releasesDocument = JsonDocument.Parse(releasesJson);
            if (releasesDocument.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var release in releasesDocument.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var isDraft = release.TryGetProperty("draft", out var draftElement) && draftElement.ValueKind == JsonValueKind.True;
                var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseElement) && prereleaseElement.ValueKind == JsonValueKind.True;

                if (isDraft || isPrerelease)
                {
                    continue;
                }

                return release.GetRawText();
            }
        }
        catch (Exception ex)
        {
            Log($"Failed parsing fallback release list metadata: {ex.Message}");
        }

        return null;
    }

    private static async Task<string?> TryGetStringWithRetriesAsync(HttpClient client, string url, string operationName)
    {
        using var response = await TryGetResponseWithRetriesAsync(client, url, operationName);
        if (response is null)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<byte[]?> TryGetByteArrayWithRetriesAsync(HttpClient client, string url, string operationName)
    {
        using var response = await TryGetResponseWithRetriesAsync(client, url, operationName);
        if (response is null)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<HttpResponseMessage?> TryGetResponseWithRetriesAsync(HttpClient client, string url, string operationName)
    {
        const int maxAttempts = 4;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (!IsTransientStatusCode(response.StatusCode) || attempt == maxAttempts)
                {
                    Log($"{operationName} request failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
                    response.Dispose();
                    return null;
                }

                Log($"{operationName} transient HTTP {(int)response.StatusCode} on attempt {attempt}/{maxAttempts}. Retrying...");
                response.Dispose();
            }
            catch (TaskCanceledException ex)
            {
                if (attempt == maxAttempts)
                {
                    Log($"{operationName} timed out after {maxAttempts} attempts: {ex.Message}");
                    return null;
                }

                Log($"{operationName} timed out on attempt {attempt}/{maxAttempts}. Retrying...");
            }
            catch (HttpRequestException ex)
            {
                if (attempt == maxAttempts)
                {
                    Log($"{operationName} network error after {maxAttempts} attempts: {ex.Message}");
                    return null;
                }

                Log($"{operationName} network error on attempt {attempt}/{maxAttempts}: {ex.Message}. Retrying...");
            }

            var delaySeconds = Math.Pow(2, attempt - 1) + Random.Shared.NextDouble();
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }

        return null;
    }

    private static string CreateRestartUpdater(string targetBinary, string replacementRoot, string binaryName)
    {
        var tempScriptDir = Path.Combine(Path.GetTempPath(), "BeatSyncUpdater" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempScriptDir);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempScriptDir, "apply-update.cmd");
            var script = BuildWindowsUpdaterScript(targetBinary, replacementRoot, binaryName);
            File.WriteAllText(scriptPath, script);
            return scriptPath;
        }

        var shellPath = Path.Combine(tempScriptDir, "apply-update.sh");
        var shellScript = BuildLinuxUpdaterScript(targetBinary, replacementRoot, binaryName, tempScriptDir);

        File.WriteAllText(shellPath, shellScript);
        var chmodInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"chmod +x '{shellPath}'\"",
            UseShellExecute = false,
        };
        Process.Start(chmodInfo)?.WaitForExit();
        return shellPath;
    }

    public static string BuildLinuxUpdaterScript(string targetBinary, string replacementRoot, string binaryName, string? scriptDirectory = null)
    {
        var appDirectory = Path.GetDirectoryName(targetBinary) ?? Directory.GetCurrentDirectory();
        var cleanupDirectory = string.IsNullOrWhiteSpace(scriptDirectory)
            ? Path.GetDirectoryName(targetBinary) ?? Directory.GetCurrentDirectory()
            : scriptDirectory;

        return $"#!/bin/bash\n" +
               $"sleep 2\n" +
               $"TARGET=\"{targetBinary}\"\n" +
               $"APP_DIR=\"{appDirectory}\"\n" +
               $"STAGED_DIR=\"{replacementRoot}\"\n" +
               $"while pgrep -f \"{binaryName}\" >/dev/null 2>&1; do sleep 2; done\n" +
               $"mkdir -p \"$APP_DIR\"\n" +
               $"cp -a \"$STAGED_DIR\"/. \"$APP_DIR\"/\n" +
               $"chmod +x \"$TARGET\"\n" +
               $"nohup \"$TARGET\" >/dev/null 2>&1 &\n" +
               $"rm -rf \"{cleanupDirectory}\"\n";
    }

    public static string BuildWindowsUpdaterScript(string targetBinary, string replacementRoot, string binaryName)
    {
        var appDirectory = Path.GetDirectoryName(targetBinary) ?? Directory.GetCurrentDirectory();

        var script = $"@echo off\r\n" +
                     $"setlocal\r\n" +
                     $"set \"TARGET={targetBinary}\"\r\n" +
                     $"set \"APP_DIR={appDirectory}\"\r\n" +
                     $"set \"STAGED_DIR={replacementRoot}\"\r\n" +
                     $":wait_for_exit\r\n" +
                     $"tasklist /FI \"IMAGENAME eq {binaryName}\" 2>NUL | find /I \"{binaryName}\" >NUL\r\n" +
                     $"if not errorlevel 1 (\r\n" +
                     $"  timeout /t 2 /nobreak >nul\r\n" +
                     $"  goto wait_for_exit\r\n" +
                     $")\r\n" +
                     $"if not exist \"%APP_DIR%\" mkdir \"%APP_DIR%\"\r\n" +
                     $"robocopy \"%STAGED_DIR%\" \"%APP_DIR%\" /E /COPY:DAT /R:1 /W:1 >nul\r\n" +
                     $"if errorlevel 8 (\r\n" +
                     $"  exit /b 1\r\n" +
                     $")\r\n" +
                     $"start \"\" \"%TARGET%\"\r\n" +
                     $"exit /b 0\r\n";

        return script;
    }

    private static bool TryGetMatchingAsset(JsonElement assetsElement, out JsonElement assetElement)
    {
        var targetName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : null;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            assetElement = default;
            return false;
        }

        foreach (var asset in assetsElement.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (name.Contains(targetName, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                assetElement = asset;
                return true;
            }
        }

        assetElement = default;
        return false;
    }

    private static bool TryGetManifestAsset(JsonElement assetsElement, out JsonElement manifestAssetElement)
    {
        foreach (var asset in assetsElement.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (string.Equals(name, "release-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                manifestAssetElement = asset;
                return true;
            }
        }

        manifestAssetElement = default;
        return false;
    }

    private static bool TryGetAssetDigestSha256(JsonElement assetElement, out string sha256Digest)
    {
        sha256Digest = string.Empty;
        if (!assetElement.TryGetProperty("digest", out var digestElement))
        {
            return false;
        }

        var digest = digestElement.GetString();
        if (string.IsNullOrWhiteSpace(digest))
        {
            return false;
        }

        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        sha256Digest = digest[prefix.Length..].Trim().ToLowerInvariant();
        return sha256Digest.Length > 0;
    }

    private static bool TryValidateReleaseManifest(
        string manifestJson,
        string releaseTag,
        string assetName,
        out string expectedAssetHash,
        out string validationMessage)
    {
        expectedAssetHash = string.Empty;
        validationMessage = string.Empty;

        try
        {
            using var manifestDocument = JsonDocument.Parse(manifestJson);
            var manifestRoot = manifestDocument.RootElement;

            if (!manifestRoot.TryGetProperty("tag_name", out var tagElement))
            {
                validationMessage = "Release manifest missing tag_name. Update aborted for safety.";
                return false;
            }

            var manifestTag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(manifestTag) || !string.Equals(manifestTag, releaseTag, StringComparison.OrdinalIgnoreCase))
            {
                validationMessage = "Release manifest tag mismatch. Update aborted for safety.";
                return false;
            }

            if (!manifestRoot.TryGetProperty("attestation", out var attestationElement) || attestationElement.ValueKind != JsonValueKind.Object)
            {
                validationMessage = "Release manifest attestation is missing. Update aborted for safety.";
                return false;
            }

            var provider = attestationElement.TryGetProperty("provider", out var providerElement)
                ? providerElement.GetString()
                : null;

            var source = attestationElement.TryGetProperty("source", out var sourceElement)
                ? sourceElement.GetString()
                : null;

            var verified = attestationElement.TryGetProperty("verified", out var verifiedElement) &&
                verifiedElement.ValueKind == JsonValueKind.True;

            if (!verified ||
                !string.Equals(provider, "github-actions", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(source, "actions/attest-build-provenance", StringComparison.OrdinalIgnoreCase))
            {
                validationMessage = "Release manifest attestation is invalid. Update aborted for safety.";
                return false;
            }

            if (!manifestRoot.TryGetProperty("artifacts", out var artifactsElement) || artifactsElement.ValueKind != JsonValueKind.Array)
            {
                validationMessage = "Release manifest artifacts are missing. Update aborted for safety.";
                return false;
            }

            foreach (var artifact in artifactsElement.EnumerateArray())
            {
                var name = artifact.TryGetProperty("name", out var artifactNameElement) ? artifactNameElement.GetString() : null;
                if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hash = artifact.TryGetProperty("sha256", out var hashElement) ? hashElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(hash))
                {
                    validationMessage = "Release manifest hash is missing. Update aborted for safety.";
                    return false;
                }

                expectedAssetHash = hash.Trim().ToLowerInvariant();
                return true;
            }

            validationMessage = "Release asset missing from release manifest. Update aborted for safety.";
            return false;
        }
        catch
        {
            validationMessage = "Release manifest is invalid JSON. Update aborted for safety.";
            return false;
        }
    }

    private static string GetCurrentBinaryFileName()
    {
        return OperatingSystem.IsWindows() ? "BeatSync.exe" : "BeatSync";
    }

    private static string GetCurrentBinaryPath(string binaryName)
    {
        var appDirectoryBinary = Path.Combine(AppContext.BaseDirectory, binaryName);
        if (File.Exists(appDirectoryBinary))
        {
            return appDirectoryBinary;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && string.Equals(Path.GetFileName(processPath), binaryName, StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return string.Empty;
    }

    private static string GetStagedUpdateRoot(string extractedPath, string binaryName)
    {
        var directBinaryPath = Path.Combine(extractedPath, binaryName);
        if (File.Exists(directBinaryPath))
        {
            return extractedPath;
        }

        var matchingDirectory = Directory.EnumerateDirectories(extractedPath, "*", SearchOption.AllDirectories)
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, binaryName)));

        if (!string.IsNullOrWhiteSpace(matchingDirectory))
        {
            return matchingDirectory;
        }

        return extractedPath;
    }

    private static string ComputeSha256ForArchiveEntry(ZipArchive archive, string binaryName)
    {
        var entry = archive.Entries.FirstOrDefault(x => string.Equals(Path.GetFileName(x.FullName), binaryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return string.Empty;
        }

        using var entryStream = entry.Open();
        return ComputeSha256(entryStream);
    }

    private static string ComputeSha256(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
