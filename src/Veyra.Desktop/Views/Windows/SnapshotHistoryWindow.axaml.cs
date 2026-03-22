using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class SnapshotHistoryWindow : Window
{
    public SnapshotHistoryWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            WindowLayoutHelper.FitToWorkingArea(
                this,
                Owner as Window,
                maximizeToWorkingArea: false,
                frameMarginDip: 8d,
                minWidthDip: 1080d,
                minHeightDip: 720d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    Owner as Window,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 8d,
                    minWidthDip: 1080d,
                    minHeightDip: 720d),
                DispatcherPriority.Background);
        };
    }
}
