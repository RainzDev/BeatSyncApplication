using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BeatmapPorter.ViewModels;
using BeatmapPorter.Views;

namespace BeatmapPorter;

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
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            BeatmapHttpServer.Start();
            desktop.MainWindow.Closed += (_, _) => BeatmapHttpServer.Stop();
        }

        base.OnFrameworkInitializationCompleted();
    }
}