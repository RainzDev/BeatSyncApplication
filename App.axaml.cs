using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BeatSync.ViewModels;
using BeatSync.Views;

namespace BeatSync;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var loadingWindow = new LoadingWindow();
            loadingWindow.Show();

            _ = Task.Run(() =>
            {
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
}