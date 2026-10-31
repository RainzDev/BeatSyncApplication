using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BeatSync.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace BeatSync.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string selectedFolder = GetDefaultSelectedFolder();

    [ObservableProperty]
    private bool enableStartup;

    [ObservableProperty]
    private bool hideBeatmapList = true;

    [ObservableProperty]
    private string? selectedVersion;

    [ObservableProperty]
    private string selectedBeatmapSource = "CustomLevels";

    [ObservableProperty]
    private string beatmapSearchText = string.Empty;

    public IEnumerable<BeatmapEntryViewModel> FilteredBeatmapEntries =>
        string.IsNullOrWhiteSpace(BeatmapSearchText)
            ? BeatmapEntries
            : BeatmapEntries.Where(entry => entry.Name.Contains(BeatmapSearchText, StringComparison.OrdinalIgnoreCase));

    public bool SelectionEnabled
    {
        get => !HideBeatmapList;
        set => HideBeatmapList = !value;
    }

    public bool HideBeatmapListToggle
    {
        get => SelectionEnabled;
        set => SelectionEnabled = value;
    }

    [ObservableProperty]
    private BeatmapSourceOption? selectedBeatmapSourceOption;

    [ObservableProperty]
    private string localAddress = GetLocalAddress();

    [ObservableProperty]
    private int serverPort = AppSettings.Load().ServerPort;

    [ObservableProperty]
    private string customLevelsSizeText = "0 MB";

    [ObservableProperty]
    private bool serverRunning = BeatmapHttpServer.IsRunning;

    [ObservableProperty]
    private SolidColorBrush serverStatusBrush = new(Colors.Green);

    [ObservableProperty]
    private string beatmapJsonPreview = "{}";

    public ObservableCollection<string> VersionOptions { get; } = new();

    public ObservableCollection<BeatmapEntryViewModel> BeatmapEntries { get; } = new();

    public ObservableCollection<BeatmapEntryViewModel> SelectedBeatmaps { get; } = new();

    private readonly Dictionary<string, List<string>> _beatmapContentsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _beatmapSizeCache = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _selectionRefreshTimer;

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
        HideBeatmapList = savedSettings.HideBeatmapList;
        ServerPort = savedSettings.ServerPort is > 0 and < 65536 ? savedSettings.ServerPort : 5050;
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

        RefreshBeatmapEntries();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateCustomLevelsSizeText();
            UpdateBeatmapJsonPreview();
        }, DispatcherPriority.Background);
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
        RefreshBeatmapEntries();
        AppSettings.Save(SelectedFolder, EnableStartup, SelectedVersion, SelectedBeatmapSource, HideBeatmapList);
    }

    public void UpdateSelectedBeatmaps(IEnumerable<BeatmapEntryViewModel> selectedEntries)
    {
        SelectedBeatmaps.Clear();

        foreach (var entry in selectedEntries)
        {
            SelectedBeatmaps.Add(entry);
        }

        AppSettings.Save(SelectedFolder, EnableStartup, SelectedVersion, SelectedBeatmapSource, HideBeatmapList, SelectedBeatmaps.Select(entry => entry.Name), ServerPort);
        QueueSelectionRefresh();
    }

    private void QueueSelectionRefresh()
    {
        _selectionRefreshTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };

        _selectionRefreshTimer.Stop();
        _selectionRefreshTimer.Tick -= SelectionRefreshTimer_Tick;
        _selectionRefreshTimer.Tick += SelectionRefreshTimer_Tick;
        _selectionRefreshTimer.Start();
    }

    private void SelectionRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _selectionRefreshTimer?.Stop();
        UpdateCustomLevelsSizeText();
        UpdateBeatmapJsonPreview();
    }

    public void UpdateServerPort(int port)
    {
        if (port is <= 0 or >= 65536)
        {
            return;
        }

        ServerPort = port;
        AppSettings.Save(SelectedFolder, EnableStartup, SelectedVersion, SelectedBeatmapSource, HideBeatmapList, SelectedBeatmaps.Select(entry => entry.Name), port);
        _ = BeatmapHttpServer.RestartAsync(port);
    }

    public void RefreshBeatmapEntries()
    {
        _beatmapContentsCache.Clear();
        _beatmapSizeCache.Clear();
        BeatmapEntries.Clear();
        SelectedBeatmaps.Clear();

        if (string.IsNullOrWhiteSpace(SelectedFolder) || string.IsNullOrWhiteSpace(SelectedVersion) || !Directory.Exists(SelectedFolder))
        {
            OnPropertyChanged(nameof(FilteredBeatmapEntries));
            BeatmapJsonPreview = "{}";
            return;
        }

        var beatmapRootPath = GetBeatmapRootPath(SelectedFolder, SelectedVersion, SelectedBeatmapSource);
        if (!Directory.Exists(beatmapRootPath))
        {
            OnPropertyChanged(nameof(FilteredBeatmapEntries));
            BeatmapJsonPreview = "{}";
            return;
        }

        foreach (var beatmapFolder in Directory.EnumerateDirectories(beatmapRootPath)
                     .OrderBy(folder => Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
        {
            var imagePath = FindBeatmapImage(beatmapFolder);
            var image = LoadBeatmapImage(beatmapFolder);
            BeatmapEntries.Add(new BeatmapEntryViewModel
            {
                Name = Path.GetFileName(beatmapFolder) ?? "Unknown",
                ImagePath = imagePath,
                Image = image,
                HasImage = image is not null,
            });
        }

        OnPropertyChanged(nameof(FilteredBeatmapEntries));
        UpdateBeatmapJsonPreview();
    }

    private void UpdateBeatmapJsonPreview()
    {
        var items = !HideBeatmapList && SelectedBeatmaps.Count > 0
            ? SelectedBeatmaps
            : BeatmapEntries;

        var payload = new
        {
            version = new
            {
                name = SelectedVersion ?? "Unknown",
                source = SelectedBeatmapSource,
                customLevels = items.Select(item => new
                {
                    name = item.Name,
                    contents = GetBeatmapContents(item.Name),
                }).ToList(),
            }
        };

        BeatmapJsonPreview = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }

    private List<string> GetBeatmapContents(string beatmapName)
    {
        if (string.IsNullOrWhiteSpace(SelectedFolder) || string.IsNullOrWhiteSpace(SelectedVersion))
        {
            return new List<string>();
        }

        var beatmapRootPath = GetBeatmapRootPath(SelectedFolder, SelectedVersion, SelectedBeatmapSource);
        if (string.IsNullOrWhiteSpace(beatmapRootPath) || !Directory.Exists(beatmapRootPath))
        {
            return new List<string>();
        }

        var beatmapFolderPath = Path.Combine(beatmapRootPath, beatmapName);
        if (!Directory.Exists(beatmapFolderPath))
        {
            return new List<string>();
        }

        var cacheKey = beatmapFolderPath;
        if (_beatmapContentsCache.TryGetValue(cacheKey, out var cachedEntries))
        {
            return cachedEntries;
        }

        var entries = Directory.EnumerateFileSystemEntries(beatmapFolderPath)
            .Where(entry => ShouldIncludeBeatmapEntry(entry, beatmapFolderPath))
            .Select(entry => Path.GetFileName(entry) ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _beatmapContentsCache[cacheKey] = entries;
        return entries;
    }

    private static bool ShouldIncludeBeatmapEntry(string path, string parentFolderPath)
    {
        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var entryName = Path.GetFileName(path);
        var parentFolderName = Path.GetFileName(parentFolderPath);

        if (Directory.Exists(path))
        {
            if (string.Equals(entryName, "autosaves", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(entryName, parentFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (ShouldHideBeatmapEntry(path))
            {
                return false;
            }
        }

        return !ShouldHideBeatmapEntry(path);
    }

    private static bool ShouldHideBeatmapEntry(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        return Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
            .Any(directory => string.Equals(Path.GetFileName(directory), "Assets", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindBeatmapImage(string beatmapFolder)
    {
        return Directory.EnumerateFiles(beatmapFolder, "*.*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static Bitmap? LoadBeatmapImage(string beatmapFolder)
    {
        var imagePath = FindBeatmapImage(beatmapFolder);
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            return new Bitmap(imagePath);
        }
        catch
        {
            return null;
        }
    }

    public static string? GetBeatSaberExecutablePath(string versionFolder)
    {
        var candidates = new[]
        {
            Path.Combine(versionFolder, "Beat Saber.exe"),
            Path.Combine(versionFolder, "Beat Saber"),
            Path.Combine(versionFolder, "Beat Saber.x86_64"),
            Path.Combine(versionFolder, "Beat Saber.x86_64.bin"),
        };

        return candidates.FirstOrDefault(File.Exists);
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
                BeatSaberExe = GetBeatSaberExecutablePath(folder),
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.BeatSaberExe))
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
        AppSettings.Save(SelectedFolder, EnableStartup, value, SelectedBeatmapSource, HideBeatmapList);
        RefreshBeatmapEntries();
        UpdateCustomLevelsSizeText();
    }

    partial void OnSelectedBeatmapSourceChanged(string value)
    {
        SelectedBeatmapSourceOption = BeatmapSourceOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));
        AppSettings.Save(SelectedFolder, EnableStartup, SelectedVersion, value, HideBeatmapList);
        RefreshBeatmapEntries();
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
        AppSettings.Save(SelectedFolder, value, SelectedVersion, SelectedBeatmapSource, HideBeatmapList);
    }

    partial void OnHideBeatmapListChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectionEnabled));
        OnPropertyChanged(nameof(HideBeatmapListToggle));
        AppSettings.Save(SelectedFolder, EnableStartup, SelectedVersion, SelectedBeatmapSource, value);
        QueueSelectionRefresh();
    }

    partial void OnBeatmapSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredBeatmapEntries));
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

        var beatmapFolders = !HideBeatmapList && SelectedBeatmaps.Count > 0
            ? SelectedBeatmaps
            : BeatmapEntries;

        long totalBytes = 0;
        foreach (var beatmap in beatmapFolders)
        {
            var folderPath = Path.Combine(customLevelsPath, beatmap.Name);
            if (!Directory.Exists(folderPath))
            {
                continue;
            }

            if (_beatmapSizeCache.TryGetValue(folderPath, out var cachedSize))
            {
                totalBytes += cachedSize;
                continue;
            }

            var folderSize = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                .Sum(filePath => new FileInfo(filePath).Length);

            _beatmapSizeCache[folderPath] = folderSize;
            totalBytes += folderSize;
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

    public sealed class BeatmapEntryViewModel
    {
        public string Name { get; set; } = string.Empty;
        public string? ImagePath { get; set; }
        public Bitmap? Image { get; set; }
        public bool HasImage { get; set; }
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
        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (runKey is null)
            {
                return;
            }

            if (enableStartup)
            {
                runKey.SetValue("BeatSync", $"\"{executablePath}\"");
                return;
            }

            runKey.DeleteValue("BeatSync", false);
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var autostartDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
        Directory.CreateDirectory(autostartDir);

        var desktopFilePath = Path.Combine(autostartDir, "beatsyncapplication.desktop");

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
