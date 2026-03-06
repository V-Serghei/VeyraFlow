using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class CreateRepositoryWindow : Window
{
    public CreateRepositoryWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
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
