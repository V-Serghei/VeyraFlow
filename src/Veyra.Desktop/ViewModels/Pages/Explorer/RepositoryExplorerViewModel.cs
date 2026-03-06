using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Application.Queries;
using Veyra.Application.Queries.Repository;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed partial class RepositoryExplorerViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly ILogger<RepositoryExplorerViewModel> _log;
    private readonly Dictionary<string, ExplorerTreeNodeViewModel> _nodeByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<RepositoryScanEntryDto> _entries = Array.Empty<RepositoryScanEntryDto>();
    private string? _selectedDirectoryPath;

    public event Action? BackRequested;
    public event Func<int, Task>? OpenSettingsRequested;

    [ObservableProperty] private int _repositoryId;
    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string _repositoryPath = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private ExplorerTreeNodeViewModel? _selectedTreeNode;
    [ObservableProperty] private ExplorerItemViewModel? _selectedItem;
    [ObservableProperty] private bool _hasSelectedItem;

    public ObservableCollection<ExplorerTreeNodeViewModel> TreeNodes { get; } = [];
    public ObservableCollection<ExplorerItemViewModel> Items { get; } = [];

    public RepositoryExplorerViewModel(
        IMediator mediator,
        ILogger<RepositoryExplorerViewModel> log)
    {
        _mediator = mediator;
        _log = log;
    }

    public async Task LoadAsync(int repositoryId)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            SelectedItem = null;

            var repo = await _mediator.Send(new GetRepositoryDetailQuery(repositoryId));
            if (repo is null)
            {
                ErrorMessage = "Репозиторий не найден.";
                IsEmpty = true;
                return;
            }

            RepositoryId = repo.Id;
            RepositoryName = repo.Name;
            RepositoryPath = repo.DirectoryPath;

            _entries = await _mediator.Send(new GetRepositoryLatestEntriesQuery(repositoryId));
            BuildTree();
            ShowItemsForPath(null);
            IsEmpty = Items.Count == 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load explorer for repository {RepositoryId}", repositoryId);
            ErrorMessage = "Не удалось загрузить содержимое репозитория.";
            Items.Clear();
            TreeNodes.Clear();
            IsEmpty = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchQueryChanged(string value) => ShowItemsForPath(_selectedDirectoryPath);

    partial void OnSelectedTreeNodeChanged(ExplorerTreeNodeViewModel? value)
    {
        if (value is not null)
            SelectTreeNode(value);
    }

    partial void OnSelectedItemChanged(ExplorerItemViewModel? value)
    {
        HasSelectedItem = value is not null;
    }

    [RelayCommand]
    private void Back()
    {
        BackRequested?.Invoke();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (RepositoryId == 0 || OpenSettingsRequested is null)
            return;

        await OpenSettingsRequested.Invoke(RepositoryId);
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (RepositoryId == 0)
            return;

        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var result = await _mediator.Send(new ScanRepositoryCommand(RepositoryId));
            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Ошибка сканирования.";
                return;
            }

            await LoadAsync(RepositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to rescan repository {RepositoryId}", RepositoryId);
            ErrorMessage = "Не удалось пересканировать репозиторий.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectTreeNode(ExplorerTreeNodeViewModel? node)
    {
        if (node is null)
            return;

        _selectedDirectoryPath = string.IsNullOrWhiteSpace(node.RelativePath)
            ? null
            : node.RelativePath;

        ShowItemsForPath(_selectedDirectoryPath);
    }

    [RelayCommand]
    private void SelectItem(ExplorerItemViewModel? item)
    {
        if (item is null)
            return;

        SelectedItem = item;
    }

    [RelayCommand]
    private void OpenItem(ExplorerItemViewModel? item)
    {
        if (item is null || !item.IsDirectory)
            return;

        if (_nodeByPath.TryGetValue(item.RelativePath, out var node))
        {
            node.IsExpanded = true;
            SelectedTreeNode = node;
            SelectTreeNode(node);
        }
    }

    private void BuildTree()
    {
        _nodeByPath.Clear();
        TreeNodes.Clear();

        var root = new ExplorerTreeNodeViewModel
        {
            RelativePath = string.Empty,
            Name = RepositoryName,
            IsExpanded = true,
            IsSelected = true
        };

        _nodeByPath[string.Empty] = root;

        var directories = _entries
            .Where(e => e.IsDirectory)
            .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dir in directories)
        {
            var node = new ExplorerTreeNodeViewModel
            {
                RelativePath = dir.RelativePath,
                Name = dir.Name
            };

            _nodeByPath[dir.RelativePath] = node;
        }

        foreach (var dir in directories)
        {
            if (!_nodeByPath.TryGetValue(dir.RelativePath, out var node))
                continue;

            if (string.IsNullOrWhiteSpace(dir.ParentRelativePath))
            {
                root.Children.Add(node);
                continue;
            }

            if (_nodeByPath.TryGetValue(dir.ParentRelativePath, out var parent))
                parent.Children.Add(node);
            else
                root.Children.Add(node);
        }

        SortNodes(root);
        TreeNodes.Add(root);
        SelectedTreeNode = root;
        _selectedDirectoryPath = null;
    }

    private void ShowItemsForPath(string? directoryRelativePath)
    {
        IEnumerable<RepositoryScanEntryDto> visible = _entries.Where(e =>
            string.Equals(NormalizeParent(e.ParentRelativePath), NormalizeParent(directoryRelativePath), StringComparison.OrdinalIgnoreCase));

        var query = SearchQuery.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            visible = visible.Where(e =>
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var rows = visible
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(MapToItem)
            .ToList();

        Items.Clear();
        foreach (var row in rows)
            Items.Add(row);

        IsEmpty = Items.Count == 0;
    }

    private static ExplorerItemViewModel MapToItem(RepositoryScanEntryDto entry)
    {
        var type = entry.IsDirectory
            ? "Папка"
            : string.IsNullOrWhiteSpace(entry.Extension)
                ? "Файл"
                : entry.Extension.TrimStart('.').ToUpperInvariant();

        return new ExplorerItemViewModel
        {
            RelativePath = entry.RelativePath,
            ParentRelativePath = entry.ParentRelativePath,
            IsDirectory = entry.IsDirectory,
            Name = entry.Name,
            Type = type,
            SizeDisplay = entry.IsDirectory ? "—" : FormatSize(entry.SizeBytes),
            ModifiedDisplay = FormatLastActivity(entry.LastWriteUtc),
            HashSha256 = entry.ContentHashSha256
        };
    }

    private static void SortNodes(ExplorerTreeNodeViewModel node)
    {
        var sorted = node.Children
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        node.Children.Clear();
        foreach (var child in sorted)
        {
            SortNodes(child);
            node.Children.Add(child);
        }
    }

    private static string? NormalizeParent(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FormatLastActivity(DateTime utc)
    {
        var delta = DateTime.UtcNow - utc;
        if (delta.TotalSeconds < 60) return "только что";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} мин назад";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} ч назад";
        return $"{(int)delta.TotalDays} дн назад";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} КБ";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} МБ";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} ГБ";
    }
}
