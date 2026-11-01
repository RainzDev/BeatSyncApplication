using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var loadingWindow = new LoadingWindow
            {
                Topmost = true,
            };

            loadingWindow.Deactivated += (_, _) =>
            {
                if (loadingWindow.IsVisible)
                {
                    loadingWindow.Topmost = true;
                    loadingWindow.Activate();
                }
            };

            loadingWindow.Show();

            _ = Task.Run(async () =>
            {
                UpdateStatus updateStatus;
                if (BypassUpdateCheckerForTesting)
                {
                    loadingWindow.SetStatus("Update check bypassed for testing");
                    await Task.Delay(400);
                    updateStatus = UpdateStatus.UpToDate;
                }
                else if (ForceUpdatePromptForTesting)
                {
                    loadingWindow.SetStatus("Testing update prompt");
                    await Task.Delay(400);
                    updateStatus = UpdateStatus.UpdateAvailable;
                }
                else
                {
                    updateStatus = await CheckForUpdatesAsync(loadingWindow);
                }

                if (updateStatus == UpdateStatus.Failed)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        loadingWindow.Close();
                        desktop.Shutdown();
                    });
                    return;
                }

                if (updateStatus == UpdateStatus.UpdateAvailable)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (loadingWindow.IsVisible)
                        {
                            loadingWindow.Topmost = true;
                            loadingWindow.Activate();
                        }
                    });

                    var shouldUpdate = await Dispatcher.UIThread.InvokeAsync(async () => await PromptForUpdateAsync(loadingWindow));
                    if (!shouldUpdate)
                    {
                        loadingWindow.SetStatus("Update skipped. Starting app...");
                        await Task.Delay(800);
                    }
                    else
                    {
                        try
                        {
                            await PerformAutoUpdateAsync(loadingWindow);
                        }
                        catch
                        {
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
            loadingWindow.SetStatus(attempt == 1
                ? "Checking for updates..."
                : $"Retrying update check ({attempt}/{maxAttempts})...");

            try
            {
                var result = await TryCheckForUpdatesAsync(loadingWindow);
                if (result == UpdateStatus.UpdateAvailable || result == UpdateStatus.UpToDate)
                {
                    return result;
                }
            }
            catch
            {
                // Retry on network or release-check failures.
            }

            if (attempt < maxAttempts)
            {
                loadingWindow.SetStatus("Could not reach GitHub. Retrying in 15 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        }

        loadingWindow.SetStatus("Unable to reach GitHub. Closing...");
        await Task.Delay(1200);
        return UpdateStatus.Failed;
    }

    private static async Task<UpdateStatus> TryCheckForUpdatesAsync(LoadingWindow loadingWindow)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncApplication/1.0");
        client.Timeout = TimeSpan.FromSeconds(20);

        using var response = await client.GetAsync("https://api.github.com/repos/RainzDev/BeatSyncApplication/releases/latest");
        if (!response.IsSuccessStatusCode)
        {
            return UpdateStatus.Failed;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        if (!TryValidateReleaseMetadata(document.RootElement, out var validationMessage))
        {
            loadingWindow.SetStatus(validationMessage);
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        if (!document.RootElement.TryGetProperty("assets", out var assetsElement))
        {
            return UpdateStatus.Failed;
        }

        if (!TryGetMatchingAsset(assetsElement, out var assetElement))
        {
            return UpdateStatus.Failed;
        }

        if (!TryGetManifestAsset(assetsElement, out var manifestAssetElement))
        {
            loadingWindow.SetStatus("Release manifest missing. Update aborted for safety.");
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        if (!TryGetAssetDigestSha256(assetElement, out var expectedAssetDigest))
        {
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

        var manifestJson = await client.GetStringAsync(manifestUrl);
        if (!TryValidateReleaseManifest(manifestJson, releaseTag, assetName, out var expectedManifestAssetHash, out var manifestValidationMessage))
        {
            loadingWindow.SetStatus(manifestValidationMessage);
            await Task.Delay(1200);
            return UpdateStatus.Failed;
        }

        var zipBytes = await client.GetByteArrayAsync(assetUrl);
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

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncApplication/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);

        using var releaseResponse = await client.GetAsync("https://api.github.com/repos/RainzDev/BeatSyncApplication/releases/latest");
        if (!releaseResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Failed to fetch latest release metadata.");
        }

        var releaseJson = await releaseResponse.Content.ReadAsStringAsync();
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

        var manifestJson = await client.GetStringAsync(manifestUrl);
        if (!TryValidateReleaseManifest(manifestJson, releaseTag, assetName, out var expectedManifestAssetHash, out var manifestValidationMessage))
        {
            throw new InvalidOperationException(manifestValidationMessage);
        }

        var zipBytes = await client.GetByteArrayAsync(assetUrl);
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
        var replacementBinary = Directory.GetFiles(extractedPath, binaryName, SearchOption.AllDirectories)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(replacementBinary))
        {
            throw new InvalidOperationException("Replacement binary not found in the downloaded update package.");
        }

        var targetBinary = GetCurrentBinaryPath(binaryName);
        if (string.IsNullOrWhiteSpace(targetBinary))
        {
            throw new InvalidOperationException("Current binary path could not be resolved.");
        }

        var updaterPath = CreateRestartUpdater(targetBinary, replacementBinary, binaryName);
        Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Directory.GetCurrentDirectory(),
        });

        loadingWindow.SetStatus("Restarting to finish update...");
        await Task.Delay(1200);
    }

    private static async Task<bool> PromptForUpdateAsync(LoadingWindow loadingWindow)
    {
        var shouldHideLoadingWindow = false;
        Dispatcher.UIThread.Invoke(() =>
        {
            if (loadingWindow.IsVisible)
            {
                loadingWindow.Hide();
                shouldHideLoadingWindow = true;
            }
        });

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"[System.Windows.Forms.MessageBox]::Show('A newer version is available. Would you like to download and install the update now?', 'Update available', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question) -eq [System.Windows.Forms.DialogResult]::Yes\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                };

                using var process = Process.Start(psi);
                if (process is null)
                {
                    return await ShowFallbackDialogAsync(loadingWindow);
                }

                await process.WaitForExitAsync();
                return process.ExitCode == 0;
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
        finally
        {
            if (shouldHideLoadingWindow)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!loadingWindow.IsVisible)
                    {
                        loadingWindow.Show();
                        loadingWindow.Topmost = true;
                        loadingWindow.Activate();
                    }
                });
            }
        }
    }

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

    private static string CreateRestartUpdater(string targetBinary, string replacementBinary, string binaryName)
    {
        var tempScriptDir = Path.Combine(Path.GetTempPath(), "BeatSyncUpdater" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempScriptDir);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempScriptDir, "apply-update.cmd");
            var script = $"@echo off\r\nsetlocal\r\n" +
                         $"ping -n 4 127.0.0.1 > nul\r\n" +
                         $"set \"TARGET={targetBinary}\"\r\n" +
                         $"set \"NEW={replacementBinary}\"\r\n" +
                         $"set \"BACKUP={targetBinary}.bak\"\r\n" +
                         $"if exist \"%BACKUP%\" del /f /q \"%BACKUP%\"\r\n" +
                         $"ren \"%TARGET%\" \"{binaryName}.bak\"\r\n" +
                         $"copy /Y \"%NEW%\" \"%TARGET%\" > nul\r\n" +
                         $"start \"\" \"%TARGET%\"\r\n" +
                         $"exit /b 0\r\n";

            File.WriteAllText(scriptPath, script);
            return scriptPath;
        }

        var shellPath = Path.Combine(tempScriptDir, "apply-update.sh");
        var shellScript = $"#!/bin/bash\n" +
                          $"sleep 2\n" +
                          $"TARGET=\"{targetBinary}\"\n" +
                          $"NEW=\"{replacementBinary}\"\n" +
                          $"BACKUP=\"${{TARGET}}.bak\"\n" +
                          $"rm -f \"$BACKUP\"\n" +
                          $"mv -f \"$TARGET\" \"$BACKUP\"\n" +
                          $"cp -f \"$NEW\" \"$TARGET\"\n" +
                          $"chmod +x \"$TARGET\"\n" +
                          $"nohup \"$TARGET\" >/dev/null 2>&1 &\n" +
                          $"rm -rf \"{tempScriptDir}\"\n";

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
