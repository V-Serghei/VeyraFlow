using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class OperationMonitorWindow : Window
{
    public OperationMonitorWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is OperationMonitorWindowViewModel vm)
            {
                vm.RequestClose += Close;
                _ = vm.StartAsync();
            }
        };

        Closed += (_, _) =>
        {
            if (DataContext is OperationMonitorWindowViewModel vm)
            {
                vm.RequestClose -= Close;
                _ = vm.StopAsync();
            }
        };
    }
}
