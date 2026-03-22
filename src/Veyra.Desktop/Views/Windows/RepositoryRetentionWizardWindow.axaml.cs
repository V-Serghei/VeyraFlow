using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class RepositoryRetentionWizardWindow : Window
{
    public RepositoryRetentionWizardWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is RepositoryRetentionWizardWindowViewModel vm)
                vm.RequestClose += Close;
        };

        Closed += (_, _) =>
        {
            if (DataContext is RepositoryRetentionWizardWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
