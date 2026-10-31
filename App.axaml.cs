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

        var assetUrl = assetElement.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            return UpdateStatus.Failed;
        }

        using var zipResponse = await client.GetAsync(assetUrl);
        if (!zipResponse.IsSuccessStatusCode)
        {
            return UpdateStatus.Failed;
        }

        await using var zipStream = await zipResponse.Content.ReadAsStreamAsync();
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

        var assetUrl = assetElement.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            throw new InvalidOperationException("Release asset URL missing.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "BeatSyncUpdate" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var zipPath = Path.Combine(tempRoot, "update.zip");
        using (var downloadResponse = await client.GetAsync(assetUrl))
        {
            if (!downloadResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Failed to download update package.");
            }

            await using var destinationStream = File.Create(zipPath);
            await downloadResponse.Content.CopyToAsync(destinationStream);
        }

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

        var hasAttestation = releaseElement.TryGetProperty("attestations", out var attestationElement) &&
            attestationElement.ValueKind == JsonValueKind.Array &&
            attestationElement.GetArrayLength() > 0;

        if (!hasAttestation && releaseElement.TryGetProperty("verification", out var verificationElement))
        {
            hasAttestation = verificationElement.TryGetProperty("verified_at", out _) ||
                verificationElement.TryGetProperty("verified_by", out _);
        }

        if (!hasAttestation && releaseElement.TryGetProperty("body", out var bodyElement))
        {
            var body = bodyElement.GetString();
            hasAttestation = body is not null && body.Contains("attestation", StringComparison.OrdinalIgnoreCase);
        }

        if (!isImmutable)
        {
            validationMessage = "GitHub release is not marked immutable. Update aborted for safety.";
            return false;
        }

        if (!hasAttestation)
        {
            validationMessage = "GitHub release has no attestation metadata. Update aborted for safety.";
            return false;
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
