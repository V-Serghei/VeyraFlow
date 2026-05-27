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

    private void OnExplorerFiltersBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseExplorerFiltersAndRestoreFocus();
        e.Handled = true;
    }

    private void OnExplorerFiltersCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseExplorerFiltersAndRestoreFocus();
        e.Handled = true;
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (DataContext is not RepositoryExplorerViewModel { IsExplorerFiltersVisible: true })
            return;

        CloseExplorerFiltersAndRestoreFocus();
        e.Handled = true;
    }

    private void CloseExplorerFiltersAndRestoreFocus()
    {
        if (DataContext is RepositoryExplorerViewModel vm)
            vm.IsExplorerFiltersVisible = false;

        Dispatcher.UIThread.Post(
            () => ExplorerFiltersButton.Focus(),
            DispatcherPriority.Background);
    }

    private void OnFileHistoryBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender))
            return;

        if (DataContext is RepositoryExplorerViewModel vm)
            vm.IsFileHistoryMenuOpen = false;

        e.Handled = true;
    }

    private void OnDiffPreviewBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender))
            return;

        if (DataContext is RepositoryExplorerViewModel vm)
            vm.IsDiffPreviewMenuOpen = false;

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
        ApplyExplorerItemGridLayout(width);
    }

    private void ApplyHeroLayout(double width)
    {
        if (width < NarrowWidth)
        {
            ExplorerHeroGrid.ColumnDefinitions = new ColumnDefinitions("*");
            ExplorerHeroGrid.RowDefinitions = new RowDefinitions("Auto");
            Grid.SetColumn(ExplorerHeroActionsPanel, 0);
            Grid.SetRow(ExplorerHeroActionsPanel, 0);
            Grid.SetColumnSpan(ExplorerHeroActionsPanel, 1);
            ExplorerHeroActionsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            return;
        }

        ExplorerHeroGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        ExplorerHeroGrid.RowDefinitions = new RowDefinitions("Auto");
        Grid.SetColumn(ExplorerHeroActionsPanel, 1);
        Grid.SetRow(ExplorerHeroActionsPanel, 0);
        Grid.SetColumnSpan(ExplorerHeroActionsPanel, 1);
        ExplorerHeroActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
    }

    private void ApplyExplorerPanelsLayout(double width)
    {
        if (width < NarrowWidth)
        {
            ExplorerLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            ExplorerLayoutGrid.RowDefinitions = new RowDefinitions("152,10,1.12*,6,0.88*");

            Grid.SetColumn(ExplorerTreePanel, 0);
            Grid.SetRow(ExplorerTreePanel, 0);

            Grid.SetColumn(ExplorerCenterPanel, 0);
            Grid.SetRow(ExplorerCenterPanel, 2);
            ExplorerCenterPanel.Margin = new Thickness(0);

            Grid.SetColumn(ExplorerDetailsPanel, 0);
            Grid.SetRow(ExplorerDetailsPanel, 4);
            ExplorerDetailsPanel.Margin = new Thickness(0, 10, 0, 0);

            Grid.SetColumn(ExplorerBottomSplitter, 0);
            Grid.SetRow(ExplorerBottomSplitter, 3);
            Grid.SetColumnSpan(ExplorerBottomSplitter, 1);

            ExplorerLeftSplitter.IsVisible = false;
            ExplorerRightSplitter.IsVisible = false;
            ExplorerBottomSplitter.IsVisible = true;
            return;
        }

        ExplorerLayoutGrid.RowDefinitions = new RowDefinitions("*");
        ExplorerLayoutGrid.ColumnDefinitions = width < CompactWidth
            ? new ColumnDefinitions("0.72*,4,1.34*,4,1.12*")
            : new ColumnDefinitions("0.78*,4,1.56*,4,1.14*");
        ApplyExplorerColumnMinimums(width);

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
        ExplorerBottomSplitter.IsVisible = false;
    }

    private void ApplyExplorerItemGridLayout(double width)
    {
        var columnDefinitions = width < NarrowWidth
            ? "32,2.1*,0.8*,0.74*,0,40"
            : width < CompactWidth
                ? "32,2.35*,0.85*,0.74*,0.82*,42"
                : "36,2.7*,0.9*,0.8*,0.95*,44";

        foreach (var grid in this.GetVisualDescendants().OfType<Grid>().Where(static g => g.Classes.Contains("explorer-file-grid")))
            grid.ColumnDefinitions = new ColumnDefinitions(columnDefinitions);
    }

    private void ApplyExplorerColumnMinimums(double width)
    {
        if (ExplorerLayoutGrid.ColumnDefinitions.Count < 5)
            return;

        if (width < CompactWidth)
        {
            ExplorerLayoutGrid.ColumnDefinitions[0].MinWidth = 210;
            ExplorerLayoutGrid.ColumnDefinitions[2].MinWidth = 420;
            ExplorerLayoutGrid.ColumnDefinitions[4].MinWidth = 320;
            return;
        }

        ExplorerLayoutGrid.ColumnDefinitions[0].MinWidth = 240;
        ExplorerLayoutGrid.ColumnDefinitions[2].MinWidth = 460;
        ExplorerLayoutGrid.ColumnDefinitions[4].MinWidth = 340;
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
