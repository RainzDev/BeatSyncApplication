using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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

    private async void EditPort_Click(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainViewModel;
        if (viewModel is null)
        {
            return;
        }

        var portTextBox = new TextBox
        {
            Text = viewModel.ServerPort.ToString(),
            Width = 160,
            Height = 32,
            PlaceholderText = "Port",
            Margin = new Thickness(0, 0, 0, 8),
        };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Spacing = 12,
        };

        var saveButton = new Button
        {
            Content = "Save",
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Width = 70,
            IsDefault = true,
            Background = new SolidColorBrush(Color.Parse("#7dd3fc")),
            Foreground = new SolidColorBrush(Color.Parse("#081621")),
            BorderBrush = new SolidColorBrush(Color.Parse("#7dd3fc")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Width = 70,
            IsCancel = true,
            Background = new SolidColorBrush(Color.Parse("#0c1d2b")),
            Foreground = new SolidColorBrush(Color.Parse("#eafcff")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2f5f94")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };

        var dialog = new Window
        {
            Title = "Edit server port",
            Width = 240,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse("#0a0d14")),
            Padding = new Thickness(0),
            Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0d1726")),
                BorderBrush = new SolidColorBrush(Color.Parse("#1f3a5b")),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Server port",
                            Foreground = new SolidColorBrush(Color.Parse("#edf6ff")),
                            FontWeight = Avalonia.Media.FontWeight.SemiBold,
                            Margin = new Thickness(0, 0, 0, 4),
                        },
                        new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("*"),
                            Children =
                            {
                                portTextBox,
                            },
                        },
                        buttons,
                    },
                },
            },
        };

        saveButton.Click += (_, _) =>
        {
            if (int.TryParse(portTextBox.Text, out var port) && port is > 0 and < 65536)
            {
                dialog.Close(port);
            }
        };
        cancelButton.Click += (_, _) => dialog.Close(null);
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);

        var result = await dialog.ShowDialog<int?>(this);
        if (result is null)
        {
            return;
        }

        var selectedPort = result.Value;
        if (selectedPort != viewModel.ServerPort)
        {
            viewModel.UpdateServerPort(selectedPort);
        }
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