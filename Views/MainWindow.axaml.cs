using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BeatSync.ViewModels;

namespace BeatSync.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainViewModel;
        if (viewModel is null)
        {
            return;
        }

        var startingDirectory = GetDefaultSelectedFolder();
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a folder",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startingDirectory),
        });

        var selectedFolder = folders.FirstOrDefault();
        if (selectedFolder is null)
        {
            return;
        }

        var localPath = selectedFolder.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            viewModel.SaveSelectedFolder(localPath);
        }
    }

    private void BeatmapList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        var viewModel = DataContext as MainViewModel;
        if (viewModel is null)
        {
            return;
        }

        var selected = listBox.SelectedItems?
            .OfType<MainViewModel.BeatmapEntryViewModel>()
            .ToList() ?? new List<MainViewModel.BeatmapEntryViewModel>();

        viewModel.UpdateSelectedBeatmaps(selected);
    }

    private static string GetDefaultSelectedFolder()
    {
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
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
        else if (OperatingSystem.IsLinux())
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
}