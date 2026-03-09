using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class SnapshotNameDialogWindow : Window
{
    public bool IsConfirmed { get; private set; }
    public string? SnapshotTitle { get; private set; }

    public SnapshotNameDialogWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is SnapshotNameDialogWindowViewModel vm)
                vm.RequestClose += OnRequestClose;
        };

        Closed += (_, _) =>
        {
            if (DataContext is SnapshotNameDialogWindowViewModel vm)
            {
                vm.RequestClose -= OnRequestClose;
                vm.CleanupPreviewResources();
            }
        };
    }

    private void OnRequestClose(bool confirmed)
    {
        IsConfirmed = confirmed;

        if (confirmed && DataContext is SnapshotNameDialogWindowViewModel vm)
            SnapshotTitle = vm.SnapshotName;
        else
            SnapshotTitle = null;

        Close();
    }
}

