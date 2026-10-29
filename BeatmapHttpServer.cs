using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using BeatSync.Models;
using BeatSync.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace BeatSync;

public static class BeatmapHttpServer
{
    private static WebApplication? _app;
    public static event Action<bool>? ServerStatusChanged;
    public static bool IsRunning => _app is not null;

    public static void Start()
    {
        if (_app is not null)
        {
            return;
        }

        var hostAddress = GetLocalAddress();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{hostAddress}:5050");

        var app = builder.Build();

        app.MapGet("/api/beatmaps", () => Results.Content(BuildBeatmapHtml(), "text/html; charset=utf-8"));
        app.MapGet("/api/beatmaps/json", () => Results.Json(GetBeatmaps()));
        app.MapGet("/api/beatmaps/download/{customLevel}", (string customLevel) =>
        {
            var folderPath = ResolveCustomLevelFolder(customLevel);
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return Results.NotFound();
            }

            var source = GetSelectedBeatmapSource();
            var archiveBytes = CreateZipForFolder(folderPath, filePath => ShouldIncludeFileInDownload(filePath, folderPath, source));
            return Results.File(new MemoryStream(archiveBytes), "application/zip", $"{customLevel}.zip");
        });
        app.MapGet("/api/beatmaps/download/{customLevel}/{*relativePath}", (string customLevel, string? relativePath) =>
        {
            var folderPath = ResolveCustomLevelFolder(customLevel);
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return Results.NotFound();
            }

            var source = GetSelectedBeatmapSource();

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                var archiveBytes = CreateZipForFolder(folderPath, filePath => ShouldIncludeFileInDownload(filePath, folderPath, source));
                return Results.File(new MemoryStream(archiveBytes), "application/zip", $"{customLevel}.zip");
            }

