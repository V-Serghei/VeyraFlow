using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class CloudInformationWindow : Window
{
    public CloudInformationWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is CloudInformationWindowViewModel vm)
            {
                vm.RequestClose -= Close;
                vm.RequestClose += Close;
                _ = vm.RefreshAsync();
            }

            WindowLayoutHelper.FitToWorkingArea(this, Owner as Window, false, 12d, 760d, 560d);
            Dispatcher.UIThread.Post(
                () => WindowLayoutHelper.FitToWorkingArea(this, Owner as Window, false, 12d, 760d, 560d),
                DispatcherPriority.Background);
        };

        Closed += (_, _) =>
        {
            if (DataContext is CloudInformationWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
