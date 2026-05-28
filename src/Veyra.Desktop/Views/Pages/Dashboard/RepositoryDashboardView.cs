using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Veyra.Desktop.Localization;
using Veyra.Desktop.ViewModels.Pages.Dashboard;

namespace Veyra.Desktop.Views.Pages.Dashboard;

public partial class RepositoryDashboardView: UserControl
{
    private RepositoryDashboardViewModel? _viewModel;
    private readonly HashSet<TreeViewItem> _observedFolderTreeItems = [];

    public RepositoryDashboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DashboardFolderTreeView.LayoutUpdated += (_, _) => RefreshFolderTreeState();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as RepositoryDashboardViewModel;

        if (DataContext is not RepositoryDashboardViewModel vm)
            return;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateRepositoryGridColumns();
        Dispatcher.UIThread.Post(RefreshFolderTreeState, DispatcherPriority.Background);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepositoryDashboardViewModel.IsCompactCards)
            or nameof(RepositoryDashboardViewModel.IsListView)
            or nameof(RepositoryDashboardViewModel.IsAdvancedFiltersVisible))
        {
            UpdateRepositoryGridColumns();
        }

        if (e.PropertyName is nameof(RepositoryDashboardViewModel.FolderTreeRoots)
            or nameof(RepositoryDashboardViewModel.SelectedFolderNode))
        {
            Dispatcher.UIThread.Post(RefreshFolderTreeState, DispatcherPriority.Background);
        }
    }

    private void OnRepositoryHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateRepositoryGridColumns();
    }

    private void OnAdvancedFiltersBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RepositoryDashboardViewModel vm)
            vm.IsAdvancedFiltersVisible = false;

        e.Handled = true;
    }

    private void UpdateRepositoryGridColumns()
    {
        if (DataContext is RepositoryDashboardViewModel vm)
            vm.UpdateRepositoryGridColumns(DashboardRepositoryHost.Bounds.Width);
    }

    private void RefreshFolderTreeState()
    {
        foreach (var item in DashboardFolderTreeView.GetVisualDescendants().OfType<TreeViewItem>())
        {
            ObserveFolderTreeItem(item);

            if (item.DataContext is not DashboardFolderTreeNodeViewModel node)
                continue;

            item.IsExpanded = node.IsExpanded;
            item.IsSelected = node.IsSelected;
            item.ContextMenu = BuildFolderNodeContextMenu(node);
        }
    }

    private void ObserveFolderTreeItem(TreeViewItem item)
    {
        if (!_observedFolderTreeItems.Add(item))
            return;

        item.PropertyChanged += OnFolderTreeItemPropertyChanged;
        item.DetachedFromVisualTree += OnObservedFolderTreeItemDetached;
    }

    private void OnFolderTreeItemPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not TreeViewItem item
            || item.DataContext is not DashboardFolderTreeNodeViewModel node)
        {
            return;
        }

        if (e.Property == TreeViewItem.IsExpandedProperty)
        {
            node.IsExpanded = item.IsExpanded;

            if (item.IsExpanded && DataContext is RepositoryDashboardViewModel vm)
            {
                vm.ExpandFolderNode(node);
                Dispatcher.UIThread.Post(RefreshFolderTreeState, DispatcherPriority.Background);
            }
        }
    }

    private void OnObservedFolderTreeItemDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TreeViewItem item)
            return;

        item.PropertyChanged -= OnFolderTreeItemPropertyChanged;
        item.ContextMenu = null;
        item.DetachedFromVisualTree -= OnObservedFolderTreeItemDetached;
        _observedFolderTreeItems.Remove(item);
    }

    private ContextMenu? BuildFolderNodeContextMenu(DashboardFolderTreeNodeViewModel node)
    {
        var menuItems = new List<MenuItem>();

        if (node.CanCreateRepository)
        {
            menuItems.Add(new MenuItem
            {
                Header = Loc.T("dashboard.folder_menu_create_repository_here"),
                CommandParameter = node
            });
            menuItems[^1].Click += OnCreateRepositoryFromTreeMenuClick;
        }

        if (node.CanOpenRepositoryActions)
        {
            menuItems.Add(new MenuItem
            {
                Header = Loc.T("dashboard.folder_menu_repository_settings"),
                CommandParameter = node
            });
            menuItems[^1].Click += OnOpenRepositorySettingsFromTreeMenuClick;

            menuItems.Add(new MenuItem
            {
                Header = Loc.T("dashboard.folder_menu_delete_repository"),
                CommandParameter = node
            });
            menuItems[^1].Click += OnDeleteRepositoryFromTreeMenuClick;
        }

        if (menuItems.Count == 0)
            return null;

        return new ContextMenu
        {
            ItemsSource = menuItems
        };
    }

    private void OnCreateRepositoryFromTreeMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteFolderMenuCommand(sender, commandKind: "create");

    private void OnOpenRepositorySettingsFromTreeMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteFolderMenuCommand(sender, commandKind: "settings");

    private void OnDeleteRepositoryFromTreeMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteFolderMenuCommand(sender, commandKind: "delete");

    private void ExecuteFolderMenuCommand(object? sender, string commandKind)
    {
        if (sender is not MenuItem { CommandParameter: DashboardFolderTreeNodeViewModel node }
            || DataContext is not RepositoryDashboardViewModel vm)
        {
            return;
        }

        DashboardFolderTreeView.SelectedItem = node;
        ICommand? command = commandKind switch
        {
            "create" => vm.CreateRepositoryFromFolderCommand,
            "settings" => vm.OpenRepositorySettingsFromFolderCommand,
            "delete" => vm.DeleteRepositoryFromFolderCommand,
            _ => null
        };

        if (command?.CanExecute(node) == true)
            command.Execute(node);
    }
}
