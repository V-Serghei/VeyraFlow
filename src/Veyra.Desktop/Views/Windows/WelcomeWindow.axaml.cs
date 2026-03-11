using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            WindowLayoutHelper.FitToWorkingArea(
                this,
                maximizeToWorkingArea: false,
                frameMarginDip: 8d,
                minWidthDip: 820d,
                minHeightDip: 560d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 8d,
                    minWidthDip: 820d,
                    minHeightDip: 560d),
                DispatcherPriority.Background);
        };
    }
}
