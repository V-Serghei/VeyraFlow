using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class RepositoryBundleImportWizardWindow : Window
{
    public bool IsCompleted => (DataContext as RepositoryBundleImportWizardWindowViewModel)?.IsCompleted == true;
    public string ResultSummary => (DataContext as RepositoryBundleImportWizardWindowViewModel)?.ResultSummary ?? string.Empty;
    public int ImportedRepositoryId => (DataContext as RepositoryBundleImportWizardWindowViewModel)?.ImportedRepositoryId ?? 0;

    public RepositoryBundleImportWizardWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is RepositoryBundleImportWizardWindowViewModel vm)
                vm.RequestClose += Close;
        };

        Closed += (_, _) =>
        {
            if (DataContext is RepositoryBundleImportWizardWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }
}
