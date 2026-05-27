using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class AuthDialogWindow : Window
{
    public AuthDialogWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is AuthDialogWindowViewModel vm)
                vm.RequestClose += Close;
        };

        Closed += (_, _) =>
        {
            if (DataContext is AuthDialogWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
