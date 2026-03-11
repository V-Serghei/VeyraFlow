using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class CreateRepositoryWindow : Window
{
    public CreateRepositoryWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            WindowLayoutHelper.FitToWorkingArea(
                this,
                Owner as Window,
                maximizeToWorkingArea: false,
                frameMarginDip: 8d,
                minWidthDip: 740d,
                minHeightDip: 540d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    Owner as Window,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 8d,
                    minWidthDip: 740d,
                    minHeightDip: 540d),
                DispatcherPriority.Background);

            if (DataContext is CreateRepositoryWindowViewModel vm)
                vm.RequestClose += Close;
        };

        Closed += (_, _) =>
        {
            if (DataContext is CreateRepositoryWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
