using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class ArtifactKeyManagementWindow : Window
{
    public ArtifactKeyManagementWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is ArtifactKeyManagementWindowViewModel vm)
            {
                vm.RequestClose -= Close;
                vm.RequestClose += Close;
            }

            WindowLayoutHelper.FitToWorkingArea(
                this,
                Owner as Window,
                maximizeToWorkingArea: false,
                frameMarginDip: 12d,
                minWidthDip: 960d,
                minHeightDip: 680d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    Owner as Window,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 12d,
                    minWidthDip: 960d,
                    minHeightDip: 680d),
                DispatcherPriority.Background);
        };

        Closed += (_, _) =>
        {
            if (DataContext is ArtifactKeyManagementWindowViewModel vm)
            {
                vm.RequestClose -= Close;
                vm.Detach();
            }
        };
    }
}
