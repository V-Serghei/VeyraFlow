using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class ConfirmActionWindow : Window
{
    public ConfirmActionWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is ConfirmActionWindowViewModel vm)
                vm.RequestClose += Close;
        };

        Closed += (_, _) =>
        {
            if (DataContext is ConfirmActionWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
