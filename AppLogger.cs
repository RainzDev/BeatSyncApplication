using System;
using System.IO;

namespace BeatSync;

public static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeatSync");
    private static readonly string LogPath = Path.Combine(LogDirectory, "BeatSync.log");

    public static void Clear()
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                File.WriteAllText(LogPath, string.Empty);
            }
        }
        catch
        {
            // Logging is best effort only.
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            Write("ERROR", message);
            return;
        }

        Write("ERROR", $"{message} Exception={exception}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging is best effort only.
        }
    }
}