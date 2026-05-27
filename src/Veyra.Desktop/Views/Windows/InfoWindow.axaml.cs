using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class InfoWindow : Window
{
    public InfoWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            WindowLayoutHelper.FitToWorkingArea(
                this,
                Owner as Window,
                maximizeToWorkingArea: false,
                frameMarginDip: 12d,
                minWidthDip: 560d,
                minHeightDip: 420d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    Owner as Window,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 12d,
                    minWidthDip: 560d,
                    minHeightDip: 420d),
                DispatcherPriority.Background);
        };
    }
}
