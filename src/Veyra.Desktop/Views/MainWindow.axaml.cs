using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            WindowLayoutHelper.FitToWorkingArea(
                this,
                maximizeToWorkingArea: true,
                frameMarginDip: 0d,
                minWidthDip: 720d,
                minHeightDip: 560d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    maximizeToWorkingArea: true,
                    frameMarginDip: 0d,
                    minWidthDip: 720d,
                    minHeightDip: 560d),
                DispatcherPriority.Background);

            if (DataContext is MainWindowViewModel vm)
                vm.OnLoaded();
        };
    }
}
