using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class CloudSyncHealthWindow : Window
{
    public CloudSyncHealthWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is CloudSyncHealthWindowViewModel openedVm)
            {
                openedVm.RequestClose -= Close;
                openedVm.RequestClose += Close;
            }

            WindowLayoutHelper.FitToWorkingArea(
                this,
                Owner as Window,
                maximizeToWorkingArea: false,
                frameMarginDip: 12d,
                minWidthDip: 980d,
                minHeightDip: 680d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    Owner as Window,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 12d,
                    minWidthDip: 980d,
                    minHeightDip: 680d),
                DispatcherPriority.Background);
        };

        Closed += (_, _) =>
        {
            if (DataContext is CloudSyncHealthWindowViewModel vm)
            {
                vm.RequestClose -= Close;
                vm.Detach();
            }
        };
    }
}
