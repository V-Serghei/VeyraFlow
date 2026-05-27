using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class RepositoryFolderPickerWindow : Window
{
    public RepositoryFolderPickerWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is RepositoryFolderPickerWindowViewModel vm)
                vm.RequestClose += Close;

            WindowLayoutHelper.FitToWorkingArea(
                this,
                Owner as Window,
                maximizeToWorkingArea: false,
                frameMarginDip: 12d,
                minWidthDip: 620d,
                minHeightDip: 460d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    Owner as Window,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 12d,
                    minWidthDip: 620d,
                    minHeightDip: 460d),
                DispatcherPriority.Background);
        };

        Closed += (_, _) =>
        {
            if (DataContext is RepositoryFolderPickerWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
