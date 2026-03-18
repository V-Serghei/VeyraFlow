using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Veyra.Desktop.ViewModels.Pages.Explorer;

namespace Veyra.Desktop.Views.Pages.Explorer;

public partial class RepositoryExplorerView : UserControl
{
    private const double CompactWidth = 1240;
    private const double NarrowWidth = 980;
    private const double SnapshotCompactWidth = 1440;
    private const double SnapshotNarrowWidth = 1120;
    private readonly HashSet<TreeViewItem> _observedTreeItems = [];
    private INotifyPropertyChanged? _observedViewModel;

    public RepositoryExplorerView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += (_, _) => ApplyResponsiveLayout(Bounds.Width);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnExplorerItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ExplorerItemViewModel item } ||
            DataContext is not RepositoryExplorerViewModel vm ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            if (vm.ActivateItemCommand.CanExecute(item))
                vm.ActivateItemCommand.Execute(item);

            if (item.IsDirectory)
                Dispatcher.UIThread.Post(RefreshTreeState, DispatcherPriority.Background);

            e.Handled = true;
            return;
        }

        if (vm.SelectItemCommand.CanExecute(item))
            vm.SelectItemCommand.Execute(item);
    }

    private void OnTreeNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ExplorerTreeNodeViewModel node }
            || DataContext is not RepositoryExplorerViewModel vm
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.ClickCount < 2)
        {
            return;
        }

        if (node.Children.Count > 0)
            node.IsExpanded = !node.IsExpanded;

        vm.SelectedTreeNode = node;
        Dispatcher.UIThread.Post(RefreshTreeState, DispatcherPriority.Background);
        e.Handled = true;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        ToggleRootClass("compact-layout", width < CompactWidth);
        ToggleRootClass("narrow-layout", width < NarrowWidth);

        ApplyHeroLayout(width);
        ApplyExplorerPanelsLayout(width);
        ApplySnapshotHistoryLayout(width);
        ApplyExplorerItemGridLayout(width);
    }

    private void ApplyHeroLayout(double width)
    {
        if (width < NarrowWidth)
        {
            ExplorerHeroGrid.ColumnDefinitions = new ColumnDefinitions("Auto,*");
            ExplorerHeroGrid.RowDefinitions = new RowDefinitions("Auto,Auto");

            Grid.SetColumn(ExplorerBackButton, 0);
            Grid.SetRow(ExplorerBackButton, 0);

            Grid.SetColumn(ExplorerHeroContent, 1);
            Grid.SetRow(ExplorerHeroContent, 0);

            Grid.SetColumn(ExplorerHeroActionsPanel, 0);
            Grid.SetRow(ExplorerHeroActionsPanel, 1);
            Grid.SetColumnSpan(ExplorerHeroActionsPanel, 2);
            ExplorerHeroActionsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            ExplorerHeroTitle.FontSize = 18;
            return;
        }

        ExplorerHeroGrid.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        ExplorerHeroGrid.RowDefinitions = new RowDefinitions("Auto");

        Grid.SetColumn(ExplorerBackButton, 0);
        Grid.SetRow(ExplorerBackButton, 0);

        Grid.SetColumn(ExplorerHeroContent, 1);
        Grid.SetRow(ExplorerHeroContent, 0);

        Grid.SetColumn(ExplorerHeroActionsPanel, 2);
        Grid.SetRow(ExplorerHeroActionsPanel, 0);
        Grid.SetColumnSpan(ExplorerHeroActionsPanel, 1);
        ExplorerHeroActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
        ExplorerHeroTitle.FontSize = width < CompactWidth ? 20 : 22;
    }

    private void ApplyExplorerPanelsLayout(double width)
    {
        if (width < NarrowWidth)
        {
            ExplorerLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            ExplorerLayoutGrid.RowDefinitions = new RowDefinitions("160,12,1*,12,Auto");

            Grid.SetColumn(ExplorerTreePanel, 0);
            Grid.SetRow(ExplorerTreePanel, 0);

            Grid.SetColumn(ExplorerCenterPanel, 0);
            Grid.SetRow(ExplorerCenterPanel, 2);
            ExplorerCenterPanel.Margin = new Thickness(0);

            Grid.SetColumn(ExplorerDetailsPanel, 0);
            Grid.SetRow(ExplorerDetailsPanel, 4);
            ExplorerDetailsPanel.Margin = new Thickness(0, 10, 0, 0);

            ExplorerLeftSplitter.IsVisible = false;
            ExplorerRightSplitter.IsVisible = false;
            return;
        }

        ExplorerLayoutGrid.RowDefinitions = new RowDefinitions("*");
        ExplorerLayoutGrid.ColumnDefinitions = width < CompactWidth
            ? new ColumnDefinitions("0.74*,4,1.38*,4,0.96*")
            : new ColumnDefinitions("0.82*,4,1.72*,4,1.02*");

        Grid.SetColumn(ExplorerTreePanel, 0);
        Grid.SetRow(ExplorerTreePanel, 0);

        Grid.SetColumn(ExplorerCenterPanel, 2);
        Grid.SetRow(ExplorerCenterPanel, 0);
        ExplorerCenterPanel.Margin = new Thickness(10, 0, 10, 0);

        Grid.SetColumn(ExplorerDetailsPanel, 4);
        Grid.SetRow(ExplorerDetailsPanel, 0);
        ExplorerDetailsPanel.Margin = new Thickness(0, 0, 0, 0);

        ExplorerLeftSplitter.IsVisible = true;
        ExplorerRightSplitter.IsVisible = true;
    }

    private void ApplySnapshotHistoryLayout(double width)
    {
        if (width < SnapshotNarrowWidth)
        {
            SnapshotHistoryLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            SnapshotHistoryLayoutGrid.RowDefinitions = new RowDefinitions("0.95*,12,1.05*,12,1.05*");

            Grid.SetColumn(SnapshotHistoryCommitsPanel, 0);
            Grid.SetRow(SnapshotHistoryCommitsPanel, 0);

            Grid.SetColumn(SnapshotHistoryFilesPanel, 0);
            Grid.SetRow(SnapshotHistoryFilesPanel, 2);
            SnapshotHistoryFilesPanel.Margin = new Thickness(0);

            Grid.SetColumn(SnapshotHistoryVersionsPanel, 0);
            Grid.SetRow(SnapshotHistoryVersionsPanel, 4);

            SnapshotHistoryLeftSplitter.IsVisible = false;
            SnapshotHistoryRightSplitter.IsVisible = false;
            return;
        }

        SnapshotHistoryLayoutGrid.RowDefinitions = new RowDefinitions("*");
        SnapshotHistoryLayoutGrid.ColumnDefinitions = width < SnapshotCompactWidth
            ? new ColumnDefinitions("0.96*,6,1.04*,6,1.02*")
            : new ColumnDefinitions("1.02*,6,1.12*,6,1.16*");

        Grid.SetColumn(SnapshotHistoryCommitsPanel, 0);
        Grid.SetRow(SnapshotHistoryCommitsPanel, 0);

        Grid.SetColumn(SnapshotHistoryFilesPanel, 2);
        Grid.SetRow(SnapshotHistoryFilesPanel, 0);
        SnapshotHistoryFilesPanel.Margin = new Thickness(14, 0);

        Grid.SetColumn(SnapshotHistoryVersionsPanel, 4);
        Grid.SetRow(SnapshotHistoryVersionsPanel, 0);

        SnapshotHistoryLeftSplitter.IsVisible = true;
        SnapshotHistoryRightSplitter.IsVisible = true;
    }

    private void ApplyExplorerItemGridLayout(double width)
    {
        var columnDefinitions = width < NarrowWidth
            ? "32,2.25*,0.9*,0.78*,0"
            : width < CompactWidth
                ? "32,2.45*,0.95*,0.78*,0.9*"
                : "36,3.2*,1*,0.9*,1*";

        foreach (var grid in this.GetVisualDescendants().OfType<Grid>().Where(static g => g.Classes.Contains("explorer-file-grid")))
            grid.ColumnDefinitions = new ColumnDefinitions(columnDefinitions);
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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _observedViewModel = DataContext as INotifyPropertyChanged;
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Dispatcher.UIThread.Post(RefreshTreeState, DispatcherPriority.Background);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepositoryExplorerViewModel.SelectedTreeNode)
            or nameof(RepositoryExplorerViewModel.TreeNodes))
        {
            Dispatcher.UIThread.Post(RefreshTreeState, DispatcherPriority.Background);
        }
    }

    private void RefreshTreeState()
    {
        foreach (var item in ExplorerTreeView.GetVisualDescendants().OfType<TreeViewItem>())
        {
            ObserveTreeItem(item);

            if (item.DataContext is not ExplorerTreeNodeViewModel node)
                continue;

            item.IsExpanded = node.IsExpanded;
            item.IsSelected = node.IsSelected;
        }
    }

    private void ObserveTreeItem(TreeViewItem item)
    {
        if (!_observedTreeItems.Add(item))
            return;

        item.PropertyChanged += OnTreeViewItemPropertyChanged;
        item.DetachedFromVisualTree += OnObservedTreeItemDetached;
    }

    private void OnTreeViewItemPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not TreeViewItem item
            || item.DataContext is not ExplorerTreeNodeViewModel node)
        {
            return;
        }

        if (e.Property == TreeViewItem.IsExpandedProperty)
        {
            node.IsExpanded = item.IsExpanded;

            if (item.IsExpanded)
                Dispatcher.UIThread.Post(RefreshTreeState, DispatcherPriority.Background);
        }
    }

    private void OnObservedTreeItemDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TreeViewItem item)
            return;

        item.PropertyChanged -= OnTreeViewItemPropertyChanged;
        item.DetachedFromVisualTree -= OnObservedTreeItemDetached;
        _observedTreeItems.Remove(item);
    }
}
