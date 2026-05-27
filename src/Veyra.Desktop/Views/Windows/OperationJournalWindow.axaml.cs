using System;
using Avalonia;
using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class OperationJournalWindow : Window
{
    private const double CompactWidth = 1320;
    private const double NarrowWidth = 1060;
    private readonly Grid _contentGrid;
    private readonly Border _entriesPanel;
    private readonly Border _detailsPanel;

    public OperationJournalWindow()
    {
        InitializeComponent();
        _contentGrid = this.FindControl<Grid>("OperationJournalContentGrid")
            ?? throw new InvalidOperationException("OperationJournalContentGrid was not found.");
        _entriesPanel = this.FindControl<Border>("OperationJournalEntriesPanel")
            ?? throw new InvalidOperationException("OperationJournalEntriesPanel was not found.");
        _detailsPanel = this.FindControl<Border>("OperationJournalDetailsPanel")
            ?? throw new InvalidOperationException("OperationJournalDetailsPanel was not found.");
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
            _contentGrid.ColumnDefinitions = new ColumnDefinitions("*");
            _contentGrid.RowDefinitions = new RowDefinitions("1.05*,12,0.95*");

            Grid.SetColumn(_entriesPanel, 0);
            Grid.SetRow(_entriesPanel, 0);

            Grid.SetColumn(_detailsPanel, 0);
            Grid.SetRow(_detailsPanel, 2);
            return;
        }

        _contentGrid.RowDefinitions = new RowDefinitions("*");
        _contentGrid.ColumnDefinitions = width < CompactWidth
            ? new ColumnDefinitions("1.05*,0.95*")
            : new ColumnDefinitions("1.15*,0.95*");

        Grid.SetColumn(_entriesPanel, 0);
        Grid.SetRow(_entriesPanel, 0);

        Grid.SetColumn(_detailsPanel, 1);
        Grid.SetRow(_detailsPanel, 0);
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
