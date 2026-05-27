using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class PasswordVerificationWindow : Window
{
    public PasswordVerificationWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is PasswordVerificationWindowViewModel vm)
                vm.RequestClose += Close;
        };

        Closed += (_, _) =>
        {
            if (DataContext is PasswordVerificationWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
