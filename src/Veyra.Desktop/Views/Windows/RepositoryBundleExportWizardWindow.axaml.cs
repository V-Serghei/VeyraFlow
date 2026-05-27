using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class RepositoryBundleExportWizardWindow : Window
{
    public bool IsCompleted => (DataContext as RepositoryBundleExportWizardWindowViewModel)?.IsCompleted == true;
    public string ResultSummary => (DataContext as RepositoryBundleExportWizardWindowViewModel)?.ResultSummary ?? string.Empty;

    public RepositoryBundleExportWizardWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is RepositoryBundleExportWizardWindowViewModel vm)
                vm.RequestClose += Close;
        };

        Closed += (_, _) =>
        {
            if (DataContext is RepositoryBundleExportWizardWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
