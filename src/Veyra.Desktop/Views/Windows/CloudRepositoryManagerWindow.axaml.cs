using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class CloudRepositoryManagerWindow : Window
{
    public CloudRepositoryManagerWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is CloudRepositoryManagerWindowViewModel vm)
            {
                vm.RequestClose -= Close;
                vm.RequestClose += Close;
                vm.StartLiveRefresh();
                _ = vm.RefreshAsync();
            }

            WindowLayoutHelper.FitToWorkingArea(this, Owner as Window, false, 12d, 980d, 640d);
            Dispatcher.UIThread.Post(
                () => WindowLayoutHelper.FitToWorkingArea(this, Owner as Window, false, 12d, 980d, 640d),
                DispatcherPriority.Background);
        };

        Closed += (_, _) =>
        {
            if (DataContext is CloudRepositoryManagerWindowViewModel vm)
            {
                vm.StopLiveRefresh();
                vm.RequestClose -= Close;
            }
        };
    }
}
