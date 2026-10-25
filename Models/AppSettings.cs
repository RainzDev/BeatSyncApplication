using System;
using System.IO;
using System.Text.Json;

namespace BeatSync.Models;

public sealed class AppSettings
{
    public string? SelectedFolder { get; set; }
    public string? SelectedVersion { get; set; }
    public string SelectedBeatmapSource { get; set; } = "CustomLevels";
    public bool EnableStartup { get; set; }

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
            return new AppSettings();
        }

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public static void Save(string? selectedFolder, bool enableStartup, string? selectedVersion = null, string? selectedBeatmapSource = null)
    {
        var settings = Load();
        settings.SelectedFolder = selectedFolder;
        settings.EnableStartup = enableStartup;
        settings.SelectedVersion = selectedVersion;
        settings.SelectedBeatmapSource = string.IsNullOrWhiteSpace(selectedBeatmapSource)
            ? "CustomLevels"
            : selectedBeatmapSource;

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        File.WriteAllText(ConfigPath, json);
    }
}
