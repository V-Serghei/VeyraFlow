using Avalonia;
using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class OperationJournalWindow : Window
{
    private const double CompactWidth = 1320;
    private const double NarrowWidth = 1060;

    public OperationJournalWindow()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;

        Opened += (_, _) =>
        {
            ApplyResponsiveLayout(Bounds.Width);

            if (DataContext is OperationJournalWindowViewModel vm)
                vm.RequestClose += Close;
        };

        Closed += (_, _) =>
        {
            if (DataContext is OperationJournalWindowViewModel vm)
                vm.RequestClose -= Close;
        };
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        ToggleRootClass("compact-layout", width < CompactWidth);
        ToggleRootClass("narrow-layout", width < NarrowWidth);

        if (width < NarrowWidth)
        {
            OperationJournalContentGrid.ColumnDefinitions = new ColumnDefinitions("*");
            OperationJournalContentGrid.RowDefinitions = new RowDefinitions("1.05*,12,0.95*");

            Grid.SetColumn(OperationJournalEntriesPanel, 0);
            Grid.SetRow(OperationJournalEntriesPanel, 0);

            Grid.SetColumn(OperationJournalDetailsPanel, 0);
            Grid.SetRow(OperationJournalDetailsPanel, 2);
            return;
        }

        OperationJournalContentGrid.RowDefinitions = new RowDefinitions("*");
        OperationJournalContentGrid.ColumnDefinitions = width < CompactWidth
            ? new ColumnDefinitions("1.05*,0.95*")
            : new ColumnDefinitions("1.15*,0.95*");

        Grid.SetColumn(OperationJournalEntriesPanel, 0);
        Grid.SetRow(OperationJournalEntriesPanel, 0);

        Grid.SetColumn(OperationJournalDetailsPanel, 1);
        Grid.SetRow(OperationJournalDetailsPanel, 0);
    }

    private void ToggleRootClass(string className, bool enabled)
    {
        if (enabled)
        {
            if (!Classes.Contains(className))
                Classes.Add(className);

            return;
        }

        if (Classes.Contains(className))
            Classes.Remove(className);
    }
}
