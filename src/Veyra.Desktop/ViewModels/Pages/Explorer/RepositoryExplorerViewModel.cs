using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.Queries;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed partial class RepositoryExplorerViewModel : ObservableObject
{
    private const int LiveSyncDebounceMs = 800;
    private const int LiveSyncMinIntervalMs = 1500;

    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
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
    private bool _isSyncingSelectionFromSnapshot;
    private long? _preferredSnapshotFileVersionId;

    public event Action? BackRequested;
    public event Func<int, Task>? OpenSettingsRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRunMaintenanceActions))]
    private int _repositoryId;

    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string _repositoryPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRunMaintenanceActions))]
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
    [NotifyPropertyChangedFor(nameof(CanCompareSelectedVersionPair))]
    private ExplorerFileVersionViewModel? _selectedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompareSelectedVersionPair))]
    private ExplorerFileVersionViewModel? _compareLeftVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompareSelectedVersionPair))]
    private ExplorerFileVersionViewModel? _compareRightVersion;

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
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRunMaintenanceActions))]
    private bool _isScanRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRunMaintenanceActions))]
    private bool _isMaintenanceRunning;

    [ObservableProperty] private int _scanPercent;
    [ObservableProperty] private string? _scanMessage;
    [ObservableProperty] private bool _scanIsIndeterminate;
    [ObservableProperty] private bool _isLiveSyncActive;

    [ObservableProperty] private string _pendingChangesSummary = "Ð˜Ð·Ð¼ÐµÐ½ÐµÐ½Ð¸Ð¹ Ñ Ð¿Ð¾ÑÐ»ÐµÐ´Ð½ÐµÐ³Ð¾ ÑÐ½Ð¸Ð¼ÐºÐ° Ð½ÐµÑ‚.";
    [ObservableProperty] private string _lastSnapshotLabel = "Ð¡Ð½Ð¸Ð¼Ð¾Ðº ÐµÑ‰Ðµ Ð½Ðµ ÑÐ¾Ð·Ð´Ð°Ð½.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPendingChanges))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    private bool _hasPendingChanges;
    [ObservableProperty] private bool _isSnapshotHistoryLoading;
    [ObservableProperty] private bool _isSnapshotFilesLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSnapshotHistory))]
    private bool _hasSnapshotHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedSnapshot))]
    [NotifyPropertyChangedFor(nameof(HasNoSnapshotFiles))]
    private RepositorySnapshotHistoryEntryViewModel? _selectedSnapshot;

    [ObservableProperty]
    private RepositorySnapshotFileChangeViewModel? _selectedSnapshotFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSnapshotFiles))]
    private bool _hasSnapshotFiles;

    [ObservableProperty] private bool _isSnapshotHistoryMenuOpen;
    [ObservableProperty] private bool _isDiffPreviewMenuOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDiffPreviewRows))]
    private bool _isDiffPreviewLoading;
    [ObservableProperty] private string _diffPreviewTitle = string.Empty;
    [ObservableProperty] private string _diffPreviewSummary = string.Empty;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasNoSelectedItem => !HasSelectedItem;
    public bool HasVersionActionMessage => !string.IsNullOrWhiteSpace(VersionActionMessage);
    public bool HasDiffPreview => !string.IsNullOrWhiteSpace(DiffPreview);
    public bool SelectedVersionHasNoContentBlocks => SelectedVersion is not null && !SelectedVersionHasContentBlocks;
    public bool HasNoPendingChanges => !HasPendingChanges;
    public bool HasNoSnapshotHistory => !HasSnapshotHistory;
    public bool HasNoSelectedSnapshot => SelectedSnapshot is null;
    public bool HasNoSnapshotFiles => SelectedSnapshot is not null && !HasSnapshotFiles;
    public bool HasDiffPreviewRows => DiffPreviewRows.Count > 0;
    public bool HasNoDiffPreviewRows => !IsDiffPreviewLoading && !HasDiffPreviewRows;
    public bool CanRunScanActions => RepositoryId > 0 && !IsLoading && !IsScanRunning && !IsMaintenanceRunning;
    public bool CanRunMaintenanceActions => RepositoryId > 0 && !IsLoading && !IsScanRunning && !IsMaintenanceRunning;
    public bool CanCreateSnapshot => CanRunScanActions && HasPendingChanges;
    public bool CanRestoreSelectedVersion => SelectedVersion is { HasContentBlocks: true, IsDeletionMarker: false };
    public bool CanRunDiffForSelectedVersion => SelectedVersion is { HasContentBlocks: true, IsDeletionMarker: false };
    public bool CanCompareSelectedVersionPair
        => CompareLeftVersion is { HasContentBlocks: true, IsDeletionMarker: false } left
           && CompareRightVersion is { HasContentBlocks: true, IsDeletionMarker: false } right
           && left.FileVersionId != right.FileVersionId;

    public ObservableCollection<ExplorerTreeNodeViewModel> TreeNodes { get; } = [];
    public ObservableCollection<ExplorerItemViewModel> Items { get; } = [];
    public ObservableCollection<ExplorerFileVersionViewModel> FileVersions { get; } = [];
    public ObservableCollection<RepositoryPendingChangeViewModel> PendingChanges { get; } = [];
    public ObservableCollection<RepositorySnapshotHistoryEntryViewModel> SnapshotHistory { get; } = [];
    public ObservableCollection<RepositorySnapshotFileChangeViewModel> SnapshotFiles { get; } = [];
    public ObservableCollection<DiffPreviewRowViewModel> DiffPreviewRows { get; } = [];

    public RepositoryExplorerViewModel(
        IMediator mediator,
        IWindowService windows,
        ILogger<RepositoryExplorerViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _log = log;
        DiffPreviewRows.CollectionChanged += OnDiffPreviewRowsCollectionChanged;
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
            CompareLeftVersion = null;
            CompareRightVersion = null;
            FileVersions.Clear();
            PendingChanges.Clear();
            SnapshotHistory.Clear();
            SnapshotFiles.Clear();
            SelectedSnapshot = null;
            SelectedSnapshotFile = null;
            HasPendingChanges = false;
            HasSnapshotHistory = false;
            HasSnapshotFiles = false;
            IsSnapshotHistoryLoading = false;
            IsSnapshotFilesLoading = false;
            IsSnapshotHistoryMenuOpen = false;
            IsDiffPreviewMenuOpen = false;
            IsDiffPreviewLoading = false;
            DiffPreviewTitle = string.Empty;
            DiffPreviewSummary = string.Empty;
            DiffPreviewRows.Clear();
            PendingChangesSummary = "Ð˜Ð·Ð¼ÐµÐ½ÐµÐ½Ð¸Ð¹ Ñ Ð¿Ð¾ÑÐ»ÐµÐ´Ð½ÐµÐ³Ð¾ ÑÐ½Ð¸Ð¼ÐºÐ° Ð½ÐµÑ‚.";
            LastSnapshotLabel = "Ð¡Ð½Ð¸Ð¼Ð¾Ðº ÐµÑ‰Ðµ Ð½Ðµ ÑÐ¾Ð·Ð´Ð°Ð½.";

            var repo = await _mediator.Send(new GetRepositoryDetailQuery(repositoryId));
            if (repo is null)
            {
                ErrorMessage = "Ð ÐµÐ¿Ð¾Ð·Ð¸Ñ‚Ð¾Ñ€Ð¸Ð¹ Ð½Ðµ Ð½Ð°Ð¹Ð´ÐµÐ½.";
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
            ErrorMessage = "ÐÐµ ÑƒÐ´Ð°Ð»Ð¾ÑÑŒ Ð·Ð°Ð³Ñ€ÑƒÐ·Ð¸Ñ‚ÑŒ ÑÐ¾Ð´ÐµÑ€Ð¶Ð¸Ð¼Ð¾Ðµ Ñ€ÐµÐ¿Ð¾Ð·Ð¸Ñ‚Ð¾Ñ€Ð¸Ñ.";
            Items.Clear();
            TreeNodes.Clear();
            FileVersions.Clear();
            PendingChanges.Clear();
            SnapshotHistory.Clear();
            SnapshotFiles.Clear();
            SelectedSnapshot = null;
            SelectedSnapshotFile = null;
            HasPendingChanges = false;
            HasSnapshotHistory = false;
            HasSnapshotFiles = false;
            IsSnapshotHistoryLoading = false;
            IsSnapshotFilesLoading = false;
            IsSnapshotHistoryMenuOpen = false;
            IsDiffPreviewMenuOpen = false;
            IsDiffPreviewLoading = false;
            DiffPreviewTitle = string.Empty;
            DiffPreviewSummary = string.Empty;
            DiffPreviewRows.Clear();
            PendingChangesSummary = "ÐÐµ ÑƒÐ´Ð°Ð»Ð¾ÑÑŒ Ð·Ð°Ð³Ñ€ÑƒÐ·Ð¸Ñ‚ÑŒ Ð¸Ð·Ð¼ÐµÐ½ÐµÐ½Ð¸Ñ.";
            LastSnapshotLabel = "Ð¡Ð½Ð¸Ð¼Ð¾Ðº ÐµÑ‰Ðµ Ð½Ðµ ÑÐ¾Ð·Ð´Ð°Ð½.";
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

        if (!_isSyncingSelectionFromSnapshot
            && value is not null
            && SelectedSnapshotFile is not null
            && !value.RelativePath.Equals(SelectedSnapshotFile.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            _preferredSnapshotFileVersionId = null;
            SelectedSnapshotFile = null;
        }

        _ = LoadVersionsForSelectedItemAsync(value);
    }

    partial void OnSelectedSnapshotChanged(RepositorySnapshotHistoryEntryViewModel? value)
    {
        _ = LoadSnapshotFilesForSelectedSnapshotAsync(value?.SnapshotId);
    }

    partial void OnSelectedSnapshotFileChanged(RepositorySnapshotFileChangeViewModel? value)
    {
        if (value is null)
        {
            _preferredSnapshotFileVersionId = null;
            return;
        }

        _preferredSnapshotFileVersionId = value.FileVersionId;
        SelectItemFromSnapshotFile(value);
    }

    partial void OnSelectedVersionChanged(ExplorerFileVersionViewModel? value)
    {
        SelectedVersionHasContentBlocks = value?.HasContentBlocks == true;

        if (value is { HasContentBlocks: true, IsDeletionMarker: false })
            CompareLeftVersion = value;

        OnPropertyChanged(nameof(CanCompareSelectedVersionPair));
    }

    private void SelectItemFromSnapshotFile(RepositorySnapshotFileChangeViewModel value)
    {
        _isSyncingSelectionFromSnapshot = true;

        try
        {
            var existing = Items.FirstOrDefault(i =>
                i.RelativePath.Equals(value.RelativePath, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                SelectedItem = existing;
                return;
            }

            SelectedItem = new ExplorerItemViewModel
            {
                RelativePath = value.RelativePath,
                ParentRelativePath = GetParentRelativePath(value.RelativePath),
                IsDirectory = false,
                Name = value.Name,
                Type = GuessItemType(value.RelativePath),
                SizeDisplay = value.ChangeKind == "deleted" ? "deleted" : FormatSize(value.CurrentSizeBytes),
                ModifiedDisplay = SelectedSnapshot?.DisplayTime ?? string.Empty,
                HashSha256 = null
            };
        }
        finally
        {
            _isSyncingSelectionFromSnapshot = false;
        }
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
            fallbackMessage: "Ð¡Ð¸Ð½Ñ…Ñ€Ð¾Ð½Ð¸Ð·Ð°Ñ†Ð¸Ñ Ð¸Ð½Ð´ÐµÐºÑÐ°...",
            showErrors: true,
            successMessage: "Ð˜Ð½Ð´ÐµÐºÑ ÑÐ¸Ð½Ñ…Ñ€Ð¾Ð½Ð¸Ð·Ð¸Ñ€Ð¾Ð²Ð°Ð½.");
    }

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        await LoadPendingChangesAsync();

        if (!HasPendingChanges)
        {
            ErrorMessage = "No changes detected. Snapshot creation is disabled.";
            return;
        }

        var owner = _windows.GetActiveWindow();
        if (owner is null)
        {
            ErrorMessage = "Unable to open snapshot dialog window.";
            return;
        }

        var dialog = _windows.Create<SnapshotNameDialogWindow>();
        if (dialog.DataContext is SnapshotNameDialogWindowViewModel vm)
        {
            var defaultName = $"snimok_{DateTime.Now:yyyyMMdd_HHmmss}";
            var changedFiles = PendingChanges.Select(change => new SnapshotPendingFileItemViewModel
            {
                RelativePath = change.RelativePath,
                Name = change.Name,
                ChangeKind = change.ChangeKind,
                CurrentSizeBytes = change.CurrentSizeBytes,
                BaselineSizeBytes = change.BaselineSizeBytes
            }).ToList();

            vm.Initialize(defaultName, changedFiles, LoadSnapshotDialogPreviewAsync);
        }

        await _windows.ShowDialogAsync(dialog, owner);

        if (!dialog.IsConfirmed)
            return;

        var snapshotTitle = dialog.SnapshotTitle;

        await ExecuteScanAsync(
            saveFileVersions: true,
            triggerOverride: "manual_snapshot",
            fallbackMessage: "Saving snapshot...",
            showErrors: true,
            successMessage: "Snapshot saved.",
            snapshotTitle: snapshotTitle);
    }

    private async Task<PendingFileDiffPreviewDto> LoadSnapshotDialogPreviewAsync(
        SnapshotPendingFileItemViewModel file,
        CancellationToken ct)
    {
        if (RepositoryId <= 0)
            return PendingFileDiffPreviewDto.Unavailable(file.RelativePath, "Repository is not selected.");

        var result = await _mediator.Send(new GetPendingFileDiffPreviewQuery(
            RepositoryId,
            file.RelativePath,
            3000), ct);

        if (!result.Success || result.Value is null)
            return PendingFileDiffPreviewDto.Unavailable(
                file.RelativePath,
                result.Error ?? "Unable to build preview for selected file.");

        return result.Value;
    }
    [RelayCommand]
    private async Task ShowSnapshotHistoryAsync()
    {
        IsSnapshotHistoryMenuOpen = true;

        if (RepositoryId == 0)
            return;

        if (!IsSnapshotHistoryLoading)
            await LoadSnapshotHistoryAsync(SelectedSnapshot?.SnapshotId ?? 0);

        if (SelectedSnapshot is null && SnapshotHistory.Count > 0)
            SelectedSnapshot = SnapshotHistory[0];
    }

    [RelayCommand]
    private void CloseSnapshotHistoryMenu()
        => IsSnapshotHistoryMenuOpen = false;

    [RelayCommand]
    private async Task ExportRepositoryBundleAsync()
    {
        if (RepositoryId <= 0 || IsMaintenanceRunning)
            return;

        var owner = _windows.GetActiveWindow();
        if (owner is null)
        {
            ErrorMessage = "Unable to open file picker window.";
            return;
        }

        var suggestedName = BuildSuggestedBundleFileName();
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export repository bundle",
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Veyra bundle")
                {
                    Patterns = ["*.veyra.zip", "*.veyra-bundle", "*.zip"]
                }
            ]
        });

        var bundlePath = file?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(bundlePath))
            return;

        await RunMaintenanceOperationAsync(
            actionName: "export",
            startedMessage: "Exporting repository bundle...",
            operation: async () => await _mediator.Send(new ExportRepositoryBundleCommand(RepositoryId, bundlePath)),
            onSuccess: result =>
            {
                VersionActionMessage =
                    $"{result.Summary}\nBundle: {result.BundlePath}\nSize: {FormatSize(result.BundleSizeBytes)}";
            });
    }

    [RelayCommand]
    private async Task ImportRepositoryBundleAsync()
    {
        if (IsMaintenanceRunning)
            return;

        var owner = _windows.GetActiveWindow();
        if (owner is null)
        {
            ErrorMessage = "Unable to open file picker window.";
            return;
        }

        var bundleSelection = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import repository bundle",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Veyra bundle")
                {
                    Patterns = ["*.veyra.zip", "*.veyra-bundle", "*.zip"]
                }
            ]
        });

        var bundlePath = bundleSelection.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(bundlePath))
            return;

        var validation = await _mediator.Send(new ValidateRepositoryBundleQuery(bundlePath));
        if (!validation.IsValid)
        {
            ErrorMessage = validation.Message;
            return;
        }

        var targetDirectorySelection = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select target directory for imported repository",
            AllowMultiple = false
        });

        var targetDirectory = targetDirectorySelection.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;

        await RunMaintenanceOperationAsync(
            actionName: "import",
            startedMessage: "Importing repository bundle...",
            operation: async () => await _mediator.Send(new ImportRepositoryBundleCommand(
                bundlePath,
                targetDirectory,
                RepositoryNameOverride: null)),
            onSuccess: result =>
            {
                var warningText = result.Warnings.Count == 0
                    ? string.Empty
                    : "\nWarnings:\n" + string.Join('\n', result.Warnings);

                VersionActionMessage =
                    $"{result.Summary}\nImported repository id: {result.RepositoryId}{warningText}";
            });
    }

    [RelayCommand]
    private async Task RepairRepositoryDataAsync()
    {
        if (RepositoryId <= 0 || IsMaintenanceRunning)
            return;

        await RunMaintenanceOperationAsync(
            actionName: "repair",
            startedMessage: "Running repository repair...",
            operation: async () => await _mediator.Send(new RepairRepositoryDataCommand(
                RepositoryId,
                RepairMissingBlocksFromCloud: true)),
            onSuccess: result =>
            {
                VersionActionMessage = FormatRecoveryMessage(result);
            });
    }

    [RelayCommand]
    private async Task ReindexRepositoryDataAsync()
    {
        if (RepositoryId <= 0 || IsMaintenanceRunning)
            return;

        await RunMaintenanceOperationAsync(
            actionName: "reindex",
            startedMessage: "Reindexing repository and rebuilding snapshot data...",
            operation: async () => await _mediator.Send(new ReindexRepositoryDataCommand(RepositoryId)),
            onSuccess: result =>
            {
                VersionActionMessage = FormatRecoveryMessage(result);
            });
    }

    [RelayCommand]
    private async Task RelinkRepositoryDataAsync()
    {
        if (RepositoryId <= 0 || IsMaintenanceRunning)
            return;

        await RunMaintenanceOperationAsync(
            actionName: "relink",
            startedMessage: "Relinking snapshot-file graph...",
            operation: async () => await _mediator.Send(new RelinkRepositoryDataCommand(RepositoryId)),
            onSuccess: result =>
            {
                VersionActionMessage = FormatRecoveryMessage(result);
            });
    }

    [RelayCommand]
    private void CloseDiffPreviewMenu()
        => IsDiffPreviewMenuOpen = false;

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
            ErrorMessage = "Ð”Ð»Ñ Ð²Ñ‹Ð±Ñ€Ð°Ð½Ð½Ð¾Ð¹ Ð²ÐµÑ€ÑÐ¸Ð¸ Ð¾Ñ‚ÑÑƒÑ‚ÑÑ‚Ð²ÑƒÑŽÑ‚ Ð±Ð»Ð¾ÐºÐ¸ ÑÐ¾Ð´ÐµÑ€Ð¶Ð¸Ð¼Ð¾Ð³Ð¾.";
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
            ErrorMessage = result.Error ?? "ÐÐµ ÑƒÐ´Ð°Ð»Ð¾ÑÑŒ Ð²Ð¾ÑÑÑ‚Ð°Ð½Ð¾Ð²Ð¸Ñ‚ÑŒ Ñ„Ð°Ð¹Ð».";
            return;
        }

        VersionActionMessage = $"Ð¤Ð°Ð¹Ð» Ð²Ð¾ÑÑÑ‚Ð°Ð½Ð¾Ð²Ð»ÐµÐ½ Ð¿Ð¾Ð²ÐµÑ€Ñ… Ñ‚ÐµÐºÑƒÑ‰ÐµÐ³Ð¾:\n{result.Value}";
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
            ErrorMessage = "Ð”Ð»Ñ Ð²Ñ‹Ð±Ñ€Ð°Ð½Ð½Ð¾Ð¹ Ð²ÐµÑ€ÑÐ¸Ð¸ Ð¾Ñ‚ÑÑƒÑ‚ÑÑ‚Ð²ÑƒÑŽÑ‚ Ð±Ð»Ð¾ÐºÐ¸ ÑÐ¾Ð´ÐµÑ€Ð¶Ð¸Ð¼Ð¾Ð³Ð¾.";
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
            ErrorMessage = result.Error ?? "ÐÐµ ÑƒÐ´Ð°Ð»Ð¾ÑÑŒ Ð²Ð¾ÑÑÑ‚Ð°Ð½Ð¾Ð²Ð¸Ñ‚ÑŒ Ñ„Ð°Ð¹Ð» Ð² Ð½Ð¾Ð²Ñ‹Ð¹ Ð¿ÑƒÑ‚ÑŒ.";
            return;
        }

        VersionActionMessage = $"Ð¤Ð°Ð¹Ð» Ð²Ð¾ÑÑÑ‚Ð°Ð½Ð¾Ð²Ð»ÐµÐ½ Ð² Ð½Ð¾Ð²Ñ‹Ð¹ Ð¿ÑƒÑ‚ÑŒ:\n{result.Value}";
    }

    [RelayCommand]
    private async Task CompareSelectedWithPreviousAsync()
    {
        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
            return;

        if (!selectedVersion.HasContentBlocks || selectedVersion.IsDeletionMarker)
        {
            ErrorMessage = "Diff preview is unavailable for this version.";
            return;
        }

        var ordered = FileVersions.ToList();
        var index = ordered.FindIndex(v => v.FileVersionId == selectedVersion.FileVersionId);
        if (index < 0)
        {
            ErrorMessage = "Unable to locate the selected file version.";
            return;
        }

        var previous = ordered
            .Skip(index + 1)
            .FirstOrDefault(v => v.HasContentBlocks && !v.IsDeletionMarker);

        if (previous is null)
        {
            ErrorMessage = "No previous content version was found for diff.";
            return;
        }

        CompareLeftVersion = selectedVersion;
        CompareRightVersion = previous;

        await ShowDiffPreviewAsync(selectedVersion, previous);
    }


    [RelayCommand]
    private async Task CompareSelectedVersionsAsync()
    {
        if (!CanCompareSelectedVersionPair || CompareLeftVersion is null || CompareRightVersion is null)
        {
            ErrorMessage = "Select two different versions with stored content to compare.";
            return;
        }

        await ShowDiffPreviewAsync(CompareLeftVersion, CompareRightVersion);
    }

    private async Task ShowDiffPreviewAsync(
        ExplorerFileVersionViewModel leftVersion,
        ExplorerFileVersionViewModel rightVersion)
    {
        ErrorMessage = null;
        DiffPreview = null;
        VersionActionMessage = null;

        IsDiffPreviewLoading = true;
        IsDiffPreviewMenuOpen = true;
        DiffPreviewTitle = SelectedItem?.Name ?? string.Empty;
        DiffPreviewSummary = "Building preview...";
        DiffPreviewRows.Clear();

        OperationResult<TextDiffResultDto> diffResult;

        try
        {
            diffResult = await Task.Run(() => _mediator.Send(new GetTextDiffQuery(
                leftVersion.FileVersionId,
                rightVersion.FileVersionId,
                4000)));
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to build diff preview. RepositoryId {RepositoryId}. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                RepositoryId,
                leftVersion.FileVersionId,
                rightVersion.FileVersionId);

            ErrorMessage = "Unable to build diff preview.";
            DiffPreviewSummary = ErrorMessage;
            DiffPreviewRows.Clear();
            IsDiffPreviewLoading = false;
            return;
        }

        IsDiffPreviewLoading = false;

        if (!diffResult.Success || diffResult.Value is null)
        {
            ErrorMessage = diffResult.Error ?? "Unable to build diff preview.";
            DiffPreviewSummary = ErrorMessage;
            DiffPreviewRows.Clear();
            return;
        }

        var value = diffResult.Value;
        DiffPreviewTitle = SelectedItem?.Name ?? value.RelativePath;
        DiffPreviewSummary = $"{value.RelativePath}   +{value.AddedLines} / -{value.RemovedLines}"
                             + (value.IsTruncated ? "  (truncated)" : string.Empty);

        DiffPreviewRows.Clear();
        foreach (var row in BuildDiffPreviewRows(value.Lines, value.Hunks))
            DiffPreviewRows.Add(row);
        VersionActionMessage = $"Diff ready: +{value.AddedLines} / -{value.RemovedLines}";
    }
    private async Task<bool> ExecuteScanAsync(
        bool saveFileVersions,
        string triggerOverride,
        string fallbackMessage,
        bool showErrors,
        string? successMessage,
        string? snapshotTitle = null)
    {
        if (RepositoryId == 0)
            return false;

        if (!await _scanGate.WaitAsync(0))
        {
            if (showErrors)
                ErrorMessage = "Ð¡ÐºÐ°Ð½Ð¸Ñ€Ð¾Ð²Ð°Ð½Ð¸Ðµ ÑƒÐ¶Ðµ Ð²Ñ‹Ð¿Ð¾Ð»Ð½ÑÐµÑ‚ÑÑ.";
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
                    TriggerOverride: triggerOverride,
                    SnapshotTitle: snapshotTitle))));

            if (!result.Success)
            {
                if (showErrors)
                    ErrorMessage = result.Error ?? "Ð¡ÐºÐ°Ð½Ð¸Ñ€Ð¾Ð²Ð°Ð½Ð¸Ðµ Ð·Ð°Ð²ÐµÑ€ÑˆÐ¸Ð»Ð¾ÑÑŒ Ñ Ð¾ÑˆÐ¸Ð±ÐºÐ¾Ð¹.";
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
                ErrorMessage = "ÐÐµ ÑƒÐ´Ð°Ð»Ð¾ÑÑŒ Ð²Ñ‹Ð¿Ð¾Ð»Ð½Ð¸Ñ‚ÑŒ ÑÐºÐ°Ð½Ð¸Ñ€Ð¾Ð²Ð°Ð½Ð¸Ðµ Ñ€ÐµÐ¿Ð¾Ð·Ð¸Ñ‚Ð¾Ñ€Ð¸Ñ.";
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
        CompareLeftVersion = null;
        CompareRightVersion = null;
        DiffPreview = null;
        VersionActionMessage = null;
        IsDiffPreviewMenuOpen = false;
        IsDiffPreviewLoading = false;
        DiffPreviewTitle = string.Empty;
        DiffPreviewSummary = string.Empty;
        DiffPreviewRows.Clear();

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

            if (_preferredSnapshotFileVersionId is > 0)
            {
                SelectedVersion = FileVersions.FirstOrDefault(v => v.FileVersionId == _preferredSnapshotFileVersionId.Value)
                                  ?? FileVersions.FirstOrDefault(v => v.HasContentBlocks && !v.IsDeletionMarker)
                                  ?? FileVersions.FirstOrDefault();
            }
            else
            {
                SelectedVersion = FileVersions.FirstOrDefault(v => v.HasContentBlocks && !v.IsDeletionMarker)
                                  ?? FileVersions.FirstOrDefault();
            }

            InitializeVersionComparePair();
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to load file versions. RepositoryId {RepositoryId}. Path {Path}",
                RepositoryId,
                item.RelativePath);
            ErrorMessage = "ÐÐµ ÑƒÐ´Ð°Ð»Ð¾ÑÑŒ Ð·Ð°Ð³Ñ€ÑƒÐ·Ð¸Ñ‚ÑŒ Ð²ÐµÑ€ÑÐ¸Ð¸ Ñ„Ð°Ð¹Ð»Ð°.";
        }
        finally
        {
            IsVersionLoading = false;
        }
    }


    private void InitializeVersionComparePair()
    {
        var comparable = FileVersions
            .Where(v => v.HasContentBlocks && !v.IsDeletionMarker)
            .ToList();

        if (comparable.Count == 0)
        {
            CompareLeftVersion = null;
            CompareRightVersion = null;
            OnPropertyChanged(nameof(CanCompareSelectedVersionPair));
            return;
        }

        if (SelectedVersion is { HasContentBlocks: true, IsDeletionMarker: false } selected)
        {
            CompareLeftVersion = selected;

            var index = comparable.FindIndex(v => v.FileVersionId == selected.FileVersionId);
            CompareRightVersion = index >= 0
                ? comparable.Skip(index + 1).FirstOrDefault()
                  ?? comparable.FirstOrDefault(v => v.FileVersionId != selected.FileVersionId)
                : comparable.FirstOrDefault(v => v.FileVersionId != selected.FileVersionId);
        }
        else
        {
            CompareLeftVersion = comparable[0];
            CompareRightVersion = comparable.Skip(1).FirstOrDefault();
        }

        OnPropertyChanged(nameof(CanCompareSelectedVersionPair));
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
            LastSnapshotLabel = "Ð¡Ð½Ð¸Ð¼Ð¾Ðº ÐµÑ‰Ðµ Ð½Ðµ ÑÐ¾Ð·Ð´Ð°Ð½.";
            PendingChangesSummary = HasPendingChanges
                ? $"Ðš Ñ„Ð¸ÐºÑÐ°Ñ†Ð¸Ð¸ {PendingChanges.Count} Ñ„Ð°Ð¹Ð»Ð¾Ð²"
                : "Ð¡Ð½Ð¸Ð¼ÐºÐ¾Ð² Ð¿Ð¾ÐºÐ° Ð½ÐµÑ‚. Ð¡Ð¾Ð·Ð´Ð°Ð¹Ñ‚Ðµ Ð¿ÐµÑ€Ð²Ñ‹Ð¹ ÑÐ½Ð¸Ð¼Ð¾Ðº.";
            return;
        }

        var local = pending.BaselineSnapshotAtUtc.Value.ToLocalTime();
        LastSnapshotLabel = $"ÐŸÐ¾ÑÐ»ÐµÐ´Ð½Ð¸Ð¹ ÑÐ½Ð¸Ð¼Ð¾Ðº {local:yyyy-MM-dd HH:mm:ss}";

        PendingChangesSummary = pending.AddedCount + pending.ModifiedCount + pending.DeletedCount == 0
            ? "Ð˜Ð·Ð¼ÐµÐ½ÐµÐ½Ð¸Ð¹ Ñ Ð¿Ð¾ÑÐ»ÐµÐ´Ð½ÐµÐ³Ð¾ ÑÐ½Ð¸Ð¼ÐºÐ° Ð½ÐµÑ‚."
            : $"Ð˜Ð·Ð¼ÐµÐ½ÐµÐ½Ð¸Ð¹ +{pending.AddedCount} ~{pending.ModifiedCount} -{pending.DeletedCount}";
    }

    private async Task LoadSnapshotHistoryAsync(long preferredSnapshotId = 0)
    {
        if (RepositoryId == 0)
            return;

        IsSnapshotHistoryLoading = true;

        try
        {
            var history = await _mediator.Send(new GetRepositorySnapshotHistoryQuery(RepositoryId, 200));

            SnapshotHistory.Clear();
            foreach (var item in history)
            {
                SnapshotHistory.Add(new RepositorySnapshotHistoryEntryViewModel
                {
                    SnapshotId = item.SnapshotId,
                    Title = item.Title,
                    CreatedAtUtc = item.CreatedAtUtc,
                    Trigger = item.Trigger,
                    ChangedFilesCount = item.ChangedFilesCount
                });
            }

            HasSnapshotHistory = SnapshotHistory.Count > 0;

            var selected = preferredSnapshotId > 0
                ? SnapshotHistory.FirstOrDefault(x => x.SnapshotId == preferredSnapshotId)
                : null;

            if (selected is null && SnapshotHistory.Count > 0)
                selected = SnapshotHistory[0];

            SelectedSnapshot = selected;

            if (selected is null)
            {
                SnapshotFiles.Clear();
                SelectedSnapshotFile = null;
                HasSnapshotFiles = false;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load snapshot history for repository {RepositoryId}", RepositoryId);
            SnapshotHistory.Clear();
            SnapshotFiles.Clear();
            SelectedSnapshot = null;
            SelectedSnapshotFile = null;
            HasSnapshotHistory = false;
            HasSnapshotFiles = false;
        }
        finally
        {
            IsSnapshotHistoryLoading = false;
        }
    }

    private async Task LoadSnapshotFilesForSelectedSnapshotAsync(long? snapshotId)
    {
        SnapshotFiles.Clear();
        SelectedSnapshotFile = null;
        HasSnapshotFiles = false;

        if (snapshotId is null || snapshotId <= 0 || RepositoryId == 0)
            return;

        IsSnapshotFilesLoading = true;

        try
        {
            var files = await _mediator.Send(new GetRepositorySnapshotChangedFilesQuery(
                RepositoryId,
                snapshotId.Value,
                2000));

            foreach (var file in files)
            {
                SnapshotFiles.Add(new RepositorySnapshotFileChangeViewModel
                {
                    SnapshotId = file.SnapshotId,
                    FileIdentityId = file.FileIdentityId,
                    FileVersionId = file.FileVersionId,
                    RelativePath = file.RelativePath,
                    Name = file.Name,
                    ChangeKind = file.ChangeKind,
                    CurrentSizeBytes = file.CurrentSizeBytes,
                    PreviousSizeBytes = file.PreviousSizeBytes
                });
            }

            HasSnapshotFiles = SnapshotFiles.Count > 0;

            if (HasSnapshotFiles)
                SelectedSnapshotFile = SnapshotFiles[0];
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Failed to load snapshot files for repository {RepositoryId}, snapshot {SnapshotId}",
                RepositoryId,
                snapshotId);

            SnapshotFiles.Clear();
            SelectedSnapshotFile = null;
            HasSnapshotFiles = false;
        }
        finally
        {
            IsSnapshotFilesLoading = false;
        }
    }

    private async Task RunMaintenanceOperationAsync<TResult>(
        string actionName,
        string startedMessage,
        Func<Task<OperationResult<TResult>>> operation,
        Action<TResult> onSuccess)
    {
        try
        {
            IsMaintenanceRunning = true;
            ErrorMessage = null;
            VersionActionMessage = startedMessage;

            var result = await operation();

            if (!result.Success || result.Value is null)
            {
                ErrorMessage = result.Error ?? $"Repository {actionName} failed.";
                VersionActionMessage = null;
                return;
            }

            onSuccess(result.Value);
            await RefreshEntriesAndTreeAsync(clearSelection: false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Repository {Action} operation failed. RepositoryId {RepositoryId}",
                actionName,
                RepositoryId);
            ErrorMessage = $"Repository {actionName} failed: {ex.Message}";
            VersionActionMessage = null;
        }
        finally
        {
            IsMaintenanceRunning = false;
        }
    }
    private async Task RefreshEntriesAndTreeAsync(bool clearSelection)
    {
        if (RepositoryId == 0)
            return;

        var previousDirectoryPath = clearSelection ? null : _selectedDirectoryPath;
        var previousItemPath = clearSelection ? null : SelectedItem?.RelativePath;
        var previousSnapshotId = clearSelection ? 0 : SelectedSnapshot?.SnapshotId ?? 0;

        _entries = await _mediator.Send(new GetRepositoryLatestEntriesQuery(RepositoryId));
        await LoadPendingChangesAsync();
        await LoadSnapshotHistoryAsync(previousSnapshotId);
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
            var restoredItem = Items.FirstOrDefault(i =>
                i.RelativePath.Equals(previousItemPath, StringComparison.OrdinalIgnoreCase));

            if (restoredItem is not null)
            {
                SelectedItem = restoredItem;
            }
            else if (SelectedSnapshotFile is not null
                     && SelectedSnapshotFile.RelativePath.Equals(previousItemPath, StringComparison.OrdinalIgnoreCase))
            {
                SelectItemFromSnapshotFile(SelectedSnapshotFile);
            }
            else
            {
                SelectedItem = null;
            }
        }
        else if (SelectedSnapshotFile is null)
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
            ScanMessage = "Ð“Ð¾Ñ‚Ð¾Ð²Ð¾";
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
            fallbackMessage: "Ð¡Ð¸Ð½Ñ…Ñ€Ð¾Ð½Ð¸Ð·Ð°Ñ†Ð¸Ñ Ð¸Ð·Ð¼ÐµÐ½ÐµÐ½Ð¸Ð¹...",
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

    private static string? GetParentRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var normalized = relativePath.Replace('\\', '/');
        var idx = normalized.LastIndexOf('/');
        if (idx <= 0)
            return null;

        return normalized[..idx];
    }

    private static string GuessItemType(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        if (string.IsNullOrWhiteSpace(ext))
            return "File";

        return ext.TrimStart('.').ToUpperInvariant();
    }
    private static ExplorerItemViewModel MapToItem(RepositoryScanEntryDto entry)
    {
        var type = entry.IsDirectory
            ? "ÐŸÐ°Ð¿ÐºÐ°"
            : string.IsNullOrWhiteSpace(entry.Extension)
                ? "Ð¤Ð°Ð¹Ð»"
                : entry.Extension.TrimStart('.').ToUpperInvariant();

        return new ExplorerItemViewModel
        {
            RelativePath = entry.RelativePath,
            ParentRelativePath = entry.ParentRelativePath,
            IsDirectory = entry.IsDirectory,
            Name = entry.Name,
            Type = type,
            SizeDisplay = entry.IsDirectory ? "â€”" : FormatSize(entry.SizeBytes),
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

    private void OnDiffPreviewRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasDiffPreviewRows));
        OnPropertyChanged(nameof(HasNoDiffPreviewRows));
    }

    private static IReadOnlyList<DiffPreviewRowViewModel> BuildDiffPreviewRows(
        IReadOnlyList<TextDiffLineDto> lines,
        IReadOnlyList<TextDiffHunkDto> hunks)
    {
        if (lines.Count == 0)
            return [];

        var rows = new List<DiffPreviewRowViewModel>(lines.Count + (hunks.Count * 3));

        if (hunks.Count > 0)
        {
            foreach (var hunk in hunks.OrderBy(h => h.Sequence))
            {
                var start = Math.Clamp(hunk.StartLineSequence, 0, lines.Count - 1);
                var end = Math.Clamp(hunk.EndLineSequence, start, lines.Count - 1);

                rows.Add(DiffPreviewRowViewModel.CreateHunkHeader(
                    FormatHunkRange(hunk.OldStartLine, hunk.OldLineCount),
                    FormatHunkRange(hunk.NewStartLine, hunk.NewLineCount),
                    NormalizeChangeKindLabel(hunk.ChangeKind)));

                AppendHunkRows(lines, start, end, rows);
            }
        }
        else
        {
            rows.Add(DiffPreviewRowViewModel.CreateHunkHeader("(full)", "(full)", "context"));
            AppendHunkRows(lines, 0, lines.Count - 1, rows);
        }

        return rows;
    }

    private static void AppendHunkRows(
        IReadOnlyList<TextDiffLineDto> lines,
        int startInclusive,
        int endInclusive,
        ICollection<DiffPreviewRowViewModel> rows)
    {
        if (startInclusive > endInclusive)
            return;

        var index = startInclusive;
        while (index <= endInclusive)
        {
            var kind = NormalizeDiffKind(lines[index].Kind);

            if (kind == "remove")
            {
                var removed = new List<TextDiffLineDto>();
                while (index <= endInclusive && NormalizeDiffKind(lines[index].Kind) == "remove")
                {
                    removed.Add(lines[index]);
                    index++;
                }

                var added = new List<TextDiffLineDto>();
                var addCursor = index;
                while (addCursor <= endInclusive && NormalizeDiffKind(lines[addCursor].Kind) == "add")
                {
                    added.Add(lines[addCursor]);
                    addCursor++;
                }

                if (added.Count > 0)
                    index = addCursor;

                var pairCount = Math.Max(removed.Count, added.Count);
                for (var i = 0; i < pairCount; i++)
                {
                    var left = i < removed.Count ? removed[i] : null;
                    var right = i < added.Count ? added[i] : null;
                    rows.Add(CreatePairedDiffRow(left, right));
                }

                continue;
            }

            if (kind == "add")
            {
                while (index <= endInclusive && NormalizeDiffKind(lines[index].Kind) == "add")
                {
                    rows.Add(CreatePairedDiffRow(null, lines[index]));
                    index++;
                }

                continue;
            }

            rows.Add(CreatePairedDiffRow(lines[index], lines[index]));
            index++;
        }
    }

    private static DiffPreviewRowViewModel CreatePairedDiffRow(
        TextDiffLineDto? left,
        TextDiffLineDto? right)
    {
        var leftKind = NormalizeDiffKind(left?.Kind);
        var rightKind = NormalizeDiffKind(right?.Kind);

        return new DiffPreviewRowViewModel
        {
            LeftLineNumber = left is null ? string.Empty : FormatLineNumber(left.LeftLineNumber),
            LeftMarker = leftKind switch
            {
                "remove" => "-",
                "equal" => "|",
                _ => " "
            },
            LeftText = left?.Text ?? string.Empty,
            LeftBackground = leftKind switch
            {
                "remove" => "#45202B",
                "equal" => "#173149",
                _ => "#10233A"
            },
            LeftMarkerForeground = leftKind switch
            {
                "remove" => "#FF8FA3",
                "equal" => "#9BB5D1",
                _ => "#94AECB"
            },
            RightLineNumber = right is null ? string.Empty : FormatLineNumber(right.RightLineNumber),
            RightMarker = rightKind switch
            {
                "add" => "+",
                "equal" => "|",
                _ => " "
            },
            RightText = right?.Text ?? string.Empty,
            RightBackground = rightKind switch
            {
                "add" => "#1E4A39",
                "equal" => "#173149",
                _ => "#10233A"
            },
            RightMarkerForeground = rightKind switch
            {
                "add" => "#89FFD0",
                "equal" => "#9BB5D1",
                _ => "#94AECB"
            }
        };
    }

    private static string NormalizeChangeKindLabel(string? kind)
    {
        if (string.Equals(kind, "added", StringComparison.OrdinalIgnoreCase))
            return "added";

        if (string.Equals(kind, "removed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "deleted", StringComparison.OrdinalIgnoreCase))
            return "removed";

        return "modified";
    }

    private static string FormatHunkRange(int startLine, int count)
        => count <= 0
            ? $"{Math.Max(0, startLine)}"
            : $"{Math.Max(0, startLine)},{count}";

    private static string FormatLineNumber(int? lineNumber)
        => lineNumber is int value ? value.ToString("D4") : string.Empty;

    private static string NormalizeDiffKind(string? kind)
    {
        if (string.Equals(kind, "add", StringComparison.OrdinalIgnoreCase))
            return "add";

        if (string.Equals(kind, "remove", StringComparison.OrdinalIgnoreCase))
            return "remove";

        return "equal";
    }

    private static string FormatLastActivity(DateTime utc)
    {
        var delta = DateTime.UtcNow - utc;
        if (delta.TotalSeconds < 60) return "Ð¢Ð¾Ð»ÑŒÐºÐ¾ Ñ‡Ñ‚Ð¾";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} Ð¼Ð¸Ð½ Ð½Ð°Ð·Ð°Ð´";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} Ñ‡ Ð½Ð°Ð·Ð°Ð´";
        return $"{(int)delta.TotalDays} Ð´Ð½ Ð½Ð°Ð·Ð°Ð´";
    }
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Ð‘";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} ÐšÐ‘";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} ÐœÐ‘";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} Ð“Ð‘";
    }

    private string BuildSuggestedBundleFileName()
    {
        var safeName = string.IsNullOrWhiteSpace(RepositoryName)
            ? $"repo_{RepositoryId}"
            : SanitizeFileName(RepositoryName);

        return $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.veyra.zip";
    }

    private static string FormatRecoveryMessage(RepositoryRecoveryResultDto result)
    {
        var extra = result.Messages.Count == 0
            ? string.Empty
            : "\n" + string.Join('\n', result.Messages);

        return $"{result.Summary}{extra}";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var normalized = new string(chars).Trim('_', ' ');

        return string.IsNullOrWhiteSpace(normalized)
            ? "repository"
            : normalized;
    }
}
