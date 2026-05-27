using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class CloudRestoreWizardWindow : Window
{
    public CloudRestoreWizardWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is CloudRestoreWizardWindowViewModel vm)
            {
                vm.RequestClose -= Close;
                vm.RequestClose += Close;
            }

            WindowLayoutHelper.FitToWorkingArea(this, Owner as Window, false, 12d, 720d, 560d);
            Dispatcher.UIThread.Post(
                () => WindowLayoutHelper.FitToWorkingArea(this, Owner as Window, false, 12d, 720d, 560d),
                DispatcherPriority.Background);
        };

        Closed += (_, _) =>
        {
            if (DataContext is CloudRestoreWizardWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
