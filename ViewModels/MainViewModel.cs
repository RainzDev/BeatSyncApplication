using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Avalonia.Media;
using BeatSync.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeatSync.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string selectedFolder = GetDefaultSelectedFolder();

    [ObservableProperty]
    private bool enableStartup;

    [ObservableProperty]
    private string? selectedVersion;

    [ObservableProperty]
    private string selectedBeatmapSource = "CustomLevels";

    [ObservableProperty]
    private BeatmapSourceOption? selectedBeatmapSourceOption;

    [ObservableProperty]
    private string localAddress = GetLocalAddress();

    [ObservableProperty]
    private string customLevelsSizeText = "0 MB";

    [ObservableProperty]
    private bool serverRunning = BeatmapHttpServer.IsRunning;

    [ObservableProperty]
    private SolidColorBrush serverStatusBrush = new(Colors.Green);

    public ObservableCollection<string> VersionOptions { get; } = new();

    public ObservableCollection<BeatmapSourceOption> BeatmapSourceOptions { get; } = new()
    {
        new("CustomLevels", "Custom Levels"),
        new("CustomWIPLevels", "Custom WIP Levels"),
        new("RemapperOutput", "Remapper Output"),
    };

    public MainViewModel()
    {
        var savedSettings = AppSettings.Load();

        if (!string.IsNullOrWhiteSpace(savedSettings.SelectedFolder))
        {
            SelectedFolder = savedSettings.SelectedFolder;
        }
        else
        {
            SelectedFolder = GetDefaultSelectedFolder();
        }

        EnableStartup = savedSettings.EnableStartup;
        SelectedBeatmapSource = GetBeatmapSourceName(savedSettings.SelectedBeatmapSource);
        SelectedBeatmapSourceOption = BeatmapSourceOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedBeatmapSource, StringComparison.OrdinalIgnoreCase));
        ServerRunning = BeatmapHttpServer.IsRunning;
        UpdateServerStatusBrush();
        BeatmapHttpServer.ServerStatusChanged += status =>
        {
            ServerRunning = status;
            UpdateServerStatusBrush();
        };
        RefreshVersionOptions();

        if (!string.IsNullOrWhiteSpace(savedSettings.SelectedVersion) && VersionOptions.Contains(savedSettings.SelectedVersion))
        {
            SelectedVersion = savedSettings.SelectedVersion;
        }

        UpdateCustomLevelsSizeText();
        ApplyStartupSetting(EnableStartup);
    }

    public static string GetDefaultSelectedFolder()
    {
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrWhiteSpace(appData))
            {
                candidates.Add(Path.Combine(appData, "BSManager", "BSInstances"));
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "BSManager", "BSInstances"));
            }

            if (!string.IsNullOrWhiteSpace(homePath))
            {
                candidates.Add(Path.Combine(homePath, "BSManager", "BSInstances"));
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (!string.IsNullOrWhiteSpace(homePath))
            {
                candidates.Add(Path.Combine(homePath, ".local", "share", "BSManager", "BSInstances"));
                candidates.Add(Path.Combine(homePath, ".config", "BSManager", "BSInstances"));
                candidates.Add(Path.Combine(homePath, "BSManager", "BSInstances"));
            }
        }

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates.FirstOrDefault() ?? homePath;
    }

    public static string GetLocalAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var address = host.AddressList
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip));

            return address is null ? "127.0.0.1" : address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    partial void OnServerRunningChanged(bool value)
    {
        UpdateServerStatusBrush();
    }

    private void UpdateServerStatusBrush()
    {
        ServerStatusBrush = new SolidColorBrush(ServerRunning ? Colors.Green : Colors.Red);
    }

    public void SaveSelectedFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        SelectedFolder = folderPath;
        RefreshVersionOptions();
        AppSettings.Save(SelectedFolder, EnableStartup, SelectedVersion, SelectedBeatmapSource);
    }

    public void RefreshVersionOptions()
    {
        VersionOptions.Clear();

        if (string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder))
        {
            SelectedVersion = null;
            return;
        }

        var validVersions = Directory.EnumerateDirectories(SelectedFolder)
            .Select(folder => new
            {
                FolderName = Path.GetFileName(folder),
                BeatSaberExe = Path.Combine(folder, OperatingSystem.IsWindows() ? "Beat Saber.exe" : "Beat Saber"),
            })
            .Where(x => File.Exists(x.BeatSaberExe))
            .Select(x => x.FolderName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var version in validVersions)
        {
            VersionOptions.Add(version);
        }

        if (string.IsNullOrWhiteSpace(SelectedVersion) || !VersionOptions.Contains(SelectedVersion))
        {
            SelectedVersion = VersionOptions.FirstOrDefault();
        }
    }

    partial void OnSelectedVersionChanged(string? value)
    {
        AppSettings.Save(SelectedFolder, EnableStartup, value, SelectedBeatmapSource);
        UpdateCustomLevelsSizeText();
    }

    partial void OnSelectedBeatmapSourceChanged(string value)
    {
        SelectedBeatmapSourceOption = BeatmapSourceOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));
        AppSettings.Save(SelectedFolder, EnableStartup, SelectedVersion, value);
        UpdateCustomLevelsSizeText();
    }

    partial void OnSelectedBeatmapSourceOptionChanged(BeatmapSourceOption? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedBeatmapSource = GetBeatmapSourceName(value.Value);
    }

    partial void OnEnableStartupChanged(bool value)
    {
        ApplyStartupSetting(value);
        AppSettings.Save(SelectedFolder, value, SelectedVersion, SelectedBeatmapSource);
    }

    public static string GetBeatmapSourceName(string? value)
    {
        if (string.Equals(value, "CustomWIPLevels", StringComparison.OrdinalIgnoreCase))
        {
            return "CustomWIPLevels";
        }

        if (string.Equals(value, "RemapperOutput", StringComparison.OrdinalIgnoreCase))
        {
            return "RemapperOutput";
        }

        return "CustomLevels";
    }

    public static string GetBeatmapRootPath(string selectedFolder, string? selectedVersion, string? selectedBeatmapSource)
    {
        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            return string.Empty;
        }

        var source = GetBeatmapSourceName(selectedBeatmapSource);
        if (string.Equals(source, "CustomWIPLevels", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(selectedFolder, selectedVersion, "Beat Saber_Data", "CustomWIPLevels");
        }

        if (string.Equals(source, "RemapperOutput", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(selectedFolder, selectedVersion, "Beat Saber_Data", "CustomWIPLevels", "OutputMaps");
        }

        return Path.Combine(selectedFolder, selectedVersion, "Beat Saber_Data", "CustomLevels");
    }

    public static string GetBeatmapRootPath(string versionFolder, string? selectedBeatmapSource)
    {
        var source = GetBeatmapSourceName(selectedBeatmapSource);
        if (string.Equals(source, "CustomWIPLevels", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(versionFolder, "Beat Saber_Data", "CustomWIPLevels");
        }

        if (string.Equals(source, "RemapperOutput", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(versionFolder, "Beat Saber_Data", "CustomWIPLevels", "OutputMaps");
        }

        return Path.Combine(versionFolder, "Beat Saber_Data", "CustomLevels");
    }

    public void UpdateCustomLevelsSizeText()
    {
        if (string.IsNullOrWhiteSpace(SelectedFolder) || string.IsNullOrWhiteSpace(SelectedVersion) || !Directory.Exists(SelectedFolder))
        {
            CustomLevelsSizeText = "0 MB";
            return;
        }

        var customLevelsPath = GetBeatmapRootPath(SelectedFolder, SelectedVersion, SelectedBeatmapSource);
        if (!Directory.Exists(customLevelsPath))
        {
            CustomLevelsSizeText = "0 MB";
            return;
        }

        var candidateFolders = Directory.EnumerateDirectories(customLevelsPath);

        if (string.Equals(SelectedBeatmapSource, "CustomWIPLevels", StringComparison.OrdinalIgnoreCase))
        {
            candidateFolders = candidateFolders.Where(folder => !ContainsAssetsFolder(folder));
        }

        long totalBytes = 0;
        foreach (var folderPath in candidateFolders)
        {
            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                var fileInfo = new FileInfo(filePath);
                totalBytes += fileInfo.Length;
            }
        }

        var sizeInMb = totalBytes / (double)(1024 * 1024);
        CustomLevelsSizeText = $"{sizeInMb:0.##} MB";
    }

    private static bool ContainsAssetsFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        return Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
            .Any(directory => string.Equals(Path.GetFileName(directory), "Assets", StringComparison.OrdinalIgnoreCase));
    }

    public sealed class BeatmapSourceOption
    {
        public BeatmapSourceOption(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; }
        public string Label { get; }
    }

    private static void ApplyStartupSetting(bool enableStartup)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var autostartDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
        Directory.CreateDirectory(autostartDir);

        var desktopFilePath = Path.Combine(autostartDir, "beatsyncapplication.desktop");
        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;

        if (enableStartup)
        {
            var desktopFile = "[Desktop Entry]\n" +
                              "Type=Application\n" +
                              "Name=BeatSync\n" +
                              "Exec=\"" + executablePath.Replace("\\", "\\\\") + "\"\n" +
                              "Terminal=false\n" +
                              "X-GNOME-Autostart-enabled=true\n";

            File.WriteAllText(desktopFilePath, desktopFile);
            return;
        }

        if (File.Exists(desktopFilePath))
        {
            File.Delete(desktopFilePath);
        }
    }
}
