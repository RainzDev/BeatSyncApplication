using Avalonia.Controls;
using Avalonia.Threading;

namespace BeatSync.Views;

public partial class LoadingWindow : Window
{
    public LoadingWindow()
    {
        InitializeComponent();
        DataContext = this;
        SetStatus("Loading BeatSync...");
    }

    public void SetStatus(string status)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (StatusTextBlock is not null)
            {
                StatusTextBlock.Text = status;
            }

            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (StatusTextBlock is not null)
            {
                StatusTextBlock.Text = status;
            }
        });
    }
}