            var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(folderPath, normalizedPath));

            if (!targetPath.StartsWith(Path.GetFullPath(folderPath), StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest();
            }

            if (!ShouldIncludePathInDownload(targetPath, folderPath, source))
            {
                return Results.NotFound();
            }

            if (File.Exists(targetPath))
            {
                var provider = new FileExtensionContentTypeProvider();
                provider.TryGetContentType(targetPath, out var contentType);
                return Results.File(targetPath, contentType ?? "application/octet-stream", Path.GetFileName(targetPath));
            }

            if (Directory.Exists(targetPath))
            {
                var archiveBytes = CreateZipForFolder(targetPath, filePath => ShouldIncludeFileInDownload(filePath, targetPath, source));
                return Results.File(new MemoryStream(archiveBytes), "application/zip", $"{Path.GetFileName(targetPath)}.zip");
            }

            return Results.NotFound();
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        _app = app;
        ServerStatusChanged?.Invoke(true);

        _ = Task.Run(async () =>
        {
            try
            {
                await app.RunAsync();
            }
            catch (Exception)
            {
                // The app is closing; this is expected in hosted desktop scenarios.
            }
        });
    }

    public static async void Stop()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        ServerStatusChanged?.Invoke(false);
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

    private static object GetBeatmaps()
    {
        var selectedVersion = GetSelectedBeatmapVersion();
        return new BeatmapResponse
        {
            Version = selectedVersion,
        };
    }

    private static BeatmapVersion? GetSelectedBeatmapVersion()
    {
        var settings = AppSettings.Load();
        var selectedFolder = !string.IsNullOrWhiteSpace(settings.SelectedFolder)
            ? settings.SelectedFolder
            : MainViewModel.GetDefaultSelectedFolder();

        if (string.IsNullOrWhiteSpace(selectedFolder) || !Directory.Exists(selectedFolder))
        {
            return null;
        }

        var selectedVersion = settings.SelectedVersion;
        var versionFolders = Directory.EnumerateDirectories(selectedFolder)
            .Where(versionFolder => File.Exists(Path.Combine(versionFolder, OperatingSystem.IsWindows() ? "Beat Saber.exe" : "Beat Saber")))
            .OrderBy(versionFolder => Path.GetFileName(versionFolder), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(selectedVersion))
        {
            versionFolders = versionFolders
                .Where(versionFolder => string.Equals(Path.GetFileName(versionFolder), selectedVersion, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var versionFolderPath = versionFolders.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(versionFolderPath))
        {
            return null;
        }

        var source = MainViewModel.GetBeatmapSourceName(settings.SelectedBeatmapSource);
        var selectedBeatmapNames = GetSelectedBeatmapNames(settings);
        var includeOnlySelected = !settings.HideBeatmapList && selectedBeatmapNames.Count > 0;
        var version = new BeatmapVersion
        {
            Name = Path.GetFileName(versionFolderPath) ?? "Unknown",
            Source = source,
            CustomLevels = GetCustomLevels(MainViewModel.GetBeatmapRootPath(versionFolderPath, settings.SelectedBeatmapSource), source, includeOnlySelected, selectedBeatmapNames),
        };

        return version.CustomLevels.Count > 0 ? version : null;
    }

    private static List<CustomLevelFolder> GetCustomLevels(string customLevelsFolder, string source, bool includeOnlySelected = false, IReadOnlyCollection<string>? selectedBeatmapNames = null)
    {
        if (!Directory.Exists(customLevelsFolder))
        {
            return new List<CustomLevelFolder>();
        }

        return Directory.EnumerateDirectories(customLevelsFolder)
            .Where(folder => !includeOnlySelected || selectedBeatmapNames is null || selectedBeatmapNames.Count == 0 || selectedBeatmapNames.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
            .OrderBy(folder => Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase)
            .Select(folder => new CustomLevelFolder
            {
                Name = Path.GetFileName(folder) ?? "Unknown",
                Contents = Directory.EnumerateFileSystemEntries(folder)
                    .Where(entry => ShouldIncludeEntryInApi(entry, source, folder))
                    .Select(entry => Path.GetFileName(entry) ?? "")
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            })
            .ToList();
    }

    private static bool ShouldIncludeEntryInApi(string path, string source, string parentFolderPath)
    {
        if (string.Equals(source, "CustomWIPLevels", StringComparison.OrdinalIgnoreCase))
        {
            var entryName = Path.GetFileName(path);
            var parentFolderName = Path.GetFileName(parentFolderPath);

            if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

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

                if (ShouldHideEntryFromApi(path))
                {
                    return false;
                }
            }

            return true;
        }

        return !ShouldHideEntryFromApi(path);
    }

    private static bool ShouldHideEntryFromApi(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        return Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
            .Any(directory => string.Equals(Path.GetFileName(directory), "Assets", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildBeatmapHtml()
    {
        var version = GetSelectedBeatmapVersion();
        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\" />");
        builder.AppendLine("<title>BeatSync</title>");
        builder.AppendLine("<style>");
        builder.AppendLine("body { font-family: Arial, sans-serif; background: #2b2d31; color: #f2f3f5; margin: 24px; }");
        builder.AppendLine("h1 { margin-bottom: 20px; }");
        builder.AppendLine("h2 { margin-top: 24px; color: #b9bbbe; }");
        builder.AppendLine(".folder { margin-left: 18px; margin-bottom: 16px; padding: 8px 12px; background: #1e1f22; border-radius: 8px; }");
        builder.AppendLine("a { color: #8ab4f8; text-decoration: none; }");
        builder.AppendLine("a:hover { text-decoration: underline; }");
        builder.AppendLine("ul { margin: 8px 0 0 18px; }");
        builder.AppendLine("li { margin: 4px 0; }");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<h1>BeatSync</h1>");

        if (version is null)
        {
            builder.AppendLine("<p>No custom levels found for the selected Beat Saber folder.</p>");
            builder.AppendLine("</body>");
            builder.AppendLine("</html>");
            return builder.ToString();
        }

        builder.AppendLine($"<h2>{WebUtility.HtmlEncode(version.Name)}</h2>");

        foreach (var customLevel in version.CustomLevels)
        {
            var folderDownload = $"/api/beatmaps/download/{Uri.EscapeDataString(customLevel.Name)}";
            builder.AppendLine("<div class=\"folder\">");
            builder.AppendLine($"<strong><a href=\"{folderDownload}\">{WebUtility.HtmlEncode(customLevel.Name)}</a></strong>");
            builder.AppendLine("<ul>");

            foreach (var content in customLevel.Contents)
            {
                var contentUrl = $"/api/beatmaps/download/{Uri.EscapeDataString(customLevel.Name)}/{Uri.EscapeDataString(content)}";
                builder.AppendLine($"<li><a href=\"{contentUrl}\">{WebUtility.HtmlEncode(content)}</a></li>");
            }

            builder.AppendLine("</ul>");
            builder.AppendLine("</div>");
        }

        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private static string? ResolveCustomLevelFolder(string customLevel)
    {
        var settings = AppSettings.Load();
        var selectedFolder = settings.SelectedFolder;
        if (string.IsNullOrWhiteSpace(selectedFolder) || !Directory.Exists(selectedFolder))
        {
            selectedFolder = MainViewModel.GetDefaultSelectedFolder();
        }

        if (string.IsNullOrWhiteSpace(selectedFolder) || !Directory.Exists(selectedFolder))
        {
            return null;
        }

        var selectedVersion = settings.SelectedVersion;
        var selectedBeatmapSource = settings.SelectedBeatmapSource;
        var selectedBeatmapNames = GetSelectedBeatmapNames(settings);
        var includeOnlySelected = !settings.HideBeatmapList && selectedBeatmapNames.Count > 0;

        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            var versionFolders = Directory.EnumerateDirectories(selectedFolder)
                .Where(versionFolder => File.Exists(Path.Combine(versionFolder, OperatingSystem.IsWindows() ? "Beat Saber.exe" : "Beat Saber")))
                .OrderBy(versionFolder => Path.GetFileName(versionFolder), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var versionFolder in versionFolders)
            {
                var customLevelsRoot = MainViewModel.GetBeatmapRootPath(versionFolder, selectedBeatmapSource);
                var match = Directory.EnumerateDirectories(customLevelsRoot)
                    .Where(folder => !includeOnlySelected || selectedBeatmapNames.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
                    .FirstOrDefault(folder => string.Equals(Path.GetFileName(folder), customLevel, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }

            return null;
        }

        var customLevelsDirectory = MainViewModel.GetBeatmapRootPath(Path.Combine(selectedFolder, selectedVersion), selectedBeatmapSource);
        if (!Directory.Exists(customLevelsDirectory))
        {
            return null;
        }

        return Directory.EnumerateDirectories(customLevelsDirectory)
            .Where(folder => !includeOnlySelected || selectedBeatmapNames.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(folder => string.Equals(Path.GetFileName(folder), customLevel, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetSelectedBeatmapNames(AppSettings settings)
    {
        return settings.SelectedBeatmapNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetSelectedBeatmapSource()
    {
        var settings = AppSettings.Load();
        return MainViewModel.GetBeatmapSourceName(settings.SelectedBeatmapSource);
    }

    private static bool ShouldIncludePathInDownload(string path, string rootFolderPath, string source)
    {
        if (!string.Equals(source, "CustomWIPLevels", StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path))
            {
                return !ShouldHideEntryFromApi(path);
            }

            if (File.Exists(path))
            {
                var rootFullPath = Path.GetFullPath(rootFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var currentDirectory = Path.GetDirectoryName(path);

                while (!string.IsNullOrWhiteSpace(currentDirectory))
                {
                    if (ShouldHideEntryFromApi(currentDirectory))
                    {
                        return false;
                    }

                    var currentFullPath = Path.GetFullPath(currentDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (string.Equals(currentFullPath, rootFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    currentDirectory = Path.GetDirectoryName(currentDirectory);
                }
            }

            return true;
        }

        if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(rootFolderPath, path).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relativePath) || string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return true;
        }

        var firstSegment = relativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstSegment))
        {
            return true;
        }

        if (string.Equals(firstSegment, "autosaves", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rootFolderName = Path.GetFileName(rootFolderPath);
        if (string.Equals(firstSegment, rootFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var firstSegmentPath = Path.Combine(rootFolderPath, firstSegment);
        if (Directory.Exists(firstSegmentPath) && ShouldHideEntryFromApi(firstSegmentPath))
        {
            return false;
        }

        return true;
    }

    private static bool ShouldIncludeFileInDownload(string filePath, string rootFolderPath, string source)
    {
        return File.Exists(filePath) && ShouldIncludePathInDownload(filePath, rootFolderPath, source);
    }

    private static byte[] CreateZipForFolder(string folderPath, Func<string, bool>? includeFile = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                if (includeFile is not null && !includeFile(file))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(folderPath, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);
            }
        }

        return stream.ToArray();
    }

    private sealed class BeatmapResponse
    {
        public BeatmapVersion? Version { get; set; }
    }

    private sealed class BeatmapVersion
    {
        public string Name { get; set; } = string.Empty;
        public string Source { get; set; } = "CustomLevels";
        public List<CustomLevelFolder> CustomLevels { get; set; } = new();
    }

    private sealed class CustomLevelFolder
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Contents { get; set; } = new();
    }
}
