using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BeatSync.Models;

public sealed class AppSettings
{
    public string? SelectedFolder { get; set; }
    public string? SelectedVersion { get; set; }
    public string SelectedBeatmapSource { get; set; } = "CustomLevels";
    public bool EnableStartup { get; set; }
    public bool HideBeatmapList { get; set; } = true;
    public int ServerPort { get; set; } = 5050;
    public List<string> SelectedBeatmapNames { get; set; } = new();

    public static string ConfigPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, "BeatSync");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "appsettings.json");
        }
    }

    public static AppSettings Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new AppSettings { HideBeatmapList = true, ServerPort = 5050 };
        }

        var json = File.ReadAllText(ConfigPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

        settings.HideBeatmapList = settings.HideBeatmapList;
        settings.ServerPort = settings.ServerPort is > 0 and < 65536 ? settings.ServerPort : 5050;
        return settings;
    }

    public static void Save(string? selectedFolder, bool enableStartup, string? selectedVersion = null, string? selectedBeatmapSource = null, bool? hideBeatmapList = null, IEnumerable<string>? selectedBeatmapNames = null, int? serverPort = null)
    {
        var settings = Load();
        settings.SelectedFolder = selectedFolder;
        settings.EnableStartup = enableStartup;
        settings.SelectedVersion = selectedVersion;
        settings.SelectedBeatmapSource = string.IsNullOrWhiteSpace(selectedBeatmapSource)
            ? "CustomLevels"
            : selectedBeatmapSource;
        settings.HideBeatmapList = hideBeatmapList ?? settings.HideBeatmapList;
        settings.ServerPort = serverPort is > 0 and < 65536 ? serverPort.Value : settings.ServerPort;
        settings.SelectedBeatmapNames = selectedBeatmapNames is null
            ? settings.SelectedBeatmapNames
            : selectedBeatmapNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        File.WriteAllText(ConfigPath, json);
    }
}
