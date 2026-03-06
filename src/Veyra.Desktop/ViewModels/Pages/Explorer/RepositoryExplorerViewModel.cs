using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
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
    private const int LiveSyncDebounceMs = 800;
    private const int LiveSyncMinIntervalMs = 1500;

    private readonly IMediator _mediator;
    private readonly ILogger<RepositoryExplorerViewModel> _log;
    private readonly Dictionary<string, ExplorerTreeNodeViewModel> _nodeByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _liveSyncExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _liveSyncLock = new();

    private IReadOnlyList<RepositoryScanEntryDto> _entries = Array.Empty<RepositoryScanEntryDto>();
    private string? _selectedDirectoryPath;
    private FileSystemWatcher? _liveSyncWatcher;
    private Timer? _liveSyncTimer;
    private bool _liveSyncPending;
    private DateTime _lastLiveSyncUtc = DateTime.MinValue;

    public event Action? BackRequested;
    public event Func<int, Task>? OpenSettingsRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    private int _repositoryId;

    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string _repositoryPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    private bool _isLoading;

    [ObservableProperty] private bool _isEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private ExplorerTreeNodeViewModel? _selectedTreeNode;
    [ObservableProperty] private ExplorerItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedItem))]
    private bool _hasSelectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedVersion))]
    [NotifyPropertyChangedFor(nameof(CanRunDiffForSelectedVersion))]
    private ExplorerFileVersionViewModel? _selectedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedVersionHasNoContentBlocks))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedVersion))]
    [NotifyPropertyChangedFor(nameof(CanRunDiffForSelectedVersion))]
    private bool _selectedVersionHasContentBlocks;

    [ObservableProperty] private bool _isVersionLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionActionMessage))]
    private string? _versionActionMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffPreview))]
    private string? _diffPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    private bool _isScanRunning;

    [ObservableProperty] private int _scanPercent;
    [ObservableProperty] private string? _scanMessage;
    [ObservableProperty] private bool _scanIsIndeterminate;
    [ObservableProperty] private bool _isLiveSyncActive;

    [ObservableProperty] private string _pendingChangesSummary = "Изменений с последнего снимка нет.";
    [ObservableProperty] private string _lastSnapshotLabel = "Снимок еще не создан.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPendingChanges))]
    private bool _hasPendingChanges;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasNoSelectedItem => !HasSelectedItem;
    public bool HasVersionActionMessage => !string.IsNullOrWhiteSpace(VersionActionMessage);
    public bool HasDiffPreview => !string.IsNullOrWhiteSpace(DiffPreview);
    public bool SelectedVersionHasNoContentBlocks => SelectedVersion is not null && !SelectedVersionHasContentBlocks;
    public bool HasNoPendingChanges => !HasPendingChanges;
    public bool CanRunScanActions => RepositoryId > 0 && !IsLoading && !IsScanRunning;
    public bool CanRestoreSelectedVersion => SelectedVersion is { HasContentBlocks: true, IsDeletionMarker: false };
    public bool CanRunDiffForSelectedVersion => SelectedVersion is { HasContentBlocks: true, IsDeletionMarker: false };

    public ObservableCollection<ExplorerTreeNodeViewModel> TreeNodes { get; } = [];
    public ObservableCollection<ExplorerItemViewModel> Items { get; } = [];
    public ObservableCollection<ExplorerFileVersionViewModel> FileVersions { get; } = [];
    public ObservableCollection<RepositoryPendingChangeViewModel> PendingChanges { get; } = [];

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
            StopLiveSync();
            IsLoading = true;
            ErrorMessage = null;
            VersionActionMessage = null;
            DiffPreview = null;
            SelectedItem = null;
            SelectedVersion = null;
            FileVersions.Clear();
            PendingChanges.Clear();
            HasPendingChanges = false;
            PendingChangesSummary = "Изменений с последнего снимка нет.";
            LastSnapshotLabel = "Снимок еще не создан.";

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

            await RefreshEntriesAndTreeAsync(clearSelection: true);
            StartLiveSync(repo.DirectoryPath, repo.LinkedFormats);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load explorer for repository {RepositoryId}", repositoryId);
            ErrorMessage = "Не удалось загрузить содержимое репозитория.";
            Items.Clear();
            TreeNodes.Clear();
            FileVersions.Clear();
            PendingChanges.Clear();
            HasPendingChanges = false;
            PendingChangesSummary = "Не удалось загрузить изменения.";
            LastSnapshotLabel = "Снимок еще не создан.";
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
        _ = LoadVersionsForSelectedItemAsync(value);
    }

    partial void OnSelectedVersionChanged(ExplorerFileVersionViewModel? value)
    {
        SelectedVersionHasContentBlocks = value?.HasContentBlocks == true;
    }

    [RelayCommand]
    private void Back()
    {
        StopLiveSync();
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
        await ExecuteScanAsync(
            saveFileVersions: false,
            triggerOverride: "sync_index_manual",
            fallbackMessage: "Синхронизация индекса...",
            showErrors: true,
            successMessage: "Индекс синхронизирован.");
    }

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        await ExecuteScanAsync(
            saveFileVersions: true,
            triggerOverride: "manual_snapshot",
            fallbackMessage: "Создание снимка...",
            showErrors: true,
            successMessage: "Снимок версии сохранен.");
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

    [RelayCommand]
    private async Task RestoreVersionOverwriteAsync()
    {
        var selectedItem = SelectedItem;
        var selectedVersion = SelectedVersion;

        if (selectedItem is null || selectedItem.IsDirectory || selectedVersion is null)
            return;

        if (!selectedVersion.HasContentBlocks)
        {
            ErrorMessage = "Для выбранной версии отсутствуют блоки содержимого.";
            return;
        }

        VersionActionMessage = null;
        ErrorMessage = null;

        var result = await Task.Run(() => _mediator.Send(new RestoreFileVersionCommand(
            RepositoryId,
            selectedItem.RelativePath,
            selectedVersion.FileVersionId,
            OverwriteCurrent: true)));

        if (!result.Success)
        {
            ErrorMessage = result.Error ?? "Не удалось восстановить файл.";
            return;
        }

        VersionActionMessage = $"Файл восстановлен поверх текущего:\n{result.Value}";
    }

    [RelayCommand]
    private async Task RestoreVersionAsCopyAsync()
    {
        var selectedItem = SelectedItem;
        var selectedVersion = SelectedVersion;

        if (selectedItem is null || selectedItem.IsDirectory || selectedVersion is null)
            return;

        if (!selectedVersion.HasContentBlocks)
        {
            ErrorMessage = "Для выбранной версии отсутствуют блоки содержимого.";
            return;
        }

        VersionActionMessage = null;
        ErrorMessage = null;

        var result = await Task.Run(() => _mediator.Send(new RestoreFileVersionCommand(
            RepositoryId,
            selectedItem.RelativePath,
            selectedVersion.FileVersionId,
            OverwriteCurrent: false,
            TargetPath: null)));

        if (!result.Success)
        {
            ErrorMessage = result.Error ?? "Не удалось восстановить файл в новый путь.";
            return;
        }

        VersionActionMessage = $"Файл восстановлен в новый путь:\n{result.Value}";
    }

    [RelayCommand]
    private async Task CompareSelectedWithPreviousAsync()
    {
        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
            return;

        if (!selectedVersion.HasContentBlocks || selectedVersion.IsDeletionMarker)
        {
            ErrorMessage = "Diff недоступен для выбранной версии.";
            return;
        }

        var ordered = FileVersions.ToList();
        var index = ordered.FindIndex(v => v.FileVersionId == selectedVersion.FileVersionId);
        if (index < 0)
        {
            ErrorMessage = "Не удалось определить позицию версии.";
            return;
        }

        var previous = ordered
            .Skip(index + 1)
            .FirstOrDefault(v => v.HasContentBlocks && !v.IsDeletionMarker);

        if (previous is null)
        {
            ErrorMessage = "Предыдущая версия с содержимым не найдена.";
            return;
        }

        ErrorMessage = null;
        DiffPreview = null;

        var diffResult = await Task.Run(() => _mediator.Send(new GetTextDiffQuery(
            selectedVersion.FileVersionId,
            previous.FileVersionId,
            4000)));

        if (!diffResult.Success || diffResult.Value is null)
        {
            ErrorMessage = diffResult.Error ?? "Не удалось построить diff.";
            return;
        }

        var value = diffResult.Value;
        VersionActionMessage = $"Diff готов\n+{value.AddedLines} / -{value.RemovedLines}";

        var lines = value.Lines.Take(60)
            .Select(line => line.Kind switch
            {
                "add" => "+ " + line.Text,
                "remove" => "- " + line.Text,
                _ => "  " + line.Text
            });

        var builder = new StringBuilder();
        foreach (var line in lines)
            builder.AppendLine(line);

        if (value.IsTruncated)
            builder.AppendLine("... diff обрезан из-за лимита строк ...");

        DiffPreview = builder.ToString().TrimEnd();
    }

    private async Task<bool> ExecuteScanAsync(
        bool saveFileVersions,
        string triggerOverride,
        string fallbackMessage,
        bool showErrors,
        string? successMessage)
    {
        if (RepositoryId == 0)
            return false;

        if (!await _scanGate.WaitAsync(0))
        {
            if (showErrors)
                ErrorMessage = "Сканирование уже выполняется.";
            return false;
        }

        var success = false;

        try
        {
            if (showErrors)
                ErrorMessage = null;

            PrepareScanUi(fallbackMessage);
            await Task.Yield();

            var progress = new Progress<RepositoryScanProgressDto>(p =>
            {
                var nextPercent = Math.Clamp(p.Percent, 0, 100);
                if (nextPercent < ScanPercent)
                    nextPercent = ScanPercent;

                ScanPercent = nextPercent;
                ScanIsIndeterminate = p.FilesTotal <= 0 && p.Percent < 100;

                if (!string.IsNullOrWhiteSpace(p.Message))
                    ScanMessage = p.Message;
            });

            var result = await Task.Run(() => _mediator.Send(new ScanRepositoryCommand(
                RepositoryId,
                progress,
                new RepositoryScanOptionsDto(
                    SaveFileVersions: saveFileVersions,
                    TriggerOverride: triggerOverride))));

            if (!result.Success)
            {
                if (showErrors)
                    ErrorMessage = result.Error ?? "Сканирование завершилось с ошибкой.";
                return false;
            }

            await RefreshEntriesAndTreeAsync(clearSelection: false);

            if (!string.IsNullOrWhiteSpace(successMessage))
                VersionActionMessage = successMessage;

            success = true;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to scan repository {RepositoryId}. SaveVersions {SaveVersions}. Trigger {Trigger}",
                RepositoryId,
                saveFileVersions,
                triggerOverride);

            if (showErrors)
                ErrorMessage = "Не удалось выполнить сканирование репозитория.";
            return false;
        }
        finally
        {
            FinishScanUi(success);
            _scanGate.Release();
        }
    }

    private async Task LoadVersionsForSelectedItemAsync(ExplorerItemViewModel? item)
    {
        FileVersions.Clear();
        SelectedVersion = null;
        DiffPreview = null;
        VersionActionMessage = null;

        if (item is null || item.IsDirectory || RepositoryId == 0)
            return;

        try
        {
            IsVersionLoading = true;

            var versions = await _mediator.Send(new GetFileVersionHistoryQuery(
                RepositoryId,
                item.RelativePath,
                40));

            foreach (var version in versions)
            {
                FileVersions.Add(new ExplorerFileVersionViewModel
                {
                    FileVersionId = version.FileVersionId,
                    CreatedAtUtc = version.CreatedAtUtc,
                    SizeBytes = version.SizeBytes,
                    IsDeletionMarker = version.IsDeletionMarker,
                    HasContentBlocks = version.HasContentBlocks,
                    ContentHashSha256 = version.ContentHashSha256
                });
            }

            SelectedVersion = FileVersions.FirstOrDefault(v => v.HasContentBlocks && !v.IsDeletionMarker)
                              ?? FileVersions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to load file versions. RepositoryId {RepositoryId}. Path {Path}",
                RepositoryId,
                item.RelativePath);
            ErrorMessage = "Не удалось загрузить версии файла.";
        }
        finally
        {
            IsVersionLoading = false;
        }
    }

    private async Task LoadPendingChangesAsync()
    {
        var pending = await _mediator.Send(new GetRepositoryPendingChangesQuery(RepositoryId, 400));

        PendingChanges.Clear();
        foreach (var entry in pending.Entries)
        {
            PendingChanges.Add(new RepositoryPendingChangeViewModel
            {
                RelativePath = entry.RelativePath,
                Name = entry.Name,
                ChangeKind = entry.ChangeKind,
                CurrentSizeBytes = entry.CurrentSizeBytes,
                BaselineSizeBytes = entry.BaselineSizeBytes
            });
        }

        HasPendingChanges = PendingChanges.Count > 0;

        if (pending.BaselineSnapshotAtUtc is null)
        {
            LastSnapshotLabel = "Снимок еще не создан.";
            PendingChangesSummary = HasPendingChanges
                ? $"К фиксации {PendingChanges.Count} файлов"
                : "Снимков пока нет. Создайте первый снимок.";
            return;
        }

        var local = pending.BaselineSnapshotAtUtc.Value.ToLocalTime();
        LastSnapshotLabel = $"Последний снимок {local:yyyy-MM-dd HH:mm:ss}";

        PendingChangesSummary = pending.AddedCount + pending.ModifiedCount + pending.DeletedCount == 0
            ? "Изменений с последнего снимка нет."
            : $"Изменений +{pending.AddedCount} ~{pending.ModifiedCount} -{pending.DeletedCount}";
    }

    private async Task RefreshEntriesAndTreeAsync(bool clearSelection)
    {
        if (RepositoryId == 0)
            return;

        var previousDirectoryPath = clearSelection ? null : _selectedDirectoryPath;
        var previousItemPath = clearSelection ? null : SelectedItem?.RelativePath;

        _entries = await _mediator.Send(new GetRepositoryLatestEntriesQuery(RepositoryId));
        await LoadPendingChangesAsync();
        BuildTree();

        if (!string.IsNullOrWhiteSpace(previousDirectoryPath)
            && _nodeByPath.TryGetValue(previousDirectoryPath, out var previousNode))
        {
            previousNode.IsExpanded = true;
            SelectedTreeNode = previousNode;
            _selectedDirectoryPath = previousDirectoryPath;
            ShowItemsForPath(previousDirectoryPath);
        }
        else
        {
            _selectedDirectoryPath = null;
            ShowItemsForPath(null);
            SelectedTreeNode = TreeNodes.FirstOrDefault();
        }

        if (!clearSelection && !string.IsNullOrWhiteSpace(previousItemPath))
        {
            SelectedItem = Items.FirstOrDefault(i =>
                i.RelativePath.Equals(previousItemPath, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            SelectedItem = null;
        }
    }

    private void PrepareScanUi(string fallbackMessage)
    {
        IsScanRunning = true;
        ScanPercent = 0;
        ScanMessage = fallbackMessage;
        ScanIsIndeterminate = true;
    }

    private void FinishScanUi(bool success)
    {
        if (success)
        {
            ScanPercent = 100;
            ScanMessage = "Готово";
        }

        ScanIsIndeterminate = false;
        IsScanRunning = false;
    }

    private void StartLiveSync(string rootPath, IReadOnlyCollection<string> linkedFormats)
    {
        StopLiveSync();

        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return;

        _liveSyncExtensions.Clear();
        foreach (var ext in NormalizeTrackedExtensions(linkedFormats))
            _liveSyncExtensions.Add(ext);

        try
        {
            _liveSyncWatcher = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
            };

            _liveSyncWatcher.Changed += OnLiveSyncChanged;
            _liveSyncWatcher.Created += OnLiveSyncChanged;
            _liveSyncWatcher.Deleted += OnLiveSyncChanged;
            _liveSyncWatcher.Renamed += OnLiveSyncRenamed;
            _liveSyncWatcher.Error += OnLiveSyncError;
            _liveSyncWatcher.EnableRaisingEvents = true;

            _liveSyncTimer = new Timer(
                _ => Dispatcher.UIThread.Post(() => _ = FlushQueuedLiveSyncAsync()),
                null,
                Timeout.Infinite,
                Timeout.Infinite);

            _liveSyncPending = false;
            _lastLiveSyncUtc = DateTime.MinValue;
            IsLiveSyncActive = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to start live sync watcher for {Path}", rootPath);
            StopLiveSync();
        }
    }

    private void StopLiveSync()
    {
        IsLiveSyncActive = false;

        if (_liveSyncWatcher is not null)
        {
            _liveSyncWatcher.EnableRaisingEvents = false;
            _liveSyncWatcher.Changed -= OnLiveSyncChanged;
            _liveSyncWatcher.Created -= OnLiveSyncChanged;
            _liveSyncWatcher.Deleted -= OnLiveSyncChanged;
            _liveSyncWatcher.Renamed -= OnLiveSyncRenamed;
            _liveSyncWatcher.Error -= OnLiveSyncError;
            _liveSyncWatcher.Dispose();
            _liveSyncWatcher = null;
        }

        _liveSyncTimer?.Dispose();
        _liveSyncTimer = null;
        _liveSyncExtensions.Clear();

        lock (_liveSyncLock)
        {
            _liveSyncPending = false;
        }
    }

    private void OnLiveSyncChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldQueueLiveSync(e.FullPath))
            QueueLiveSync();
    }

    private void OnLiveSyncRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldQueueLiveSync(e.FullPath) || ShouldQueueLiveSync(e.OldFullPath))
            QueueLiveSync();
    }

    private void OnLiveSyncError(object sender, ErrorEventArgs e)
    {
        _log.LogWarning(e.GetException(), "Live sync watcher error for repository {RepositoryId}", RepositoryId);
        QueueLiveSync();
    }

    private bool ShouldQueueLiveSync(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        if (_liveSyncExtensions.Count == 0)
            return true;

        var ext = Path.GetExtension(fullPath);
        if (string.IsNullOrWhiteSpace(ext))
            return true;

        var normalized = ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
        return _liveSyncExtensions.Contains(normalized);
    }

    private void QueueLiveSync()
    {
        if (!IsLiveSyncActive || RepositoryId == 0)
            return;

        lock (_liveSyncLock)
        {
            _liveSyncPending = true;
            try
            {
                _liveSyncTimer?.Change(LiveSyncDebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task FlushQueuedLiveSyncAsync()
    {
        bool shouldRun;

        lock (_liveSyncLock)
        {
            shouldRun = _liveSyncPending;
            _liveSyncPending = false;
        }

        if (!shouldRun || RepositoryId == 0)
            return;

        if (IsLoading || IsScanRunning)
        {
            QueueLiveSync();
            return;
        }

        var elapsedMs = (DateTime.UtcNow - _lastLiveSyncUtc).TotalMilliseconds;
        if (elapsedMs < LiveSyncMinIntervalMs)
        {
            lock (_liveSyncLock)
            {
                _liveSyncPending = true;
                try
                {
                    _liveSyncTimer?.Change(LiveSyncMinIntervalMs - (int)elapsedMs, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            return;
        }

        _lastLiveSyncUtc = DateTime.UtcNow;

        await ExecuteScanAsync(
            saveFileVersions: false,
            triggerOverride: "sync_live_watcher",
            fallbackMessage: "Синхронизация изменений...",
            showErrors: false,
            successMessage: null);
    }

    private static IReadOnlyList<string> NormalizeTrackedExtensions(IEnumerable<string> values)
    {
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Select(v => v.StartsWith('.') ? v : "." + v)
            .Select(v => v.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        if (delta.TotalSeconds < 60) return "Только что";
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

