using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
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
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Common.Repository;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Execution;
using Veyra.Desktop.Services.Monitoring;
using Veyra.Desktop.Services.Monitoring.Models;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Repositories;
using Veyra.Desktop.Services.Scanning;
using Veyra.Desktop.Services.State;
using Veyra.Desktop.Services.Storage;
using Veyra.Desktop.Services.Sync;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed partial class RepositoryExplorerViewModel : ObservableObject
{
    private const int LiveSyncDebounceMs = 2500;
    private const int LiveSyncMinIntervalMs = 10000;
    private const int LiveSyncBurstDebounceMs = 6000;
    private const int LiveSyncHighPressureDebounceMs = 20000;
    private const int LiveSyncBurstQueueThreshold = 8;
    private const int LiveSyncHighPressureQueueThreshold = 32;
    private const int VisualRefreshDebounceMs = 700;
    private const int CollapsedVisibleFileVersions = 4;
    private const int ExplorerFilterDebounceMs = 100;
    private const int ExplorerItemsInitialWindowSize = 400;
    private const int ExplorerItemsWindowStep = 400;

    private readonly IMediator _mediator;
    private readonly IServiceScopeExecutor _scopeExecutor;
    private readonly IWindowService _windows;
    private readonly ILogger<RepositoryExplorerViewModel> _log;
    private readonly IMonitoringControlService _monitoringControl;
    private readonly IProcessResourceStatusStore _processResourceStatusStore;
    private readonly IRepositoryLiveSyncDeltaBuilder _liveSyncDeltaBuilder;
    private readonly IRepositoryFsEventQueueService _fsEventQueue;
    private readonly IRepositoryExplorerFilterStore _filterStore;
    private readonly IRepositoryLiveSyncStatusStore _liveSyncStatusStore;
    private readonly IRepositoryRelocationDetector _relocationDetector;
    private readonly IRepositoryScanStatusService _scanStatus;
    private readonly LocalizationManager _localization;
    private readonly Dictionary<string, ExplorerTreeNodeViewModel> _nodeByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _liveSyncExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _liveSyncLock = new();

    private IReadOnlyList<RepositoryScanEntryDto> _entries = Array.Empty<RepositoryScanEntryDto>();
    private readonly Dictionary<string, RepositoryScanEntryDto> _directoryByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<RepositoryScanEntryDto>> _directoryChildrenByParent =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RepositorySnapshotHistoryEntryViewModel> _snapshotHistorySource = [];
    private readonly List<RepositorySnapshotFileChangeViewModel> _snapshotFilesSource = [];
    private IReadOnlyList<ExplorerEntryRow> _filteredExplorerEntries = Array.Empty<ExplorerEntryRow>();
    private RepositoryDto? _repositoryDetail;
    private string? _selectedDirectoryPath;
    private FileSystemWatcher? _liveSyncWatcher;
    private Timer? _liveSyncTimer;
    private Timer? _visualRefreshTimer;
    private DateTime _lastLiveSyncUtc = DateTime.MinValue;
    private bool _autoCaptureFileVersions;
    private string _liveSyncRootPath = string.Empty;
    private readonly List<string> _liveSyncTrackedFormats = [];
    private RepositoryFsEventQueueStatus _liveSyncQueueStatus = RepositoryFsEventQueueStatus.Empty;
    private bool _isSyncingSelectionFromSnapshot;
    private bool _isClearingSnapshotFileSelection;
    private bool _suppressSnapshotSelectionLoad;
    private long? _preferredSnapshotId;
    private string? _preferredSnapshotFileRelativePath;
    private long? _preferredSnapshotFileVersionId;
    private CancellationTokenSource? _snapshotFilesLoadCts;
    private long _snapshotFilesLoadRequestId;
    private CancellationTokenSource? _versionsLoadCts;
    private long _versionsLoadRequestId;
    private CancellationTokenSource? _itemsLoadCts;
    private long _itemsLoadRequestId;
    private long? _diffPreviewBeforeVersionId;
    private long? _diffPreviewAfterVersionId;
    private string? _diffPreviewRelativePath;
    private bool _savedFiltersLoaded;
    private bool _suppressExplorerFilterApply;
    private bool _suppressSnapshotFilterApply;
    private int _visibleExplorerItemLimit;
    private DateTime? _pendingBaselineSnapshotAtUtc;
    private int _pendingAddedCount;
    private int _pendingModifiedCount;
    private int _pendingDeletedCount;
    private bool _suppressFileVersionsCollectionChanged;
    private bool _suppressComparableVersionsCollectionChanged;
    private SnapshotHistoryWindow? _snapshotHistoryWindow;
    private RepositoryLiveSyncOperationSnapshot _liveSyncLastOperation = RepositoryLiveSyncOperationSnapshot.None;
    private ProcessResourceSnapshotDto? _lastProcessMetricsSnapshot;
    private TimeSpan? _lastLiveSyncOperationDuration;
    private DateTime _lastLiveSyncOperationCompletedUtc = DateTime.MinValue;
    private TimeSpan? _lastCompletedScanDuration;
    private DateTime _lastCompletedScanUtc = DateTime.MinValue;
    private string _lastCompletedScanOrigin = string.Empty;
    private bool _lastCompletedScanSucceeded;
    private DateTime _lastBackgroundScanRefreshStartedUtc = DateTime.MinValue;
    private bool _suppressExplorerViewModeOptionChange;

    private enum ScanExecutionOutcome
    {
        Completed,
        Deferred,
        Failed
    }

    private enum IncrementalLiveSyncOutcome
    {
        Completed,
        Deferred,
        Failed,
        FallbackToFullScan
    }

    private sealed record ExplorerEntryRow(
        RepositoryScanEntryDto Entry,
        bool IsTracked,
        string StatusKind);

    public event Action? BackRequested;
    public event Func<int, Task>? OpenSettingsRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    [NotifyPropertyChangedFor(nameof(CanRescanRepository))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRunMaintenanceActions))]
    [NotifyPropertyChangedFor(nameof(CanOpenSnapshotHistory))]
    [NotifyPropertyChangedFor(nameof(CanOpenSelectedFileHistory))]
    [NotifyPropertyChangedFor(nameof(CanRelinkRepository))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedSnapshot))]
    [NotifyPropertyChangedFor(nameof(ShowRepositoryUnavailableCard))]
    private int _repositoryId;

    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepositoryPathAvailable))]
    [NotifyPropertyChangedFor(nameof(ShowRepositoryUnavailableCard))]
    [NotifyPropertyChangedFor(nameof(CanRescanRepository))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRelinkRepository))]
    private string _repositoryPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    [NotifyPropertyChangedFor(nameof(CanRescanRepository))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRunMaintenanceActions))]
    [NotifyPropertyChangedFor(nameof(CanOpenSnapshotHistory))]
    [NotifyPropertyChangedFor(nameof(CanRelinkRepository))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedSnapshot))]
    [NotifyPropertyChangedFor(nameof(ShowRepositoryUnavailableCard))]
    [NotifyPropertyChangedFor(nameof(ShowBlockingOverlay))]
    [NotifyPropertyChangedFor(nameof(BlockingOverlayTitle))]
    [NotifyPropertyChangedFor(nameof(BlockingOverlayDetail))]
    private bool _isLoading;

    [ObservableProperty] private bool _isEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedEntryTypeFilter = "all";
    [ObservableProperty] private string _selectedExtensionFilter = "all";
    [ObservableProperty] private string _selectedModifiedWindowFilter = "all";
    [ObservableProperty] private string _minSizeMb = string.Empty;
    [ObservableProperty] private string _maxSizeMb = string.Empty;
    [ObservableProperty] private bool _isExplorerFiltersVisible;
    [ObservableProperty] private bool _showAllRepositoryFiles;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplorerListView))]
    [NotifyPropertyChangedFor(nameof(IsExplorerGridView))]
    [NotifyPropertyChangedFor(nameof(ExplorerViewModeIcon))]
    private bool _isExplorerGridViewMode;
    [ObservableProperty] private ExplorerViewModeOptionViewModel? _selectedExplorerViewModeOption;
    [ObservableProperty] private string _savedExplorerFilterName = string.Empty;
    [ObservableProperty] private RepositoryExplorerSavedFilterViewModel? _selectedExplorerSavedFilter;
    [ObservableProperty] private bool _hasExplorerSavedFilters;
    [ObservableProperty] private ExplorerTreeNodeViewModel? _selectedTreeNode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenSelectedFileHistory))]
    private ExplorerItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedItem))]
    private bool _hasSelectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedVersion))]
    [NotifyPropertyChangedFor(nameof(CanRunDiffForSelectedVersion))]
    [NotifyPropertyChangedFor(nameof(CanCompareSelectedVersionPair))]
    [NotifyPropertyChangedFor(nameof(HasSelectedVersion))]
    private ExplorerFileVersionViewModel? _selectedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompareSelectedVersionPair))]
    [NotifyPropertyChangedFor(nameof(CanSwapCompareVersions))]
    [NotifyCanExecuteChangedFor(nameof(SwapCompareVersionsCommand))]
    private ExplorerFileVersionViewModel? _compareLeftVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompareSelectedVersionPair))]
    [NotifyPropertyChangedFor(nameof(CanSwapCompareVersions))]
    [NotifyCanExecuteChangedFor(nameof(SwapCompareVersionsCommand))]
    private ExplorerFileVersionViewModel? _compareRightVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedVersionHasNoContentBlocks))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedVersion))]
    [NotifyPropertyChangedFor(nameof(CanRunDiffForSelectedVersion))]
    private bool _selectedVersionHasContentBlocks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFileVersions))]
    [NotifyPropertyChangedFor(nameof(HasVersionPanelError))]
    private bool _isVersionLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionPanelError))]
    [NotifyPropertyChangedFor(nameof(HasNoFileVersions))]
    private string? _versionPanelError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedVersion))]
    [NotifyPropertyChangedFor(nameof(CanRunDiffForSelectedVersion))]
    [NotifyPropertyChangedFor(nameof(CanCompareSelectedVersionPair))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedSnapshot))]
    [NotifyPropertyChangedFor(nameof(VersionActionProgressTitle))]
    [NotifyPropertyChangedFor(nameof(VersionActionProgressDetail))]
    private bool _isVersionActionRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionActionMessage))]
    [NotifyPropertyChangedFor(nameof(VersionActionProgressDetail))]
    private string? _versionActionMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionActionProgressTitle))]
    private bool _isRestoreOverwriteMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffPreview))]
    private string? _diffPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    [NotifyPropertyChangedFor(nameof(CanRescanRepository))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRunMaintenanceActions))]
    [NotifyPropertyChangedFor(nameof(CanRelinkRepository))]
    private bool _isScanRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    [NotifyPropertyChangedFor(nameof(CanRescanRepository))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRunMaintenanceActions))]
    [NotifyPropertyChangedFor(nameof(CanRelinkRepository))]
    [NotifyPropertyChangedFor(nameof(MaintenanceProgressTitle))]
    [NotifyPropertyChangedFor(nameof(MaintenanceProgressDetail))]
    private bool _isMaintenanceRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaintenanceProgressTitle))]
    private string _maintenanceActionName = string.Empty;

    [ObservableProperty] private int _scanPercent;
    [ObservableProperty] private string? _scanMessage;
    [ObservableProperty] private string _scanProgressDetail = string.Empty;
    [ObservableProperty] private bool _scanIsIndeterminate;
    [ObservableProperty] private bool _isLiveSyncActive;
    [ObservableProperty] private bool _isLiveSyncPaused;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveSyncIssue))]
    private string _liveSyncLastIssue = string.Empty;

    [ObservableProperty] private string _pendingChangesSummary = "";
    [ObservableProperty] private string _lastSnapshotLabel = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepositoryRelinkStatusMessage))]
    private string _repositoryRelinkStatusMessage = string.Empty;

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
    [NotifyPropertyChangedFor(nameof(HasSnapshotHistorySearchQuery))]
    private string _snapshotHistorySearchQuery = string.Empty;
    [ObservableProperty] private string _selectedSnapshotKindFilter = "all";
    [ObservableProperty] private string _selectedSnapshotTriggerFilter = "all";
    [ObservableProperty] private string _selectedSnapshotTagFilter = "all";
    [ObservableProperty] private string _snapshotHistoryMinChangedFiles = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedSnapshot))]
    [NotifyPropertyChangedFor(nameof(HasNoSnapshotFiles))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedSnapshot))]
    private RepositorySnapshotHistoryEntryViewModel? _selectedSnapshot;

    [ObservableProperty]
    private RepositorySnapshotFileChangeViewModel? _selectedSnapshotFile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshotFilesSearchQuery))]
    private string _snapshotFilesSearchQuery = string.Empty;
    [ObservableProperty] private string _selectedSnapshotFileChangeKindFilter = "all";
    [ObservableProperty] private string _selectedSnapshotFileExtensionFilter = "all";
    [ObservableProperty] private string _snapshotFilesMinSizeDeltaKb = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSnapshotFiles))]
    private bool _hasSnapshotFiles;

    [ObservableProperty] private bool _isSnapshotHistoryMenuOpen;
    [ObservableProperty] private bool _isDiffPreviewMenuOpen;
    [ObservableProperty] private bool _isFileHistoryMenuOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBlockingOverlay))]
    [NotifyPropertyChangedFor(nameof(BlockingOverlayTitle))]
    [NotifyPropertyChangedFor(nameof(BlockingOverlayDetail))]
    [NotifyPropertyChangedFor(nameof(CanRunScanActions))]
    [NotifyPropertyChangedFor(nameof(CanRescanRepository))]
    [NotifyPropertyChangedFor(nameof(CanCreateSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanRunMaintenanceActions))]
    [NotifyPropertyChangedFor(nameof(CanOpenSnapshotHistory))]
    [NotifyPropertyChangedFor(nameof(CanRelinkRepository))]
    [NotifyPropertyChangedFor(nameof(CanToggleLiveSyncMonitoring))]
    [NotifyPropertyChangedFor(nameof(CanProcessLiveSyncQueueNow))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedVersion))]
    [NotifyPropertyChangedFor(nameof(CanRunDiffForSelectedVersion))]
    [NotifyPropertyChangedFor(nameof(CanCompareSelectedVersionPair))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedSnapshot))]
    [NotifyPropertyChangedFor(nameof(CanOpenSelectedFileHistory))]
    private bool _isTransientActionBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockingOverlayTitle))]
    private string _transientActionTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockingOverlayDetail))]
    private string _transientActionDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDiffPreviewRows))]
    [NotifyPropertyChangedFor(nameof(CanToggleFullFilePreview))]
    private bool _isDiffPreviewLoading;
    [ObservableProperty] private string _diffPreviewTitle = string.Empty;
    [ObservableProperty] private string _diffPreviewSummary = string.Empty;
    [ObservableProperty] private string _comparePairSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPreviewToggleLabel))]
    [NotifyPropertyChangedFor(nameof(ShowDiffRowsPanel))]
    [NotifyPropertyChangedFor(nameof(ShowNoDiffPreviewMessage))]
    [NotifyPropertyChangedFor(nameof(ShowFullFilePreviewPanel))]
    private bool _isFullFilePreviewMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFullFilePreviewMessage))]
    private bool _isFullFilePreviewLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFullFilePreviewContent))]
    [NotifyPropertyChangedFor(nameof(ShowNoFullFilePreviewMessage))]
    private string _fullPreviewBeforeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFullFilePreviewContent))]
    [NotifyPropertyChangedFor(nameof(ShowNoFullFilePreviewMessage))]
    private string _fullPreviewAfterText = string.Empty;

    [ObservableProperty] private string _fullPreviewSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleFileVersionsView))]
    [NotifyPropertyChangedFor(nameof(FileVersionsToggleLabel))]
    private bool _showAllFileVersions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreExplorerItems))]
    [NotifyPropertyChangedFor(nameof(ExplorerResultsSummary))]
    [NotifyPropertyChangedFor(nameof(ExplorerLoadMoreLabel))]
    [NotifyPropertyChangedFor(nameof(ExplorerWindowHintText))]
    private int _filteredExplorerItemCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreExplorerItems))]
    [NotifyPropertyChangedFor(nameof(ExplorerResultsSummary))]
    [NotifyPropertyChangedFor(nameof(ExplorerLoadMoreLabel))]
    [NotifyPropertyChangedFor(nameof(ExplorerWindowHintText))]
    private int _visibleExplorerItemCount;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasNoSelectedItem => !HasSelectedItem;
    public bool HasSelectedVersion => SelectedVersion is not null;
    public bool HasVersionActionMessage => !string.IsNullOrWhiteSpace(VersionActionMessage);
    public bool HasVersionPanelError => !string.IsNullOrWhiteSpace(VersionPanelError);
    public bool HasNoFileVersions => !IsVersionLoading && !HasVersionPanelError && FileVersions.Count == 0;
    public bool HasDiffPreview => !string.IsNullOrWhiteSpace(DiffPreview);
    public string VersionActionProgressTitle => IsRestoreOverwriteMode
        ? Loc.T("explorer.version_restore_running_overwrite_title")
        : Loc.T("explorer.version_restore_running_copy_title");
    public string VersionActionProgressDetail => !string.IsNullOrWhiteSpace(VersionActionMessage)
        ? VersionActionMessage!
        : Loc.T("explorer.version_restore_running_detail");
    public bool SelectedVersionHasNoContentBlocks => SelectedVersion is not null && !SelectedVersionHasContentBlocks;
    public bool HasNoPendingChanges => !HasPendingChanges;
    public bool HasNoSnapshotHistory => !HasSnapshotHistory;
    public bool HasNoSelectedSnapshot => SelectedSnapshot is null;
    public bool HasNoSnapshotFiles => SelectedSnapshot is not null && !HasSnapshotFiles;
    public bool HasDiffPreviewRows => DiffPreviewRows.Count > 0;
    public bool HasNoDiffPreviewRows => !IsDiffPreviewLoading && !HasDiffPreviewRows;
    public bool HasComparableVersions => ComparableFileVersions.Count > 0;
    public bool HasFullFilePreviewContent
        => !string.IsNullOrWhiteSpace(FullPreviewBeforeText) || !string.IsNullOrWhiteSpace(FullPreviewAfterText);
    public bool ShowBlockingOverlay => IsLoading || IsTransientActionBusy;
    public string BlockingOverlayTitle => IsTransientActionBusy
        ? TransientActionTitle
        : Loc.T("explorer.loading_title");
    public string BlockingOverlayDetail => IsTransientActionBusy
        ? TransientActionDetail
        : Loc.T("explorer.loading_detail");
    public bool ShowLiveSyncMonitorCard => RepositoryId > 0;
    public bool HasLiveSyncIssue => !string.IsNullOrWhiteSpace(LiveSyncLastIssue);
    public bool HasLiveSyncProcessLoad => _lastProcessMetricsSnapshot is not null;
    public bool HasLiveSyncProcessHistory => _processResourceStatusStore.History.Count > 0;
    public bool HasLiveSyncOperationCost
        => _lastLiveSyncOperationDuration is not null
           && _liveSyncLastOperation.Kind != RepositoryLiveSyncOperationKind.None;
    public bool HasLiveSyncScanCost => _lastCompletedScanDuration is not null;
    public bool HasLiveSyncDiagnostics => HasLiveSyncOperationCost || HasLiveSyncScanCost;
    public string LiveSyncLastResultText
    {
        get
        {
            var operation = _liveSyncLastOperation;
            return operation.Kind switch
            {
                RepositoryLiveSyncOperationKind.IncrementalApplied when operation.SaveFileVersions
                    => Loc.F(
                        "explorer.monitoring_last_result_incremental_versions",
                        operation.PathCount,
                        operation.UpdatedCount,
                        operation.RemovedCount),
                RepositoryLiveSyncOperationKind.IncrementalApplied
                    => Loc.F(
                        "explorer.monitoring_last_result_incremental_index",
                        operation.PathCount,
                        operation.UpdatedCount,
                        operation.RemovedCount),
                RepositoryLiveSyncOperationKind.IncrementalNoChanges when operation.SaveFileVersions
                    => Loc.F("explorer.monitoring_last_result_incremental_versions_no_changes", operation.PathCount),
                RepositoryLiveSyncOperationKind.IncrementalNoChanges
                    => Loc.F("explorer.monitoring_last_result_incremental_index_no_changes", operation.PathCount),
                RepositoryLiveSyncOperationKind.IncrementalFailed when operation.SaveFileVersions
                    => Loc.F("explorer.monitoring_last_result_incremental_versions_failed", operation.PathCount),
                RepositoryLiveSyncOperationKind.IncrementalFailed
                    => Loc.F("explorer.monitoring_last_result_incremental_index_failed", operation.PathCount),
                RepositoryLiveSyncOperationKind.FallbackRequested
                    => Loc.F(
                        "explorer.monitoring_last_result_fallback_started",
                        RepositoryLiveSyncStatusPresenter.LocalizeFallbackReason(operation.Reason),
                        operation.PathCount),
                RepositoryLiveSyncOperationKind.FallbackCompleted
                    => Loc.F(
                        "explorer.monitoring_last_result_fallback_completed",
                        RepositoryLiveSyncStatusPresenter.LocalizeFallbackReason(operation.Reason),
                        operation.PathCount),
                RepositoryLiveSyncOperationKind.FallbackFailed
                    => Loc.F(
                        "explorer.monitoring_last_result_fallback_failed",
                        RepositoryLiveSyncStatusPresenter.LocalizeFallbackReason(operation.Reason),
                        operation.PathCount),
                _ => Loc.T("explorer.monitoring_last_result_none")
            };
        }
    }
    public string LiveSyncOperationCostText
    {
        get
        {
            if (!HasLiveSyncOperationCost || _lastLiveSyncOperationDuration is null)
                return string.Empty;

            var durationText = FormatMonitoringDuration(_lastLiveSyncOperationDuration.Value);
            var completedAtText = _lastLiveSyncOperationCompletedUtc == DateTime.MinValue
                ? string.Empty
                : _lastLiveSyncOperationCompletedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return _liveSyncLastOperation.PathCount > 0
                ? Loc.F(
                    "explorer.monitoring_cost_last_operation_with_batch",
                    durationText,
                    _liveSyncLastOperation.PathCount,
                    completedAtText)
                : Loc.F("explorer.monitoring_cost_last_operation", durationText, completedAtText);
        }
    }

    public string LiveSyncProcessLoadText
    {
        get
        {
            if (_lastProcessMetricsSnapshot is not { } snapshot)
                return string.Empty;

            return snapshot.CpuPercent.HasValue
                ? Loc.F(
                    "explorer.monitoring_process_summary",
                    snapshot.CpuPercent.Value.ToString("0.#", CultureInfo.CurrentCulture),
                    FormatSize(snapshot.WorkingSetBytes))
                : Loc.F("explorer.monitoring_process_summary_cpu_pending", FormatSize(snapshot.WorkingSetBytes));
        }
    }

    public string LiveSyncProcessLoadDetailText
    {
        get
        {
            if (_lastProcessMetricsSnapshot is not { } snapshot)
                return string.Empty;

            return Loc.F(
                "explorer.monitoring_process_detail",
                FormatSize(snapshot.PrivateMemoryBytes),
                FormatSize(snapshot.ManagedHeapBytes),
                snapshot.ThreadCount,
                snapshot.HandleCount,
                snapshot.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        }
    }

    public string LiveSyncProcessPeakText
        => ProcessResourceStatusPresenter.FormatPeakSummary(_processResourceStatusStore.History);

    public string LiveSyncProcessHistoryText
        => ProcessResourceStatusPresenter.FormatRecentHistory(_processResourceStatusStore.History);

    public string LiveSyncScanCostText
    {
        get
        {
            if (!HasLiveSyncScanCost || _lastCompletedScanDuration is null)
                return string.Empty;

            return Loc.F(
                "explorer.monitoring_cost_last_scan",
                LocalizeScanDiagnosticsOrigin(_lastCompletedScanOrigin),
                FormatMonitoringDuration(_lastCompletedScanDuration.Value),
                _lastCompletedScanSucceeded
                    ? Loc.T("explorer.monitoring_scan_status_success")
                    : Loc.T("explorer.monitoring_scan_status_failed"),
                _lastCompletedScanUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        }
    }
    public bool IsRepositoryPathAvailable => !string.IsNullOrWhiteSpace(RepositoryPath) && Directory.Exists(RepositoryPath);
    public bool ShowRepositoryUnavailableCard => RepositoryId > 0 && !IsRepositoryPathAvailable;
    public bool HasRepositoryRelinkStatusMessage => !string.IsNullOrWhiteSpace(RepositoryRelinkStatusMessage);
    public bool ShowDiffRowsPanel => !IsFullFilePreviewMode && HasDiffPreviewRows;
    public bool ShowNoDiffPreviewMessage => !IsFullFilePreviewMode && HasNoDiffPreviewRows;
    public bool ShowFullFilePreviewPanel => IsFullFilePreviewMode;
    public bool ShowNoFullFilePreviewMessage => IsFullFilePreviewMode && !IsFullFilePreviewLoading && !HasFullFilePreviewContent;
    public string MaintenanceProgressTitle => string.IsNullOrWhiteSpace(MaintenanceActionName)
        ? Loc.T("explorer.maintenance.running_title_generic")
        : Loc.F("explorer.maintenance.running_title", GetMaintenanceActionDisplayName(MaintenanceActionName));
    public string MaintenanceProgressDetail => !string.IsNullOrWhiteSpace(VersionActionMessage)
        ? VersionActionMessage!
        : Loc.T("explorer.maintenance.running_detail");
    public bool HasActiveExplorerFilters => GetActiveExplorerFilterCount() > 0;
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool IsExplorerListView => !IsExplorerGridViewMode;
    public bool IsExplorerGridView => IsExplorerGridViewMode;
    public string ExplorerViewModeIcon => IsExplorerGridViewMode ? "\uECA5" : "\uE8FD";
    public bool HasSnapshotHistorySearchQuery => !string.IsNullOrWhiteSpace(SnapshotHistorySearchQuery);
    public bool HasSnapshotFilesSearchQuery => !string.IsNullOrWhiteSpace(SnapshotFilesSearchQuery);
    public string ExplorerFilterButtonLabel => HasActiveExplorerFilters
        ? Loc.F("explorer.filters_active_button", GetActiveExplorerFilterCount())
        : Loc.T("explorer.filters_button");
    public bool HasMoreExplorerItems => VisibleExplorerItemCount < FilteredExplorerItemCount;
    public string ExplorerResultsSummary => IsEmpty
        ? Loc.T("explorer.results_empty")
        : HasMoreExplorerItems
            ? Loc.F("explorer.results_summary_windowed", VisibleExplorerItemCount, FilteredExplorerItemCount)
            : Loc.F("explorer.results_summary", VisibleExplorerItemCount);
    public string ExplorerLoadMoreLabel
    {
        get
        {
            var remaining = Math.Max(FilteredExplorerItemCount - VisibleExplorerItemCount, 0);
            return Loc.F("explorer.load_more_items", Math.Min(ExplorerItemsWindowStep, remaining));
        }
    }
    public string ExplorerWindowHintText
        => HasMoreExplorerItems
            ? Loc.F("explorer.windowed_items_hint", Math.Max(FilteredExplorerItemCount - VisibleExplorerItemCount, 0))
            : string.Empty;
    public string FullPreviewToggleLabel => IsFullFilePreviewMode
        ? Loc.T("compare.full_preview.show_changes_only")
        : Loc.T("compare.full_preview.view_full_file");
    public bool CanToggleFullFilePreview => !IsDiffPreviewLoading && _diffPreviewBeforeVersionId is > 0 && _diffPreviewAfterVersionId is > 0;
    public bool CanOpenSelectedFileOnDisk => GetSelectedFileFullPath() is not null;
    public bool CanOpenSelectedFileHistory
        => SelectedItem is { IsDirectory: false, IsTracked: true } && RepositoryId > 0 && !IsLoading && !IsTransientActionBusy;
    public bool CanRunScanActions => RepositoryId > 0 && !IsLoading && !IsScanRunning && !IsMaintenanceRunning && !IsTransientActionBusy;
    public bool CanRescanRepository => CanRunScanActions && IsRepositoryPathAvailable;
    public bool CanRunMaintenanceActions => RepositoryId > 0 && !IsLoading && !IsScanRunning && !IsMaintenanceRunning && !IsTransientActionBusy;
    public bool CanCreateSnapshot => CanRescanRepository && HasPendingChanges;
    public bool CanOpenSnapshotHistory => RepositoryId > 0 && !IsLoading && !IsTransientActionBusy;
    public bool CanRestoreSelectedSnapshot
        => SelectedSnapshot is not null && RepositoryId > 0 && !IsLoading && !IsTransientActionBusy && !IsVersionActionRunning;
    public bool CanRelinkRepository => RepositoryId > 0 && !IsLoading && !IsScanRunning && !IsMaintenanceRunning && !IsTransientActionBusy;
    public bool CanToggleLiveSyncMonitoring => RepositoryId > 0
                                                && !IsLoading
                                                && !IsTransientActionBusy
                                                && IsRepositoryPathAvailable
                                                && !string.IsNullOrWhiteSpace(_liveSyncRootPath);
    public bool CanProcessLiveSyncQueueNow => RepositoryId > 0 && !IsLoading && !IsScanRunning
        && !IsTransientActionBusy
        && (!string.IsNullOrWhiteSpace(_liveSyncRootPath) || _liveSyncQueueStatus.TotalCount > 0);
    public bool CanRestoreSelectedVersion
        => !IsTransientActionBusy && !IsVersionActionRunning && SelectedVersion is { HasContentBlocks: true, IsDeletionMarker: false };
    public bool CanRunDiffForSelectedVersion
        => !IsTransientActionBusy && !IsVersionActionRunning && SelectedVersion is { HasContentBlocks: true, IsDeletionMarker: false };
    public bool CanCompareSelectedVersionPair
        => !IsTransientActionBusy
           && !IsVersionActionRunning
           && CompareLeftVersion is { HasContentBlocks: true, IsDeletionMarker: false } left
           && CompareRightVersion is { HasContentBlocks: true, IsDeletionMarker: false } right
           && left.FileVersionId != right.FileVersionId;

    public bool CanSwapCompareVersions
        => CompareLeftVersion is not null
           && CompareRightVersion is not null
           && CompareLeftVersion.FileVersionId != CompareRightVersion.FileVersionId;

    public bool CanToggleFileVersionsView => FileVersions.Count > CollapsedVisibleFileVersions;

    public string FileVersionsToggleLabel => ShowAllFileVersions
        ? Loc.F("explorer.file_versions.show_latest", CollapsedVisibleFileVersions)
        : Loc.F("explorer.file_versions.show_all", FileVersions.Count);
    public string LiveSyncStateText
    {
        get
        {
            if (IsLiveSyncPaused)
                return Loc.T("explorer.monitoring_state_paused");

            if (IsLiveSyncActive)
                return Loc.T("explorer.monitoring_state_active");

            return string.IsNullOrWhiteSpace(_liveSyncRootPath) || Directory.Exists(_liveSyncRootPath)
                ? Loc.T("explorer.monitoring_state_stopped")
                : Loc.T("explorer.monitoring_state_unavailable");
        }
    }

    public string LiveSyncModeText => _autoCaptureFileVersions
        ? Loc.T("explorer.monitoring_mode_versions")
        : Loc.T("explorer.monitoring_mode_index_only");

    public string LiveSyncQueueText => _liveSyncQueueStatus.TotalCount <= 0
        ? Loc.T("explorer.monitoring_queue_idle")
        : Loc.F(
            "explorer.monitoring_queue_counts",
            _liveSyncQueueStatus.PendingCount,
            _liveSyncQueueStatus.RunningCount,
            _liveSyncQueueStatus.TotalCount,
            _liveSyncQueueStatus.MaxRetryCount);

    public string LiveSyncLastActivityText => _lastLiveSyncUtc == DateTime.MinValue
        ? Loc.T("explorer.monitoring_last_processed_never")
        : Loc.F("explorer.monitoring_last_processed_format", _lastLiveSyncUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

    public string LiveSyncTrackedFormatsText => _liveSyncExtensions.Count == 0
        ? Loc.T("explorer.monitoring_formats_all")
        : Loc.F("explorer.monitoring_formats_count", _liveSyncExtensions.Count);

    public string LiveSyncToggleLabel => IsLiveSyncPaused || !IsLiveSyncActive
        ? Loc.T("explorer.monitoring_resume")
        : Loc.T("explorer.monitoring_pause");

    public ObservableCollection<ExplorerTreeNodeViewModel> TreeNodes { get; } = [];
    public ObservableCollection<ExplorerItemViewModel> Items { get; } = [];
    public ObservableCollection<string> EntryTypeFilters { get; } = ["all", "files", "folders"];
    public ObservableCollection<string> ExtensionFilters { get; } = ["all"];
    public ObservableCollection<string> ModifiedWindowFilters { get; } = ["all", "24h", "7d", "30d"];
    public ObservableCollection<RepositoryExplorerSavedFilterViewModel> SavedExplorerFilters { get; } = [];
    public ObservableCollection<string> SnapshotKindFilters { get; } = ["all"];
    public ObservableCollection<string> SnapshotTriggerFilters { get; } = ["all"];
    public ObservableCollection<string> SnapshotTagFilters { get; } = ["all"];
    public ObservableCollection<string> SnapshotFileChangeKindFilters { get; } = ["all", "added", "modified", "removed"];
    public ObservableCollection<string> SnapshotFileExtensionFilters { get; } = ["all"];
    public ObservableCollection<ExplorerFileVersionViewModel> FileVersions { get; } = [];
    public ObservableCollection<ExplorerFileVersionViewModel> VisibleFileVersions { get; } = [];
    public ObservableCollection<ExplorerFileVersionViewModel> ComparableFileVersions { get; } = [];
    public ObservableCollection<RepositoryPendingChangeViewModel> PendingChanges { get; } = [];
    public ObservableCollection<RepositorySnapshotHistoryEntryViewModel> SnapshotHistory { get; } = [];
    public ObservableCollection<RepositorySnapshotFileChangeViewModel> SnapshotFiles { get; } = [];
    public ObservableCollection<DiffPreviewRowViewModel> DiffPreviewRows { get; } = [];
    public ObservableCollection<ExplorerViewModeOptionViewModel> ExplorerViewModeOptions { get; } = [];

    public RepositoryExplorerViewModel(
        IMediator mediator,
        IServiceScopeExecutor scopeExecutor,
        IWindowService windows,
        ILogger<RepositoryExplorerViewModel> log,
        IMonitoringControlService monitoringControl,
        IProcessResourceStatusStore processResourceStatusStore,
        IRepositoryLiveSyncDeltaBuilder liveSyncDeltaBuilder,
        IRepositoryFsEventQueueService fsEventQueue,
        IRepositoryExplorerFilterStore filterStore,
        IRepositoryLiveSyncStatusStore liveSyncStatusStore,
        IRepositoryRelocationDetector relocationDetector,
        IRepositoryScanStatusService scanStatus)
    {
        _mediator = mediator;
        _scopeExecutor = scopeExecutor;
        _windows = windows;
        _log = log;
        _monitoringControl = monitoringControl;
        _processResourceStatusStore = processResourceStatusStore;
        _liveSyncDeltaBuilder = liveSyncDeltaBuilder;
        _fsEventQueue = fsEventQueue;
        _filterStore = filterStore;
        _liveSyncStatusStore = liveSyncStatusStore;
        _relocationDetector = relocationDetector;
        _scanStatus = scanStatus;
        _localization = LocalizationManager.Instance;
        FileVersions.CollectionChanged += OnFileVersionsCollectionChanged;
        ComparableFileVersions.CollectionChanged += OnComparableVersionsCollectionChanged;
        DiffPreviewRows.CollectionChanged += OnDiffPreviewRowsCollectionChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _monitoringControl.StateChanged += OnMonitoringStateChanged;
        _processResourceStatusStore.StatusChanged += OnProcessResourceStatusChanged;
        _scanStatus.StatusChanged += OnRepositoryScanStatusChanged;
        _lastProcessMetricsSnapshot = _processResourceStatusStore.Snapshot;
        RefreshExplorerViewModeOptions();
        RefreshPendingChangesSummary();
    }

    public async Task LoadAsync(int repositoryId)
    {
        try
        {
            StopLiveSync();
            CancelPanelLoadRequests();
            IsLoading = true;
            ErrorMessage = null;
            ResetLiveSyncLastOperation();
            ResetLiveSyncDiagnostics();
            ResetProcessMetrics();
            _log.LogInformation("Loading repository explorer. RepositoryId {RepositoryId}", repositoryId);
            await Task.Yield();

            if (!_savedFiltersLoaded)
            {
                await LoadSavedExplorerFiltersAsync();
                _savedFiltersLoaded = true;
            }

            VersionPanelError = null;
            IsVersionActionRunning = false;
            VersionActionMessage = null;
            DiffPreview = null;
            SelectedItem = null;
            SelectedVersion = null;
            CompareLeftVersion = null;
            CompareRightVersion = null;
            FileVersions.Clear();
            VisibleFileVersions.Clear();
            ComparableFileVersions.Clear();
            ShowAllFileVersions = false;
            PendingChanges.Clear();
            _snapshotHistorySource.Clear();
            SnapshotHistory.Clear();
            _snapshotFilesSource.Clear();
            SnapshotFiles.Clear();
            SelectedSnapshot = null;
            SelectedSnapshotFile = null;
            _preferredSnapshotId = null;
            _preferredSnapshotFileRelativePath = null;
            _preferredSnapshotFileVersionId = null;
            HasPendingChanges = false;
            HasSnapshotHistory = false;
            HasSnapshotFiles = false;
            IsSnapshotHistoryLoading = false;
            IsSnapshotFilesLoading = false;
            CloseSnapshotHistoryWindow();
            IsSnapshotHistoryMenuOpen = false;
            IsDiffPreviewMenuOpen = false;
            IsFileHistoryMenuOpen = false;
            IsDiffPreviewLoading = false;
            DiffPreviewTitle = string.Empty;
            DiffPreviewSummary = string.Empty;
            DiffPreviewRows.Clear();
            _diffPreviewBeforeVersionId = null;
            _diffPreviewAfterVersionId = null;
            _diffPreviewRelativePath = null;
            ResetFullFilePreviewState();
            OnPropertyChanged(nameof(CanToggleFullFilePreview));
            OnPropertyChanged(nameof(CanOpenSelectedFileOnDisk));
            _pendingBaselineSnapshotAtUtc = null;
            _pendingAddedCount = 0;
            _pendingModifiedCount = 0;
            _pendingDeletedCount = 0;
            _filteredExplorerEntries = Array.Empty<ExplorerEntryRow>();
            _visibleExplorerItemLimit = 0;
            FilteredExplorerItemCount = 0;
            VisibleExplorerItemCount = 0;
            RefreshPendingChangesSummary();
            RepositoryRelinkStatusMessage = string.Empty;
            _repositoryDetail = null;

            var repo = await _mediator.Send(new GetRepositoryDetailQuery(repositoryId));
            if (repo is null)
            {
                ErrorMessage = Loc.T("explorer.error_repository_not_found");
                IsEmpty = true;
                return;
            }

            RepositoryId = repo.Id;
            RepositoryName = repo.Name;
            RepositoryPath = repo.DirectoryPath;
            _repositoryDetail = repo;
            _autoCaptureFileVersions = repo.AutoCaptureFileVersions;
            ApplyRepositoryScanStatus(_scanStatus.GetSnapshot(RepositoryId));
            IsEmpty = false;
            IsLoading = false;
            await Task.Yield();

            await RefreshEntriesAndTreeAsync(clearSelection: true);
            _log.LogInformation(
                "Repository explorer loaded. RepositoryId {RepositoryId}. Entries {EntryCount}. PendingChanges {PendingCount}",
                RepositoryId,
                _entries.Count,
                PendingChanges.Count);
            StartLiveSync(repo.DirectoryPath, repo.LinkedFormats);
            _lastProcessMetricsSnapshot = _monitoringControl.IsEnabled
                ? _processResourceStatusStore.Snapshot
                : null;
            RaiseLiveSyncStateChanged();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load explorer for repository {RepositoryId}", repositoryId);
            ErrorMessage = Loc.T("explorer.error_load_failed");
            RepositoryRelinkStatusMessage = string.Empty;
            _repositoryDetail = null;
            VersionPanelError = null;
            IsVersionActionRunning = false;
            Items.Clear();
            TreeNodes.Clear();
            FileVersions.Clear();
            VisibleFileVersions.Clear();
            ComparableFileVersions.Clear();
            ShowAllFileVersions = false;
            PendingChanges.Clear();
            _snapshotHistorySource.Clear();
            SnapshotHistory.Clear();
            _snapshotFilesSource.Clear();
            SnapshotFiles.Clear();
            SelectedSnapshot = null;
            SelectedSnapshotFile = null;
            _preferredSnapshotId = null;
            _preferredSnapshotFileRelativePath = null;
            _preferredSnapshotFileVersionId = null;
            HasPendingChanges = false;
            HasSnapshotHistory = false;
            HasSnapshotFiles = false;
            IsSnapshotHistoryLoading = false;
            IsSnapshotFilesLoading = false;
            IsSnapshotHistoryMenuOpen = false;
            IsDiffPreviewMenuOpen = false;
            IsFileHistoryMenuOpen = false;
            IsDiffPreviewLoading = false;
            DiffPreviewTitle = string.Empty;
            DiffPreviewSummary = string.Empty;
            DiffPreviewRows.Clear();
            _diffPreviewBeforeVersionId = null;
            _diffPreviewAfterVersionId = null;
            _diffPreviewRelativePath = null;
            ResetFullFilePreviewState();
            OnPropertyChanged(nameof(CanToggleFullFilePreview));
            OnPropertyChanged(nameof(CanOpenSelectedFileOnDisk));
            PendingChangesSummary = Loc.T("explorer.pending.load_failed");
            LastSnapshotLabel = Loc.T("explorer.snapshot.none_yet");
            IsEmpty = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchQueryChanged(string value) => ApplyExplorerFiltersIfNeeded();
    partial void OnSelectedEntryTypeFilterChanged(string value) => ApplyExplorerFiltersIfNeeded();
    partial void OnSelectedExtensionFilterChanged(string value) => ApplyExplorerFiltersIfNeeded();
    partial void OnSelectedModifiedWindowFilterChanged(string value) => ApplyExplorerFiltersIfNeeded();
    partial void OnMinSizeMbChanged(string value) => ApplyExplorerFiltersIfNeeded();
    partial void OnMaxSizeMbChanged(string value) => ApplyExplorerFiltersIfNeeded();
    partial void OnShowAllRepositoryFilesChanged(bool value) => _ = RefreshEntriesPresentationAsync(
        clearSelection: false,
        reloadPendingChanges: false,
        allowSnapshotHistoryReload: false);
    partial void OnIsExplorerGridViewModeChanged(bool value)
    {
        if (_suppressExplorerViewModeOptionChange)
            return;

        SelectExplorerViewModeOption(value ? "grid" : "list");
    }

    partial void OnSelectedExplorerViewModeOptionChanged(ExplorerViewModeOptionViewModel? value)
    {
        if (_suppressExplorerViewModeOptionChange || value is null)
            return;

        IsExplorerGridViewMode = string.Equals(value.Key, "grid", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSnapshotHistorySearchQueryChanged(string value) => ApplySnapshotHistoryFiltersIfNeeded();
    partial void OnSelectedSnapshotKindFilterChanged(string value) => ApplySnapshotHistoryFiltersIfNeeded();
    partial void OnSelectedSnapshotTriggerFilterChanged(string value) => ApplySnapshotHistoryFiltersIfNeeded();
    partial void OnSelectedSnapshotTagFilterChanged(string value) => ApplySnapshotHistoryFiltersIfNeeded();
    partial void OnSnapshotHistoryMinChangedFilesChanged(string value) => ApplySnapshotHistoryFiltersIfNeeded();
    partial void OnSnapshotFilesSearchQueryChanged(string value) => ApplySnapshotFilesFiltersIfNeeded();
    partial void OnSelectedSnapshotFileChangeKindFilterChanged(string value) => ApplySnapshotFilesFiltersIfNeeded();
    partial void OnSelectedSnapshotFileExtensionFilterChanged(string value) => ApplySnapshotFilesFiltersIfNeeded();
    partial void OnSnapshotFilesMinSizeDeltaKbChanged(string value) => ApplySnapshotFilesFiltersIfNeeded();

    partial void OnRepositoryPathChanged(string value)
    {
        OnPropertyChanged(nameof(CanOpenSelectedFileOnDisk));
        OnPropertyChanged(nameof(IsRepositoryPathAvailable));
        OnPropertyChanged(nameof(ShowRepositoryUnavailableCard));
        OnPropertyChanged(nameof(CanRescanRepository));
        OnPropertyChanged(nameof(CanCreateSnapshot));
        OnPropertyChanged(nameof(CanRelinkRepository));
        OnPropertyChanged(nameof(HasLiveSyncProcessHistory));
        OnPropertyChanged(nameof(LiveSyncProcessPeakText));
        OnPropertyChanged(nameof(LiveSyncProcessHistoryText));
    }

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
            _preferredSnapshotFileRelativePath = null;
            _preferredSnapshotFileVersionId = null;
            _isClearingSnapshotFileSelection = true;
            SelectedSnapshotFile = null;
            _isClearingSnapshotFileSelection = false;
        }
        OnPropertyChanged(nameof(CanOpenSelectedFileOnDisk));
        OnPropertyChanged(nameof(CanOpenSelectedFileHistory));
        _ = LoadVersionsForSelectedItemAsync(value);
    }

    partial void OnSelectedSnapshotChanged(RepositorySnapshotHistoryEntryViewModel? value)
    {
        var previousSnapshotId = _preferredSnapshotId;
        _preferredSnapshotId = value?.SnapshotId;

        if (value is null)
        {
            _preferredSnapshotFileRelativePath = null;
            _preferredSnapshotFileVersionId = null;
        }
        else if (previousSnapshotId != value.SnapshotId)
        {
            _preferredSnapshotFileRelativePath = null;
            _preferredSnapshotFileVersionId = null;
        }

        if (_suppressSnapshotSelectionLoad)
            return;

        _ = LoadSnapshotFilesForSelectedSnapshotAsync(value?.SnapshotId);
    }

    partial void OnSelectedSnapshotFileChanged(RepositorySnapshotFileChangeViewModel? value)
    {
        if (value is null)
        {
            if (!_isClearingSnapshotFileSelection)
            {
                _preferredSnapshotFileRelativePath = null;
                _preferredSnapshotFileVersionId = null;
            }

            return;
        }

        _preferredSnapshotFileRelativePath = value.RelativePath;
        _preferredSnapshotFileVersionId = value.FileVersionId;
        SelectItemFromSnapshotFile(value);
    }

    partial void OnSelectedVersionChanged(ExplorerFileVersionViewModel? value)
    {
        SelectedVersionHasContentBlocks = value?.HasContentBlocks == true;

        if (value is { HasContentBlocks: true, IsDeletionMarker: false })
            CompareLeftVersion = value;

        if (value is not null)
            _preferredSnapshotFileVersionId = value.FileVersionId;

        RefreshComparePairSummary();
        OnPropertyChanged(nameof(CanCompareSelectedVersionPair));
    }

    partial void OnCompareLeftVersionChanged(ExplorerFileVersionViewModel? value)
    {
        if (value is not null
            && CompareRightVersion is not null
            && CompareRightVersion.FileVersionId == value.FileVersionId)
        {
            CompareRightVersion = PickAlternativeComparableVersion(value.FileVersionId);
        }

        RefreshComparePairSummary();
        OnPropertyChanged(nameof(CanCompareSelectedVersionPair));
    }

    partial void OnCompareRightVersionChanged(ExplorerFileVersionViewModel? value)
    {
        if (value is not null
            && CompareLeftVersion is not null
            && CompareLeftVersion.FileVersionId == value.FileVersionId)
        {
            CompareLeftVersion = PickAlternativeComparableVersion(value.FileVersionId) ?? CompareLeftVersion;
        }

        RefreshComparePairSummary();
        OnPropertyChanged(nameof(CanCompareSelectedVersionPair));
    }

    private ExplorerFileVersionViewModel? PickAlternativeComparableVersion(long excludedFileVersionId)
        => ComparableFileVersions.FirstOrDefault(v => v.FileVersionId != excludedFileVersionId);

    partial void OnShowAllFileVersionsChanged(bool value)
    {
        RebuildVisibleFileVersions();
    }

    private void SelectItemFromSnapshotFile(RepositorySnapshotFileChangeViewModel value)
    {
        _isSyncingSelectionFromSnapshot = true;

        try
        {
            EnsureExplorerItemVisible(value.RelativePath);
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
                SizeDisplay = value.ChangeKind == "deleted" ? Loc.T("common.deleted") : FormatSize(value.CurrentSizeBytes),
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
        IsFileHistoryMenuOpen = false;
        CloseSnapshotHistoryWindow();
        StopLiveSync();
        BackRequested?.Invoke();
    }

    [RelayCommand]
    private async Task ToggleLiveSyncMonitoringAsync()
    {
        if (!CanToggleLiveSyncMonitoring)
            return;

        if (IsLiveSyncActive && !IsLiveSyncPaused)
            PauseLiveSync();
        else
            ResumeLiveSync();

        await RefreshLiveSyncIndicatorsAsync();
    }

    [RelayCommand]
    private async Task ProcessLiveSyncQueueNowAsync()
    {
        if (!CanProcessLiveSyncQueueNow)
            return;

        await FlushQueuedLiveSyncAsync(allowPaused: true, manualTrigger: true);
        await RefreshLiveSyncIndicatorsAsync();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (RepositoryId == 0 || OpenSettingsRequested is null)
            return;

        await RunTransientPreparationAsync(
            "explorer.open_settings_title",
            "explorer.open_settings_detail",
            () => OpenSettingsRequested.Invoke(RepositoryId),
            ex =>
            {
                _log.LogError(ex, "Failed to open repository settings from explorer. RepositoryId {RepositoryId}", RepositoryId);
                ErrorMessage = Loc.T("common.error_generic");
            });
    }

    [RelayCommand]
    private async Task AutoRelinkRepositoryAsync()
    {
        if (!CanRelinkRepository)
            return;

        await RunTransientPreparationAsync(
            "repo_relink.search_title",
            "repo_relink.search_detail",
            async () =>
            {
                RepositoryRelinkStatusMessage = string.Empty;

                var repository = await EnsureRepositoryDetailAsync();
                if (repository is null)
                {
                    RepositoryRelinkStatusMessage = Loc.T("ui_error.repository_not_found");
                    return;
                }

                var suggestion = await _relocationDetector.SuggestAsync(repository.DirectoryPath, _entries);
                if (!suggestion.CanAutoRelink || string.IsNullOrWhiteSpace(suggestion.SuggestedPath))
                {
                    RepositoryRelinkStatusMessage = suggestion.IsAmbiguous
                        ? Loc.T("repo_relink.error_auto_ambiguous")
                        : Loc.T("repo_relink.error_auto_not_found");
                    return;
                }

                await ApplyRepositoryRelinkAsync(repository, suggestion.SuggestedPath);
            },
            ex =>
            {
                _log.LogError(ex, "Failed to auto-relink repository from explorer. RepositoryId {RepositoryId}", RepositoryId);
                RepositoryRelinkStatusMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "repo_relink.error_apply_failed");
            });
    }

    [RelayCommand]
    private async Task BrowseRepositoryFolderAsync()
    {
        if (!CanRelinkRepository)
            return;

        var owner = _windows.GetActiveWindow();
        if (owner?.StorageProvider is null)
        {
            RepositoryRelinkStatusMessage = Loc.T("common.error_generic");
            return;
        }

        var selection = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("repo_relink.browse_title"),
            AllowMultiple = false
        });

        var selectedPath = StoragePathResolver.TryGetLocalPath(selection.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;

        await RunTransientPreparationAsync(
            "repo_relink.apply_title",
            "repo_relink.apply_detail",
            async () =>
            {
                RepositoryRelinkStatusMessage = string.Empty;

                var repository = await EnsureRepositoryDetailAsync();
                if (repository is null)
                {
                    RepositoryRelinkStatusMessage = Loc.T("ui_error.repository_not_found");
                    return;
                }

                await ApplyRepositoryRelinkAsync(repository, selectedPath);
            },
            ex =>
            {
                _log.LogError(ex, "Failed to manually relink repository from explorer. RepositoryId {RepositoryId}", RepositoryId);
                RepositoryRelinkStatusMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "repo_relink.error_apply_failed");
            });
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (!CanRescanRepository)
            return;

        await ExecuteScanAsync(
            saveFileVersions: false,
            triggerOverride: "sync_index_manual",
            fallbackMessage: Loc.T("explorer.scan.syncing_index"),
            showErrors: true,
            successMessage: Loc.T("explorer.scan.index_synchronized"));
    }

    [RelayCommand]
    private void ToggleExplorerFilters()
        => IsExplorerFiltersVisible = !IsExplorerFiltersVisible;

    [RelayCommand]
    private void ClearSearchQuery()
        => SearchQuery = string.Empty;

    [RelayCommand]
    private void ClearAdvancedFilters()
    {
        _suppressExplorerFilterApply = true;
        try
        {
            SearchQuery = string.Empty;
            SelectedEntryTypeFilter = "all";
            SelectedExtensionFilter = "all";
            SelectedModifiedWindowFilter = "all";
            MinSizeMb = string.Empty;
            MaxSizeMb = string.Empty;
        }
        finally
        {
            _suppressExplorerFilterApply = false;
        }

        ShowItemsForPath(_selectedDirectoryPath);
    }

    [RelayCommand]
    private async Task SaveCurrentExplorerFilterAsync()
    {
        var name = (SavedExplorerFilterName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = Loc.T("explorer.error_filter_preset_name_required");
            return;
        }

        var preset = BuildCurrentExplorerPreset(name);
        var existing = SavedExplorerFilters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var index = SavedExplorerFilters.IndexOf(existing);
            SavedExplorerFilters.RemoveAt(index);
            SavedExplorerFilters.Insert(index, new RepositoryExplorerSavedFilterViewModel(preset));
        }
        else
        {
            SavedExplorerFilters.Add(new RepositoryExplorerSavedFilterViewModel(preset));
        }

        SortSavedExplorerFilters();
        await PersistSavedExplorerFiltersAsync();
        SelectedExplorerSavedFilter = SavedExplorerFilters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        HasExplorerSavedFilters = SavedExplorerFilters.Count > 0;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void ApplyExplorerSavedFilter(RepositoryExplorerSavedFilterViewModel? filter)
    {
        var target = filter ?? SelectedExplorerSavedFilter;
        if (target is null)
            return;

        ApplyExplorerPreset(target.Preset);
    }

    [RelayCommand]
    private async Task DeleteExplorerSavedFilterAsync(RepositoryExplorerSavedFilterViewModel? filter)
    {
        var target = filter ?? SelectedExplorerSavedFilter;
        if (target is null)
            return;

        SavedExplorerFilters.Remove(target);
        HasExplorerSavedFilters = SavedExplorerFilters.Count > 0;

        if (ReferenceEquals(SelectedExplorerSavedFilter, target))
            SelectedExplorerSavedFilter = null;

        await PersistSavedExplorerFiltersAsync();
    }

    [RelayCommand]
    private void ClearSnapshotHistoryFilters()
    {
        _suppressSnapshotFilterApply = true;
        try
        {
            SnapshotHistorySearchQuery = string.Empty;
            SelectedSnapshotKindFilter = "all";
            SelectedSnapshotTriggerFilter = "all";
            SelectedSnapshotTagFilter = "all";
            SnapshotHistoryMinChangedFiles = string.Empty;
        }
        finally
        {
            _suppressSnapshotFilterApply = false;
        }

        ApplySnapshotHistoryFilters();
    }

    [RelayCommand]
    private void ClearSnapshotHistorySearch()
        => SnapshotHistorySearchQuery = string.Empty;

    [RelayCommand]
    private void ClearSnapshotFilesFilters()
    {
        _suppressSnapshotFilterApply = true;
        try
        {
            SnapshotFilesSearchQuery = string.Empty;
            SelectedSnapshotFileChangeKindFilter = "all";
            SelectedSnapshotFileExtensionFilter = "all";
            SnapshotFilesMinSizeDeltaKb = string.Empty;
        }
        finally
        {
            _suppressSnapshotFilterApply = false;
        }

        ApplySnapshotFilesFilters();
    }

    [RelayCommand]
    private void ClearSnapshotFilesSearch()
        => SnapshotFilesSearchQuery = string.Empty;

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        await RunTransientPreparationAsync(
            "explorer.snapshot_opening_title",
            "explorer.snapshot_opening_detail",
            async () =>
            {
                await LoadPendingChangesAsync();

                if (!HasPendingChanges)
                {
                    ErrorMessage = Loc.T("explorer.error_no_changes_for_snapshot");
                    return;
                }

                var owner = _windows.GetActiveWindow();
                if (owner is null)
                {
                    ErrorMessage = Loc.T("explorer.error_snapshot_dialog_unavailable");
                    return;
                }

                var dialog = _windows.Create<SnapshotNameDialogWindow>();
                if (dialog.DataContext is SnapshotNameDialogWindowViewModel vm)
                {
                    var defaultName = $"{Loc.T("snapshot.default_name_prefix")}_{DateTime.Now:yyyyMMdd_HHmmss}";
                    var changedFiles = PendingChanges.Select(change => new SnapshotPendingFileItemViewModel
                    {
                        RelativePath = change.RelativePath,
                        Name = change.Name,
                        ChangeKind = change.ChangeKind,
                        CurrentSizeBytes = change.CurrentSizeBytes,
                        BaselineSizeBytes = change.BaselineSizeBytes
                    }).ToList();

                    var knownTags = SnapshotTagFilters
                        .Where(t => !string.Equals(t, "all", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    vm.Initialize(defaultName, changedFiles, LoadSnapshotDialogPreviewAsync, knownTags);
                    vm.RequestOpenSnapshotTag += tag => _ = OpenSnapshotHistoryForTagAsync(tag);
                }

                await _windows.ShowDialogAsync(dialog, owner);

                if (!dialog.IsConfirmed)
                    return;

                var snapshotTitle = dialog.SnapshotTitle;
                var snapshotTags = dialog.SnapshotTags;

                await ExecuteScanAsync(
                    saveFileVersions: true,
                    triggerOverride: "manual_snapshot",
                    fallbackMessage: Loc.T("explorer.scan.saving_snapshot"),
                    showErrors: true,
                    successMessage: Loc.T("explorer.scan.snapshot_saved"),
                    snapshotTitle: snapshotTitle,
                    snapshotTags: snapshotTags);
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open snapshot dialog for repository {RepositoryId}", RepositoryId);
                ErrorMessage = Loc.T("explorer.error_snapshot_dialog_unavailable");
            });
    }

    private async Task OpenSnapshotHistoryForTagAsync(string tag)
    {
        if (RepositoryId <= 0 || string.IsNullOrWhiteSpace(tag))
            return;

        var normalizedTag = NormalizeSnapshotTagFilter(tag);
        if (string.IsNullOrWhiteSpace(normalizedTag) || string.Equals(normalizedTag, "all", StringComparison.OrdinalIgnoreCase))
            return;

        IsSnapshotHistoryMenuOpen = true;

        if (!SnapshotTagFilters.Contains(normalizedTag, StringComparer.OrdinalIgnoreCase))
            SnapshotTagFilters.Add(normalizedTag);

        SelectedSnapshotTagFilter = normalizedTag;

        if (!IsSnapshotHistoryLoading)
            await LoadSnapshotHistoryAsync(forceSnapshotFilesReload: true);

        if (!SnapshotTagFilters.Contains(normalizedTag, StringComparer.OrdinalIgnoreCase))
            SnapshotTagFilters.Add(normalizedTag);

        SelectedSnapshotTagFilter = normalizedTag;
        ApplySnapshotHistoryFilters(forceSnapshotFilesReload: true);
        ShowSnapshotHistoryWindow();
    }

    private async Task<PendingFileDiffPreviewDto> LoadSnapshotDialogPreviewAsync(
        SnapshotPendingFileItemViewModel file,
        CancellationToken ct)
    {
        if (RepositoryId <= 0)
            return PendingFileDiffPreviewDto.Unavailable(file.RelativePath, Loc.T("explorer.error_repository_not_selected"));

        var result = await _mediator.Send(new GetPendingFileDiffPreviewQuery(
            RepositoryId,
            file.RelativePath,
            3000), ct);

        if (!result.Success || result.Value is null)
            return PendingFileDiffPreviewDto.Unavailable(
                file.RelativePath,
                UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "explorer.preview_error.build_failed"));

        return result.Value;
    }
    [RelayCommand]
    private async Task ShowSnapshotHistoryAsync()
    {
        if (RepositoryId == 0)
            return;

        IsSnapshotHistoryMenuOpen = true;

        if (!IsSnapshotHistoryLoading)
            await LoadSnapshotHistoryAsync(SelectedSnapshot?.SnapshotId ?? 0);

        if (SelectedSnapshot is null && SnapshotHistory.Count > 0)
            SelectedSnapshot = SnapshotHistory[0];

        ShowSnapshotHistoryWindow();
    }

    [RelayCommand]
    private void CloseSnapshotHistoryMenu()
        => CloseSnapshotHistoryWindow();

    private void ShowSnapshotHistoryWindow()
    {
        if (_snapshotHistoryWindow is { } existing)
        {
            existing.Activate();
            existing.Focus();
            return;
        }

        var window = _windows.Create<SnapshotHistoryWindow>();
        window.DataContext = this;

        window.Closed += OnSnapshotHistoryWindowClosed;
        _snapshotHistoryWindow = window;
        _windows.Show(window);
    }

    private void CloseSnapshotHistoryWindow()
    {
        IsSnapshotHistoryMenuOpen = false;

        if (_snapshotHistoryWindow is not { } window)
            return;

        _snapshotHistoryWindow = null;
        window.Closed -= OnSnapshotHistoryWindowClosed;
        window.Close();
    }

    private void OnSnapshotHistoryWindowClosed(object? sender, EventArgs e)
    {
        if (sender is SnapshotHistoryWindow window)
            window.Closed -= OnSnapshotHistoryWindowClosed;

        _snapshotHistoryWindow = null;
        IsSnapshotHistoryMenuOpen = false;
    }

    [RelayCommand]
    private async Task ExportRepositoryBundleAsync()
    {
        if (RepositoryId <= 0 || IsMaintenanceRunning)
            return;

        var owner = _windows.GetActiveWindow();
        if (owner is null)
        {
            ErrorMessage = Loc.T("explorer.error_file_picker_unavailable");
            return;
        }

        var suggestedName = BuildSuggestedBundleFileName();
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("explorer.bundle.export_title"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType(Loc.T("explorer.bundle.file_type"))
                {
                    Patterns = ["*.veyra.zip", "*.veyra-bundle", "*.zip"]
                }
            ]
        });

        var bundlePath = StoragePathResolver.TryGetLocalPath(file);
        if (string.IsNullOrWhiteSpace(bundlePath))
            return;

        await RunMaintenanceOperationAsync(
            actionName: "export",
            startedMessage: Loc.T("explorer.bundle.export_started"),
            operation: async () => await _mediator.Send(new ExportRepositoryBundleCommand(RepositoryId, bundlePath)),
            onSuccess: result =>
            {
                VersionActionMessage = Loc.F(
                    "explorer.bundle.export_finished",
                    result.Summary,
                    result.BundlePath,
                    FormatSize(result.BundleSizeBytes));
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
            ErrorMessage = Loc.T("explorer.error_file_picker_unavailable");
            return;
        }

        var bundleSelection = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("explorer.bundle.import_title"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.T("explorer.bundle.file_type"))
                {
                    Patterns = ["*.veyra.zip", "*.veyra-bundle", "*.zip"]
                }
            ]
        });

        var bundlePath = StoragePathResolver.TryGetLocalPath(bundleSelection.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(bundlePath))
            return;

        var validation = await _mediator.Send(new ValidateRepositoryBundleQuery(bundlePath));
        if (!validation.IsValid)
        {
            ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(validation.Message, "explorer.bundle.validation_failed");
            return;
        }

        var targetDirectorySelection = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("explorer.bundle.pick_target_folder"),
            AllowMultiple = false
        });

        var targetDirectory = StoragePathResolver.TryGetLocalPath(targetDirectorySelection.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;

        await RunMaintenanceOperationAsync(
            actionName: "import",
            startedMessage: Loc.T("explorer.bundle.import_started"),
            operation: async () => await _mediator.Send(new ImportRepositoryBundleCommand(
                bundlePath,
                targetDirectory,
                RepositoryNameOverride: null)),
            onSuccess: result =>
            {
                var warningText = result.Warnings.Count == 0
                    ? string.Empty
                    : "\n" + Loc.T("explorer.bundle.warnings_title") + "\n" + string.Join('\n', result.Warnings);

                VersionActionMessage = Loc.F("explorer.bundle.import_finished", result.Summary, result.RepositoryId) + warningText;
            });
    }

    [RelayCommand]
    private async Task RepairRepositoryDataAsync()
    {
        if (RepositoryId <= 0 || IsMaintenanceRunning)
            return;

        await RunMaintenanceOperationAsync(
            actionName: "repair",
            startedMessage: Loc.T("explorer.maintenance.repair_started"),
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
            startedMessage: Loc.T("explorer.maintenance.reindex_started"),
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
            startedMessage: Loc.T("explorer.maintenance.relink_started"),
            operation: async () => await _mediator.Send(new RelinkRepositoryDataCommand(RepositoryId)),
            onSuccess: result =>
            {
                VersionActionMessage = FormatRecoveryMessage(result);
            });
    }

    [RelayCommand]
    private void CloseDiffPreviewMenu()
    {
        IsDiffPreviewMenuOpen = false;
        _diffPreviewBeforeVersionId = null;
        _diffPreviewAfterVersionId = null;
        _diffPreviewRelativePath = null;
        ResetFullFilePreviewState();
        OnPropertyChanged(nameof(CanToggleFullFilePreview));
        OnPropertyChanged(nameof(CanOpenSelectedFileOnDisk));
    }

    [RelayCommand]
    private async Task OpenSelectedFileHistoryAsync()
    {
        if (!CanOpenSelectedFileHistory || SelectedItem is null)
            return;

        IsFileHistoryMenuOpen = true;
        ShowAllFileVersions = true;

        if (!IsVersionLoading)
            await LoadVersionsForSelectedItemAsync(SelectedItem);
    }

    [RelayCommand]
    private void CloseFileHistoryMenu()
        => IsFileHistoryMenuOpen = false;

    [RelayCommand]
    private async Task ToggleFullFilePreviewAsync()
    {
        if (!CanToggleFullFilePreview)
            return;

        if (IsFullFilePreviewMode)
        {
            IsFullFilePreviewMode = false;
            return;
        }

        await LoadFullFilePreviewAsync();
    }

    [RelayCommand]
    private void SelectTreeNode(ExplorerTreeNodeViewModel? node)
    {
        if (node is null)
            return;

        UpdateTreeSelectionState(node);

        _selectedDirectoryPath = string.IsNullOrWhiteSpace(node.RelativePath)
            ? null
            : node.RelativePath;

        ShowItemsForPath(_selectedDirectoryPath);
    }

    [RelayCommand]
    private void UseExplorerListView()
    {
        IsExplorerGridViewMode = false;
    }

    [RelayCommand]
    private void UseExplorerGridView()
    {
        IsExplorerGridViewMode = true;
    }

    [RelayCommand]
    private async Task RefreshRepositoryTreeAsync()
    {
        await RefreshEntriesAndTreeAsync(clearSelection: false);
    }

    [RelayCommand]
    private async Task RefreshTreeFolderAsync(ExplorerTreeNodeViewModel? node)
    {
        if (node is not null)
            SelectedTreeNode = node;

        await RefreshEntriesPresentationAsync(
            clearSelection: false,
            reloadPendingChanges: false,
            allowSnapshotHistoryReload: false);
    }

    [RelayCommand]
    private void OpenTreeFolder(ExplorerTreeNodeViewModel? node)
    {
        if (node is null)
            return;

        SelectedTreeNode = node;
        SelectTreeNode(node);
    }

    [RelayCommand]
    private void OpenTreeFolderInExplorer(ExplorerTreeNodeViewModel? node)
    {
        var fullPath = GetFullPathForTreeNode(node);
        if (string.IsNullOrWhiteSpace(fullPath) || !Directory.Exists(fullPath))
        {
            ErrorMessage = Loc.T("explorer.file_operation.folder_not_found");
            return;
        }

        OpenPathInExplorer(fullPath, selectPath: false);
    }

    [RelayCommand]
    private async Task CreateFolderInTreeNodeAsync(ExplorerTreeNodeViewModel? node)
    {
        var parentPath = GetFullPathForTreeNode(node ?? SelectedTreeNode);
        if (string.IsNullOrWhiteSpace(parentPath) || !Directory.Exists(parentPath))
        {
            ErrorMessage = Loc.T("explorer.file_operation.folder_not_found");
            return;
        }

        try
        {
            ErrorMessage = null;
            var baseName = Loc.T("explorer.file_operation.new_folder_name");
            var createdPath = await Task.Run(() => CreateUniqueDirectory(parentPath, baseName));

            var relativePath = TryGetRepositoryRelativePath(createdPath);
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                ShowAllRepositoryFiles = true;
                ExpandAndSelectTreePath(relativePath);
            }

            await RefreshEntriesPresentationAsync(
                clearSelection: false,
                reloadPendingChanges: false,
                allowSnapshotHistoryReload: false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to create folder in repository explorer. RepositoryId {RepositoryId}. Parent {Parent}", RepositoryId, parentPath);
            ErrorMessage = Loc.T("explorer.file_operation.create_folder_failed");
        }
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

        ExpandAndSelectTreePath(item.RelativePath);
    }

    [RelayCommand]
    private void ActivateItem(ExplorerItemViewModel? item)
    {
        if (item is null)
            return;

        if (item.IsDirectory)
        {
            ExpandAndSelectTreePath(item.RelativePath);
            return;
        }

        OpenFileOnDisk(item);
    }

    [RelayCommand]
    private void OpenSelectedFile()
    {
        OpenFileOnDisk(SelectedItem);
    }

    [RelayCommand]
    private void OpenSelectedFileInExplorer()
    {
        OpenItemInExplorer(SelectedItem);
    }

    [RelayCommand]
    private void OpenItemInExplorer(ExplorerItemViewModel? item)
    {
        var fullPath = item is null ? GetSelectedFileFullPath() : GetFullPathForItem(item);
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            VersionPanelError = Loc.T("explorer.version_error.select_file_for_explorer");
            return;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            VersionPanelError = Loc.T("explorer.version_error.file_not_found_on_disk");
            return;
        }

        OpenPathInExplorer(fullPath, selectPath: File.Exists(fullPath));
    }

    [RelayCommand]
    private async Task CopyItemToFolderAsync(ExplorerItemViewModel? item)
    {
        await CopyOrMoveItemToFolderAsync(item, move: false);
    }

    [RelayCommand]
    private async Task MoveItemToFolderAsync(ExplorerItemViewModel? item)
    {
        await CopyOrMoveItemToFolderAsync(item, move: true);
    }

    private void OpenPathInExplorer(string fullPath, bool selectPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = selectPath ? $"/select,\"{fullPath}\"" : $"\"{fullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open Explorer for path {Path}", fullPath);
            VersionPanelError = Loc.T("explorer.version_error.open_in_explorer_failed");
        }
    }

    [RelayCommand]
    private Task RestoreVersionOverwriteAsync()
        => ExecuteRestoreVersionActionAsync(overwriteCurrent: true);

    [RelayCommand]
    private Task RestoreVersionAsCopyAsync()
        => ExecuteRestoreVersionActionAsync(overwriteCurrent: false);

    [RelayCommand]
    private Task RollbackRepositoryToSelectedSnapshotAsync()
        => ExecuteRepositorySnapshotRestoreAsync(RepositorySnapshotRestoreMode.Rollback);

    [RelayCommand]
    private Task RestoreSelectedSnapshotAsCopiesAsync()
        => ExecuteRepositorySnapshotRestoreAsync(RepositorySnapshotRestoreMode.Copies);

    private async Task ExecuteRestoreVersionActionAsync(bool overwriteCurrent)
    {
        if (IsVersionActionRunning)
            return;

        var selectedItem = SelectedItem;
        var selectedVersion = SelectedVersion;

        if (selectedItem is null || selectedItem.IsDirectory || selectedVersion is null)
            return;

        if (!selectedVersion.HasContentBlocks)
        {
            ErrorMessage = Loc.T("explorer.version_error.no_content_blocks");
            return;
        }

        IsRestoreOverwriteMode = overwriteCurrent;
        IsVersionActionRunning = true;
        VersionActionMessage = overwriteCurrent
            ? Loc.T("explorer.version_restore_overwrite_started")
            : Loc.T("explorer.version_restore_copy_started");
        VersionPanelError = null;
        ErrorMessage = null;

        try
        {
            var result = await _mediator.Send(new RestoreFileVersionCommand(
                RepositoryId,
                selectedItem.RelativePath,
                selectedVersion.FileVersionId,
                OverwriteCurrent: overwriteCurrent,
                TargetPath: null));

            if (!result.Success)
            {
                var message = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "explorer.version_error.restore_failed");
                ErrorMessage = message;
                VersionPanelError = message;
                VersionActionMessage = null;
                return;
            }

            var restoredPath = result.Value ?? string.Empty;
            VersionActionMessage = overwriteCurrent
                ? Loc.F("explorer.version_restore_overwrite_success", restoredPath)
                : Loc.F("explorer.version_restore_copy_success", restoredPath);

            _preferredSnapshotFileVersionId = selectedVersion.FileVersionId;
            await RefreshEntriesAndTreeAsync(clearSelection: false);
        }
        catch (OperationCanceledException)
        {
            // panel load cancellation can race with action refresh; safe to ignore
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Restore action failed. RepositoryId {RepositoryId}. Path {Path}. Version {VersionId}. Overwrite {Overwrite}",
                RepositoryId,
                selectedItem.RelativePath,
                selectedVersion.FileVersionId,
                overwriteCurrent);
            var message = Loc.T("explorer.version_error.restore_action_failed");
            ErrorMessage = message;
            VersionPanelError = message;
            VersionActionMessage = null;
        }
        finally
        {
            IsVersionActionRunning = false;
            IsRestoreOverwriteMode = false;
        }
    }

    private async Task ExecuteRepositorySnapshotRestoreAsync(string mode)
    {
        if (IsVersionActionRunning || SelectedSnapshot is null)
            return;

        var selected = SelectedSnapshot;
        var normalizedMode = RepositorySnapshotRestoreMode.Normalize(mode);

        IsRestoreOverwriteMode = normalizedMode == RepositorySnapshotRestoreMode.Rollback;
        IsVersionActionRunning = true;
        VersionPanelError = null;
        ErrorMessage = null;
        VersionActionMessage = normalizedMode == RepositorySnapshotRestoreMode.Rollback
            ? "Preparing repository rollback..."
            : "Preparing snapshot copy restore...";

        try
        {
            var planResult = await _mediator.Send(new GetRepositorySnapshotRestorePlanQuery(RepositoryId, selected.SnapshotId));
            if (!planResult.Success || planResult.Value is null)
            {
                var message = UserFacingMessageLocalizer.LocalizeOrFallback(planResult.Error, "explorer.version_error.restore_failed");
                ErrorMessage = message;
                VersionPanelError = message;
                VersionActionMessage = null;
                return;
            }

            var plan = planResult.Value;
            if (!plan.CanRestore)
            {
                var preview = string.Join(", ", plan.MissingBlockKeys.Take(6));
                var message = $"Cannot restore snapshot because {plan.MissingBlockCount} content block(s) are missing locally. {preview}";
                ErrorMessage = message;
                VersionPanelError = message;
                VersionActionMessage = null;
                return;
            }

            var confirmed = normalizedMode == RepositorySnapshotRestoreMode.Copies
                ? await ConfirmSnapshotCopyRestoreAsync(plan)
                : await ConfirmSnapshotRollbackAsync(plan);

            if (!confirmed)
            {
                VersionActionMessage = null;
                return;
            }

            VersionActionMessage = normalizedMode == RepositorySnapshotRestoreMode.Rollback
                ? "Restoring repository files and creating rollback snapshot..."
                : "Restoring snapshot files as copies...";

            var result = await _mediator.Send(new RestoreRepositorySnapshotCommand(
                RepositoryId,
                selected.SnapshotId,
                normalizedMode));

            if (!result.Success || result.Value is null)
            {
                var message = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "explorer.version_error.restore_failed");
                ErrorMessage = message;
                VersionPanelError = message;
                VersionActionMessage = null;
                return;
            }

            VersionActionMessage = normalizedMode == RepositorySnapshotRestoreMode.Rollback
                ? $"Rollback completed. Restored {result.Value.RestoredFilesCount} file(s), backed up {result.Value.BackedUpFilesCount} extra file(s), and created a new rollback snapshot."
                : $"Snapshot restored as copies to {result.Value.RestoreDirectoryPath}.";

            await RefreshEntriesAndTreeAsync(clearSelection: false);
            if (normalizedMode == RepositorySnapshotRestoreMode.Rollback)
                await LoadSnapshotHistoryAsync(result.Value.SourceSnapshotId, forceSnapshotFilesReload: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Repository snapshot restore failed. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Mode {Mode}",
                RepositoryId,
                selected.SnapshotId,
                normalizedMode);
            var message = "Snapshot restore failed.";
            ErrorMessage = message;
            VersionPanelError = message;
            VersionActionMessage = null;
        }
        finally
        {
            IsVersionActionRunning = false;
            IsRestoreOverwriteMode = false;
        }
    }

    private async Task<bool> ConfirmSnapshotRollbackAsync(RepositorySnapshotRestorePlanDto plan)
    {
        var selected = SelectedSnapshot;
        if (selected is null)
            return false;

        var title = $"Rollback repository to snapshot {plan.SnapshotId}";
        var message =
            $"Selected snapshot: {BuildSnapshotDisplayName(selected)}\n" +
            $"Files to restore: {plan.FilesToRestoreCount}\n" +
            $"Files that will change: {plan.FilesToChangeCount}\n" +
            $"Current files moved to backup: {plan.FilesToMoveToBackupCount}\n" +
            "A new snapshot will be created after rollback.";
        const string warning = "Current files that are not part of the selected snapshot will be moved to a backup folder outside the repository, not deleted.";

        return await ConfirmSnapshotRestoreAsync(title, message, warning, "Rollback");
    }

    private async Task<bool> ConfirmSnapshotCopyRestoreAsync(RepositorySnapshotRestorePlanDto plan)
    {
        var selected = SelectedSnapshot;
        if (selected is null)
            return false;

        var title = $"Restore snapshot {plan.SnapshotId} as copies";
        var message =
            $"Selected snapshot: {BuildSnapshotDisplayName(selected)}\n" +
            $"Files to restore: {plan.FilesToRestoreCount}\n" +
            $"Current files overwritten: 0\n" +
            "No new snapshot will be created automatically.";
        const string warning = "Files will be restored under .veyra-restores and current files will not be replaced.";

        return await ConfirmSnapshotRestoreAsync(title, message, warning, "Restore copies");
    }

    private async Task<bool> ConfirmSnapshotRestoreAsync(
        string title,
        string message,
        string warning,
        string confirmButton)
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return false;

        var window = _windows.Create<ConfirmActionWindow>();
        if (window.DataContext is ConfirmActionWindowViewModel vm)
            vm.Configure(title, message, warning, confirmButton);

        await _windows.ShowDialogAsync(window, owner);
        return window.DataContext is ConfirmActionWindowViewModel resultVm && resultVm.IsConfirmed;
    }

    private static string BuildSnapshotDisplayName(RepositorySnapshotHistoryEntryViewModel snapshot)
        => string.IsNullOrWhiteSpace(snapshot.DisplayTitle)
            ? $"snapshot {snapshot.SnapshotId}"
            : $"{snapshot.DisplayTitle} ({snapshot.DisplayTime})";

    public async Task FocusEntryAsync(string relativePath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        _suppressExplorerFilterApply = true;
        try
        {
            SearchQuery = GetLeafName(normalized);
            SelectedEntryTypeFilter = "all";
            SelectedExtensionFilter = "all";
            SelectedModifiedWindowFilter = "all";
            MinSizeMb = string.Empty;
            MaxSizeMb = string.Empty;
            SelectedExplorerSavedFilter = null;
            SavedExplorerFilterName = string.Empty;
        }
        finally
        {
            _suppressExplorerFilterApply = false;
        }

        var targetDirectory = isDirectory
            ? normalized
            : GetParentRelativePath(normalized);

        if (!string.IsNullOrWhiteSpace(targetDirectory))
            ExpandAndSelectTreePath(targetDirectory);
        else
            await ShowItemsForPathAsync(null, debounce: false);

        await ShowItemsForPathAsync(targetDirectory, debounce: false);

        if (!isDirectory)
        {
            EnsureExplorerItemVisible(normalized);
            var targetItem = Items.FirstOrDefault(item =>
                item.RelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase));

            if (targetItem is not null)
                SelectedItem = targetItem;
        }
        else
        {
            SelectedItem = null;
            ExpandAndSelectTreePath(normalized);
        }
    }

    public async Task FocusSnapshotAsync(long snapshotId)
    {
        if (snapshotId <= 0 || RepositoryId <= 0)
            return;

        IsSnapshotHistoryMenuOpen = true;
        await LoadSnapshotHistoryAsync(snapshotId);
        ShowSnapshotHistoryWindow();

        var target = SnapshotHistory.FirstOrDefault(item => item.SnapshotId == snapshotId);
        if (target is not null)
            SelectedSnapshot = target;
    }


    [RelayCommand]
    private async Task CompareSelectedWithPreviousAsync()
    {
        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
            return;

        if (!selectedVersion.HasContentBlocks || selectedVersion.IsDeletionMarker)
        {
            VersionPanelError = Loc.T("explorer.version_error.diff_unavailable");
            return;
        }

        var ordered = ComparableFileVersions.ToList();
        var index = ordered.FindIndex(v => v.FileVersionId == selectedVersion.FileVersionId);
        if (index < 0)
        {
            VersionPanelError = Loc.T("explorer.version_error.version_not_found");
            return;
        }

        var previous = ordered.Skip(index + 1).FirstOrDefault();
        if (previous is null)
        {
            VersionPanelError = Loc.T("explorer.version_error.no_previous_version");
            return;
        }

        CompareLeftVersion = previous;
        CompareRightVersion = selectedVersion;
        RefreshComparePairSummary();

        await OpenVersionCompareWindowAsync(previous, selectedVersion);
    }


    [RelayCommand]
    private async Task CompareSelectedVersionsAsync()
    {
        if (!CanCompareSelectedVersionPair || CompareLeftVersion is null || CompareRightVersion is null)
        {
            VersionPanelError = Loc.T("explorer.version_error.select_two_versions");
            return;
        }

        var (left, right) = NormalizeComparePairByCreatedAt(CompareLeftVersion, CompareRightVersion);
        CompareLeftVersion = left;
        CompareRightVersion = right;
        RefreshComparePairSummary();

        await OpenVersionCompareWindowAsync(left, right);
    }

    [RelayCommand(CanExecute = nameof(CanSwapCompareVersions))]
    private void SwapCompareVersions()
    {
        if (!CanSwapCompareVersions || CompareLeftVersion is null || CompareRightVersion is null)
            return;

        (CompareLeftVersion, CompareRightVersion) = (CompareRightVersion, CompareLeftVersion);
        RefreshComparePairSummary();
    }

    [RelayCommand]
    private void ToggleFileVersionsView()
    {
        if (!CanToggleFileVersionsView)
            return;

        ShowAllFileVersions = !ShowAllFileVersions;
        RebuildVisibleFileVersions();
    }

    private async Task OpenVersionCompareWindowAsync(
        ExplorerFileVersionViewModel leftVersion,
        ExplorerFileVersionViewModel rightVersion)
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null)
        {
            VersionPanelError = Loc.T("explorer.version_error.compare_window_unavailable");
            return;
        }

        var dialog = _windows.Create<FileVersionCompareWindow>();
        if (dialog.DataContext is FileVersionCompareWindowViewModel vm)
        {
            await RunTransientPreparationAsync(
                "explorer.compare_opening_title",
                "explorer.compare_opening_detail",
                async () =>
                {
                    await vm.InitializeAsync(
                        repositoryId: RepositoryId,
                        repositoryPath: RepositoryPath,
                        relativePath: SelectedItem?.RelativePath ?? leftVersion.RelativePath,
                        fileDisplayName: SelectedItem?.Name ?? leftVersion.FileName,
                        preferredLeftVersionId: leftVersion.FileVersionId,
                        preferredRightVersionId: rightVersion.FileVersionId);
                },
                ex =>
                {
                    _log.LogError(
                        ex,
                        "Failed to initialize compare window. RepositoryId {RepositoryId}. LeftVersion {LeftVersionId}. RightVersion {RightVersionId}",
                        RepositoryId,
                        leftVersion.FileVersionId,
                        rightVersion.FileVersionId);
                    VersionPanelError = Loc.T("explorer.version_error.compare_window_unavailable");
                });
        }

        await _windows.ShowDialogAsync(dialog, owner);
    }
    private async Task ShowDiffPreviewAsync(
        ExplorerFileVersionViewModel leftVersion,
        ExplorerFileVersionViewModel rightVersion)
    {
        var (beforeVersion, afterVersion) = NormalizeComparePairByCreatedAt(leftVersion, rightVersion);

        _diffPreviewBeforeVersionId = beforeVersion.FileVersionId;
        _diffPreviewAfterVersionId = afterVersion.FileVersionId;
        _diffPreviewRelativePath = SelectedItem?.RelativePath;
        ResetFullFilePreviewState();

        ErrorMessage = null;
        DiffPreview = null;
        VersionActionMessage = null;
        VersionPanelError = null;

        IsDiffPreviewLoading = true;
        IsDiffPreviewMenuOpen = true;
        OnPropertyChanged(nameof(CanToggleFullFilePreview));
        OnPropertyChanged(nameof(CanOpenSelectedFileOnDisk));
        DiffPreviewTitle = $"{(SelectedItem?.Name ?? beforeVersion.FileName)}  {beforeVersion.VersionName} -> {afterVersion.VersionName}";
        DiffPreviewSummary = Loc.F("explorer.diff_preparing_summary", FormatVersionInline(beforeVersion), FormatVersionInline(afterVersion));
        DiffPreviewRows.Clear();

        OperationResult<TextDiffResultDto> diffResult;

        try
        {
            diffResult = await _mediator.Send(new GetTextDiffQuery(
                beforeVersion.FileVersionId,
                afterVersion.FileVersionId,
                4000));
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to build diff preview. RepositoryId {RepositoryId}. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                RepositoryId,
                beforeVersion.FileVersionId,
                afterVersion.FileVersionId);

            var message = FormatDiffPreviewError(ex.Message, beforeVersion, afterVersion);
            VersionPanelError = message;
            DiffPreviewSummary = message;
            DiffPreviewRows.Clear();
            IsDiffPreviewLoading = false;
            return;
        }

        IsDiffPreviewLoading = false;

        if (!diffResult.Success || diffResult.Value is null)
        {
            var message = FormatDiffPreviewError(diffResult.Error, beforeVersion, afterVersion);
            VersionPanelError = message;
            DiffPreviewSummary = message;
            DiffPreviewRows.Clear();
            return;
        }

        var value = diffResult.Value;
        _diffPreviewRelativePath = value.RelativePath;
        OnPropertyChanged(nameof(CanOpenSelectedFileOnDisk));
        DiffPreviewTitle = $"{(SelectedItem?.Name ?? value.RelativePath)}  {beforeVersion.VersionName} -> {afterVersion.VersionName}";
        DiffPreviewSummary = Loc.F(
            value.IsTruncated ? "explorer.diff_ready_summary_truncated" : "explorer.diff_ready_summary",
            FormatVersionInline(beforeVersion),
            FormatVersionInline(afterVersion),
            value.AddedLines,
            value.RemovedLines);

        DiffPreviewRows.Clear();
        foreach (var row in BuildDiffPreviewRows(value.Lines, value.Hunks))
            DiffPreviewRows.Add(row);

        VersionActionMessage = Loc.F("explorer.diff_ready_message", beforeVersion.VersionName, afterVersion.VersionName, value.AddedLines, value.RemovedLines);
    }

    private async Task<ScanExecutionOutcome> ExecuteScanAsync(
        bool saveFileVersions,
        string triggerOverride,
        string fallbackMessage,
        bool showErrors,
        string? successMessage,
        string? diagnosticsOriginOverride = null,
        string? snapshotTitle = null,
        IReadOnlyList<string>? snapshotTags = null)
    {
        if (RepositoryId == 0)
            return ScanExecutionOutcome.Failed;

        if (!await _scanGate.WaitAsync(0))
        {
            if (showErrors)
                ErrorMessage = Loc.T("explorer.error_scan_already_running");
            return ScanExecutionOutcome.Deferred;
        }

        var success = false;
        var shouldRecordScanMetrics = false;
        var scanUiPrepared = false;
        var ownsScanStatus = false;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (showErrors)
                ErrorMessage = null;

            if (_scanStatus.GetSnapshot(RepositoryId) is { IsActive: true } activeScan)
            {
                ApplyRepositoryScanStatus(activeScan);
                if (showErrors)
                    ErrorMessage = Loc.T("explorer.error_scan_already_running");
                return ScanExecutionOutcome.Deferred;
            }

            PrepareScanUi(fallbackMessage);
            scanUiPrepared = true;
            _scanStatus.Begin(RepositoryId, triggerOverride, fallbackMessage);
            ownsScanStatus = true;
            shouldRecordScanMetrics = true;
            await Task.Yield();

            var progress = new Progress<RepositoryScanProgressDto>(p =>
            {
                var status = _scanStatus.Report(RepositoryId, triggerOverride, p);
                var nextPercent = Math.Clamp(p.Percent, 0, 100);
                if (nextPercent < ScanPercent)
                    nextPercent = ScanPercent;

                ScanPercent = nextPercent;
                ScanIsIndeterminate = p.FilesTotal <= 0 && p.Percent < 100;
                ScanProgressDetail = FormatScanProgressDetail(status);

                if (!string.IsNullOrWhiteSpace(p.Message))
                    ScanMessage = p.Message;
            });

            var command = new ScanRepositoryCommand(
                RepositoryId,
                progress,
                new RepositoryScanOptionsDto(
                    SaveFileVersions: saveFileVersions,
                    TriggerOverride: triggerOverride,
                    SnapshotTitle: snapshotTitle,
                    SnapshotTags: snapshotTags));

            var result = await _scopeExecutor.ExecuteAsync<IMediator, OperationResult<RepositoryScanResultDto>>(
                (mediator, token) => mediator.Send(command, token));

            if (!result.Success)
            {
                if (showErrors)
                    ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "explorer.error_scan_finished_with_error");
                return ScanExecutionOutcome.Failed;
            }

            if (result.Value?.SkippedBecauseScanInProgress == true)
            {
                shouldRecordScanMetrics = false;
                ApplyRepositoryScanStatus(_scanStatus.GetSnapshot(RepositoryId));
                if (showErrors)
                    ErrorMessage = Loc.T("explorer.error_scan_already_running");
                return ScanExecutionOutcome.Deferred;
            }

            await RefreshEntriesAndTreeAsync(clearSelection: false);

            if (!string.IsNullOrWhiteSpace(successMessage))
                VersionActionMessage = successMessage;

            success = true;
            return ScanExecutionOutcome.Completed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to scan repository {RepositoryId}. SaveVersions {SaveVersions}. Trigger {Trigger}",
                RepositoryId,
                saveFileVersions,
                triggerOverride);

            if (showErrors)
                ErrorMessage = Loc.T("explorer.error_scan_failed");
            return ScanExecutionOutcome.Failed;
        }
        finally
        {
            if (ownsScanStatus)
            {
                _scanStatus.Complete(
                    RepositoryId,
                    triggerOverride,
                    success,
                    success ? Loc.T("common.done") : ScanMessage);
            }

            if (scanUiPrepared)
                FinishScanUi(success);

            ApplyRepositoryScanStatus(_scanStatus.GetSnapshot(RepositoryId));
            if (shouldRecordScanMetrics)
            {
                RecordCompletedScanMetrics(
                    ResolveScanDiagnosticsOrigin(saveFileVersions, triggerOverride, diagnosticsOriginOverride),
                    stopwatch.Elapsed,
                    success);
            }

            _scanGate.Release();
        }
    }

    private async Task<IncrementalLiveSyncOutcome> ExecuteIncrementalLiveSyncAsync(
        RepositoryFsEventLease lease,
        bool saveFileVersions)
    {
        if (RepositoryId == 0)
            return IncrementalLiveSyncOutcome.Failed;

        if (!await _scanGate.WaitAsync(0))
            return IncrementalLiveSyncOutcome.Deferred;

        var success = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            PrepareScanUi(Loc.T("explorer.scan.syncing_file_changes"));
            await Task.Yield();

            var repository = await EnsureRepositoryDetailAsync();
            if (repository is null)
            {
                SetLiveSyncLastOperation(new RepositoryLiveSyncOperationSnapshot(
                    RepositoryLiveSyncOperationKind.IncrementalFailed,
                    saveFileVersions,
                    lease.Count,
                    0,
                    0,
                    "repository_missing"), stopwatch.Elapsed);
                return IncrementalLiveSyncOutcome.Failed;
            }

            var delta = await _liveSyncDeltaBuilder.BuildAsync(
                repository.DirectoryPath,
                repository.LinkedFormats,
                repository.ExcludedPatterns,
                _entries,
                lease.Items);

            if (!delta.CanApplyIncrementally)
            {
                _log.LogDebug(
                    "Falling back to full live-sync scan. RepositoryId {RepositoryId}. Reason {Reason}. LeaseItems {LeaseItems}",
                    RepositoryId,
                    delta.FallbackReason ?? "unknown",
                    lease.Count);
                SetLiveSyncLastOperation(new RepositoryLiveSyncOperationSnapshot(
                    RepositoryLiveSyncOperationKind.FallbackRequested,
                    saveFileVersions,
                    lease.Count,
                    delta.UpsertEntries.Count,
                    delta.RemovedPaths.Count,
                    delta.FallbackReason), stopwatch.Elapsed);
                return IncrementalLiveSyncOutcome.FallbackToFullScan;
            }

            if (!delta.HasMeaningfulChanges)
            {
                SetLiveSyncLastOperation(new RepositoryLiveSyncOperationSnapshot(
                    RepositoryLiveSyncOperationKind.IncrementalNoChanges,
                    saveFileVersions,
                    lease.Count,
                    0,
                    0,
                    null), stopwatch.Elapsed);
                success = true;
                return IncrementalLiveSyncOutcome.Completed;
            }

            var scannedAtUtc = DateTime.UtcNow;
            var saveResult = await _scopeExecutor.ExecuteAsync<IRepositorySnapshotRepository, SnapshotSaveResultDto>(
                (snapshots, token) => saveFileVersions
                    ? snapshots.ApplyVersionedSnapshotDeltaAsync(
                        RepositoryId,
                        "auto_snapshot_live_watcher_incremental",
                        scannedAtUtc,
                        delta.UpdatedEntries,
                        delta.UpsertEntries,
                        delta.RemovedPaths,
                        token)
                    : snapshots.ApplyWorkingSnapshotDeltaAsync(
                        RepositoryId,
                        "sync_live_watcher_incremental",
                        scannedAtUtc,
                        delta.UpsertEntries,
                        delta.RemovedPaths,
                        token));

            if (!saveResult.SnapshotCreated && !saveResult.NoChangesDetected)
            {
                SetLiveSyncLastOperation(new RepositoryLiveSyncOperationSnapshot(
                    RepositoryLiveSyncOperationKind.FallbackRequested,
                    saveFileVersions,
                    lease.Count,
                    delta.UpsertEntries.Count,
                    delta.RemovedPaths.Count,
                    "snapshot_delta_unavailable"), stopwatch.Elapsed);
                return IncrementalLiveSyncOutcome.FallbackToFullScan;
            }

            _entries = delta.UpdatedEntries;
            await RefreshEntriesPresentationAsync(
                clearSelection: false,
                reloadPendingChanges: true,
                allowSnapshotHistoryReload: saveFileVersions);

            if (saveResult.NoChangesDetected)
            {
                SetLiveSyncLastOperation(new RepositoryLiveSyncOperationSnapshot(
                    RepositoryLiveSyncOperationKind.IncrementalNoChanges,
                    saveFileVersions,
                    lease.Count,
                    0,
                    0,
                    null), stopwatch.Elapsed);
                success = true;
                return IncrementalLiveSyncOutcome.Completed;
            }

            SetLiveSyncLastOperation(new RepositoryLiveSyncOperationSnapshot(
                RepositoryLiveSyncOperationKind.IncrementalApplied,
                saveFileVersions,
                lease.Count,
                delta.UpsertEntries.Count,
                delta.RemovedPaths.Count,
                null), stopwatch.Elapsed);
            success = true;
            return IncrementalLiveSyncOutcome.Completed;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Failed to apply incremental live-sync update. RepositoryId {RepositoryId}. LeaseItems {LeaseItems}. SaveVersions {SaveVersions}",
                RepositoryId,
                lease.Count,
                saveFileVersions);
            SetLiveSyncLastOperation(new RepositoryLiveSyncOperationSnapshot(
                RepositoryLiveSyncOperationKind.IncrementalFailed,
                saveFileVersions,
                lease.Count,
                0,
                0,
                ex.Message), stopwatch.Elapsed);
            return IncrementalLiveSyncOutcome.Failed;
        }
        finally
        {
            FinishScanUi(success);
            _scanGate.Release();
        }
    }

    private async Task LoadVersionsForSelectedItemAsync(ExplorerItemViewModel? item)
    {
        var (requestId, ct) = BeginVersionsLoadRequest();

        FileVersions.Clear();
        VisibleFileVersions.Clear();
        ComparableFileVersions.Clear();
        ShowAllFileVersions = false;
        OnPropertyChanged(nameof(HasNoFileVersions));
        SelectedVersion = null;
        CompareLeftVersion = null;
        CompareRightVersion = null;
        DiffPreview = null;
        VersionActionMessage = null;
        VersionPanelError = null;
        IsDiffPreviewMenuOpen = false;
        IsDiffPreviewLoading = false;
        DiffPreviewTitle = string.Empty;
        DiffPreviewSummary = string.Empty;
        DiffPreviewRows.Clear();
        _diffPreviewBeforeVersionId = null;
        _diffPreviewAfterVersionId = null;
        _diffPreviewRelativePath = null;
        ResetFullFilePreviewState();
        OnPropertyChanged(nameof(CanToggleFullFilePreview));
        OnPropertyChanged(nameof(CanOpenSelectedFileOnDisk));

        if (item is null || item.IsDirectory || !item.IsTracked || RepositoryId == 0)
        {
            if (IsLatestVersionsLoadRequest(requestId))
                IsVersionLoading = false;

            return;
        }

        try
        {
            IsVersionLoading = true;
            await Task.Yield();

            var versions = await _mediator.Send(new GetFileVersionHistoryQuery(
                RepositoryId,
                item.RelativePath,
                40), ct);

            if (!IsLatestVersionsLoadRequest(requestId) || ct.IsCancellationRequested)
                return;

            var versionCount = versions.Count;
            var rows = versions
                .Select((version, index) => new ExplorerFileVersionViewModel
                {
                    FileVersionId = version.FileVersionId,
                    VersionOrdinal = versionCount - index,
                    CreatedAtUtc = version.CreatedAtUtc,
                    SizeBytes = version.SizeBytes,
                    IsDeletionMarker = version.IsDeletionMarker,
                    HasContentBlocks = version.HasContentBlocks,
                    ContentHashSha256 = version.ContentHashSha256,
                    RelativePath = item.RelativePath
                })
                .ToList();

            ReplaceFileVersions(rows);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // rapid navigation cancels previous version loads
        }
        catch (Exception ex)
        {
            if (!IsLatestVersionsLoadRequest(requestId))
                return;

            _log.LogError(ex,
                "Failed to load file versions. RepositoryId {RepositoryId}. Path {Path}",
                RepositoryId,
                item.RelativePath);
            var message = Loc.T("explorer.version_error.load_versions_failed");
            ErrorMessage = message;
            VersionPanelError = message;
        }
        finally
        {
            if (IsLatestVersionsLoadRequest(requestId))
                IsVersionLoading = false;
        }
    }

    private void RebuildVisibleFileVersions()
    {
        var selectedVersionId = SelectedVersion?.FileVersionId;

        var ordered = FileVersions
            .OrderByDescending(v => v.CreatedAtUtc)
            .ThenByDescending(v => v.FileVersionId)
            .ToList();

        var visible = ShowAllFileVersions
            ? ordered
            : ordered.Take(CollapsedVisibleFileVersions).ToList();

        if (!ShowAllFileVersions
            && selectedVersionId.HasValue
            && visible.All(v => v.FileVersionId != selectedVersionId.Value))
        {
            var selected = ordered.FirstOrDefault(v => v.FileVersionId == selectedVersionId.Value);
            if (selected is not null)
                visible.Add(selected);
        }

        ReplaceCollectionItems(VisibleFileVersions, visible, AreReferenceItemsEquivalent);

        OnPropertyChanged(nameof(CanToggleFileVersionsView));
        OnPropertyChanged(nameof(FileVersionsToggleLabel));
    }
    private void InitializeVersionComparePair()
    {
        RebuildVisibleFileVersions();

        var comparable = FileVersions
            .Where(v => v.HasContentBlocks && !v.IsDeletionMarker)
            .OrderByDescending(v => v.CreatedAtUtc)
            .ThenByDescending(v => v.FileVersionId)
            .ToList();

        ReplaceComparableFileVersions(comparable);

        if (comparable.Count == 0)
        {
            CompareLeftVersion = null;
            CompareRightVersion = null;
            RefreshComparePairSummary();
            OnPropertyChanged(nameof(CanCompareSelectedVersionPair));
            return;
        }

        if (SelectedVersion is { HasContentBlocks: true, IsDeletionMarker: false } selected)
        {
            var index = comparable.FindIndex(v => v.FileVersionId == selected.FileVersionId);
            var previous = index >= 0
                ? comparable.Skip(index + 1).FirstOrDefault()
                  ?? comparable.FirstOrDefault(v => v.FileVersionId != selected.FileVersionId)
                : comparable.FirstOrDefault(v => v.FileVersionId != selected.FileVersionId);

            CompareLeftVersion = previous;
            CompareRightVersion = selected;
        }
        else
        {
            CompareLeftVersion = comparable.Skip(1).FirstOrDefault();
            CompareRightVersion = comparable[0];
        }

        if (CompareLeftVersion is not null && CompareRightVersion is not null)
        {
            var normalized = NormalizeComparePairByCreatedAt(CompareLeftVersion, CompareRightVersion);
            CompareLeftVersion = normalized.Left;
            CompareRightVersion = normalized.Right;
        }

        RefreshComparePairSummary();
        OnPropertyChanged(nameof(CanCompareSelectedVersionPair));
    }

    private (ExplorerFileVersionViewModel Left, ExplorerFileVersionViewModel Right) NormalizeComparePairByCreatedAt(
        ExplorerFileVersionViewModel left,
        ExplorerFileVersionViewModel right)
    {
        if (left.CreatedAtUtc < right.CreatedAtUtc)
            return (left, right);

        if (left.CreatedAtUtc > right.CreatedAtUtc)
            return (right, left);

        return left.FileVersionId <= right.FileVersionId
            ? (left, right)
            : (right, left);
    }

    private void RefreshComparePairSummary()
    {
        if (ComparableFileVersions.Count == 0)
        {
            ComparePairSummary = Loc.T("explorer.compare.no_comparable");
            return;
        }

        if (CompareLeftVersion is null && CompareRightVersion is null)
        {
            ComparePairSummary = Loc.T("explorer.compare.select_before_after");
            return;
        }

        if (CompareLeftVersion is not null && CompareRightVersion is not null)
        {
            if (CompareLeftVersion.FileVersionId == CompareRightVersion.FileVersionId)
            {
                ComparePairSummary = Loc.T("explorer.compare.select_two_different");
                return;
            }

            var normalized = NormalizeComparePairByCreatedAt(CompareLeftVersion, CompareRightVersion);
            ComparePairSummary = Loc.F(
                "explorer.compare.before_after",
                FormatVersionInline(normalized.Left),
                FormatVersionInline(normalized.Right));
            return;
        }

        var selected = CompareLeftVersion ?? CompareRightVersion;
        ComparePairSummary = selected is null
            ? Loc.T("explorer.compare.select_before_after")
            : Loc.F("explorer.compare.pick_second", FormatVersionInline(selected));
    }

    private static string FormatVersionInline(ExplorerFileVersionViewModel version)
        => $"{version.VersionName} ({version.CreatedAtDisplay}, {version.SizeDisplay})";

    private static string FormatDiffPreviewError(
        string? rawMessage,
        ExplorerFileVersionViewModel beforeVersion,
        ExplorerFileVersionViewModel afterVersion)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return Loc.T("explorer.preview_error.compare_failed");

        var message = UserFacingMessageLocalizer.TryLocalize(rawMessage)?.Trim() ?? rawMessage.Trim();

        if (message.Contains("native block format", StringComparison.OrdinalIgnoreCase)
            || message.Contains("native block hash", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("explorer.preview_error.native_runtime_missing", beforeVersion.VersionName, afterVersion.VersionName);
        }

        if (message.Contains("missing", StringComparison.OrdinalIgnoreCase)
            || message.Contains("block", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.F("explorer.preview_error.blocks_missing", beforeVersion.VersionName, afterVersion.VersionName);
        }

        return message;
    }

    private async Task LoadPendingChangesAsync()
    {
        try
        {
            var pending = await _mediator.Send(new GetRepositoryPendingChangesQuery(RepositoryId, 400));
            var rows = pending.Entries
                .Select(entry => new RepositoryPendingChangeViewModel
                {
                    RelativePath = entry.RelativePath,
                    Name = entry.Name,
                    ChangeKind = entry.ChangeKind,
                    CurrentSizeBytes = entry.CurrentSizeBytes,
                    BaselineSizeBytes = entry.BaselineSizeBytes
                })
                .ToList();

            PendingChanges.Clear();
            foreach (var row in rows)
                PendingChanges.Add(row);

            HasPendingChanges = PendingChanges.Count > 0;
            _pendingBaselineSnapshotAtUtc = pending.BaselineSnapshotAtUtc;
            _pendingAddedCount = pending.AddedCount;
            _pendingModifiedCount = pending.ModifiedCount;
            _pendingDeletedCount = pending.DeletedCount;
            RefreshPendingChangesSummary();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load pending changes for repository {RepositoryId}", RepositoryId);
            PendingChanges.Clear();
            HasPendingChanges = false;
            _pendingBaselineSnapshotAtUtc = null;
            _pendingAddedCount = 0;
            _pendingModifiedCount = 0;
            _pendingDeletedCount = 0;
            PendingChangesSummary = Loc.T("explorer.pending.load_failed");
            LastSnapshotLabel = Loc.T("explorer.snapshot.none_yet");
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void OnProcessResourceStatusChanged(object? sender, ProcessResourceStatusChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _lastProcessMetricsSnapshot = _monitoringControl.IsEnabled ? e.Snapshot : null;
            RaiseLiveSyncStateChanged();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _lastProcessMetricsSnapshot = _monitoringControl.IsEnabled ? e.Snapshot : null;
            RaiseLiveSyncStateChanged();
        });
    }

    private void OnRepositoryScanStatusChanged(object? sender, RepositoryScanStatusChangedEventArgs e)
    {
        if (e.Snapshot.RepositoryId != RepositoryId)
            return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyRepositoryScanStatus(e.Snapshot);
            RefreshEntriesAfterBackgroundScanIfNeeded(e.Snapshot);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            ApplyRepositoryScanStatus(e.Snapshot);
            RefreshEntriesAfterBackgroundScanIfNeeded(e.Snapshot);
        });
    }

    private void ApplyRepositoryScanStatus(RepositoryScanStatusSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.RepositoryId != RepositoryId)
            return;

        if (!snapshot.IsActive)
        {
            if (IsScanRunning)
                FinishScanUi(snapshot.Percent >= 100);
            return;
        }

        IsScanRunning = true;
        ScanPercent = Math.Clamp(snapshot.Percent, 0, 100);
        ScanMessage = string.IsNullOrWhiteSpace(snapshot.Message)
            ? Loc.T("explorer.scan.syncing_index")
            : snapshot.Message;
        ScanIsIndeterminate = snapshot.IsIndeterminate;
        ScanProgressDetail = FormatScanProgressDetail(snapshot);
    }

    private void RefreshEntriesAfterBackgroundScanIfNeeded(RepositoryScanStatusSnapshot snapshot)
    {
        if (snapshot.IsActive
            || snapshot.Percent < 100
            || !string.Equals(snapshot.Trigger, "scheduled_sync_requested", StringComparison.Ordinal))
        {
            return;
        }

        if (snapshot.StartedAtUtc <= _lastBackgroundScanRefreshStartedUtc)
            return;

        _lastBackgroundScanRefreshStartedUtc = snapshot.StartedAtUtc;
        _ = RefreshEntriesAfterBackgroundScanAsync();
    }

    private async Task RefreshEntriesAfterBackgroundScanAsync()
    {
        try
        {
            await RefreshEntriesAndTreeAsync(clearSelection: false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to refresh explorer after background scan. RepositoryId {RepositoryId}", RepositoryId);
        }
    }

    private void OnMonitoringStateChanged(bool enabled)
    {
        void ApplyState()
        {
            if (!enabled)
            {
                ResetLiveSyncDiagnostics();
                ResetProcessMetrics();
                RaiseLiveSyncStateChanged();
                return;
            }

            if (_repositoryDetail is { } repository)
            {
                if (!IsLiveSyncActive && !IsLiveSyncPaused)
                    StartLiveSync(repository.DirectoryPath, repository.LinkedFormats);

                _lastProcessMetricsSnapshot = _processResourceStatusStore.Snapshot;
                RaiseLiveSyncStateChanged();
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyState();
            return;
        }

        Dispatcher.UIThread.Post(ApplyState);
    }

    partial void OnIsLoadingChanged(bool value) => RaiseLiveSyncStateChanged();
    partial void OnIsScanRunningChanged(bool value) => RaiseLiveSyncStateChanged();
    partial void OnIsLiveSyncActiveChanged(bool value) => RaiseLiveSyncStateChanged();
    partial void OnIsLiveSyncPausedChanged(bool value) => RaiseLiveSyncStateChanged();
    partial void OnLiveSyncLastIssueChanged(string value) => RaiseLiveSyncStateChanged();

    private void RefreshLocalizationState()
    {
        RefreshExplorerViewModeOptions();
        RefreshFilterOptionBindings();
        RefreshPendingChangesSummary();
        RefreshComparePairSummary();
        RefreshPendingChangesBindings();
        RefreshFileVersionBindings();
        RefreshScanProgressDetail();
        RebuildVisibleExplorerItems();

        var selectedSnapshotId = SelectedSnapshot?.SnapshotId ?? 0;
        var selectedSnapshotFilePath = SelectedSnapshotFile?.RelativePath;
        ApplySnapshotHistoryFilters(selectedSnapshotId);
        ApplySnapshotFilesFilters(selectedSnapshotFilePath);

        OnPropertyChanged(nameof(FullPreviewToggleLabel));
        OnPropertyChanged(nameof(FileVersionsToggleLabel));
        OnPropertyChanged(nameof(VersionActionProgressTitle));
        OnPropertyChanged(nameof(VersionActionProgressDetail));
        OnPropertyChanged(nameof(MaintenanceProgressTitle));
        OnPropertyChanged(nameof(MaintenanceProgressDetail));
        OnPropertyChanged(nameof(ScanProgressDetail));
        OnPropertyChanged(nameof(ShowRepositoryUnavailableCard));
        OnPropertyChanged(nameof(HasLiveSyncProcessLoad));
        OnPropertyChanged(nameof(LiveSyncProcessLoadText));
        OnPropertyChanged(nameof(LiveSyncProcessLoadDetailText));
        NotifyExplorerChromeStateChanged();
        RaiseLiveSyncStateChanged();
    }

    private void RefreshExplorerViewModeOptions()
    {
        var selectedKey = IsExplorerGridViewMode ? "grid" : "list";

        _suppressExplorerViewModeOptionChange = true;
        try
        {
            ExplorerViewModeOptions.Clear();
            ExplorerViewModeOptions.Add(new ExplorerViewModeOptionViewModel("list", "\uE8FD", Loc.T("explorer.view_list")));
            ExplorerViewModeOptions.Add(new ExplorerViewModeOptionViewModel("grid", "\uECA5", Loc.T("explorer.view_grid")));
            SelectExplorerViewModeOption(selectedKey);
        }
        finally
        {
            _suppressExplorerViewModeOptionChange = false;
        }
    }

    private void SelectExplorerViewModeOption(string key)
    {
        var selected = ExplorerViewModeOptions.FirstOrDefault(option =>
            string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
            SelectedExplorerViewModeOption = selected;
    }

    private void RaiseLiveSyncStateChanged()
    {
        OnPropertyChanged(nameof(ShowLiveSyncMonitorCard));
        OnPropertyChanged(nameof(HasLiveSyncIssue));
        OnPropertyChanged(nameof(HasLiveSyncProcessLoad));
        OnPropertyChanged(nameof(HasLiveSyncProcessHistory));
        OnPropertyChanged(nameof(HasLiveSyncOperationCost));
        OnPropertyChanged(nameof(HasLiveSyncScanCost));
        OnPropertyChanged(nameof(HasLiveSyncDiagnostics));
        OnPropertyChanged(nameof(IsRepositoryPathAvailable));
        OnPropertyChanged(nameof(ShowRepositoryUnavailableCard));
        OnPropertyChanged(nameof(LiveSyncStateText));
        OnPropertyChanged(nameof(LiveSyncModeText));
        OnPropertyChanged(nameof(LiveSyncQueueText));
        OnPropertyChanged(nameof(LiveSyncLastActivityText));
        OnPropertyChanged(nameof(LiveSyncLastResultText));
        OnPropertyChanged(nameof(LiveSyncProcessLoadText));
        OnPropertyChanged(nameof(LiveSyncProcessLoadDetailText));
        OnPropertyChanged(nameof(LiveSyncProcessPeakText));
        OnPropertyChanged(nameof(LiveSyncProcessHistoryText));
        OnPropertyChanged(nameof(LiveSyncOperationCostText));
        OnPropertyChanged(nameof(LiveSyncScanCostText));
        OnPropertyChanged(nameof(LiveSyncTrackedFormatsText));
        OnPropertyChanged(nameof(LiveSyncToggleLabel));
        OnPropertyChanged(nameof(CanToggleLiveSyncMonitoring));
        OnPropertyChanged(nameof(CanProcessLiveSyncQueueNow));
        PublishLiveSyncStatus();
    }

    private void ResetLiveSyncLastOperation()
    {
        _liveSyncLastOperation = RepositoryLiveSyncOperationSnapshot.None;
        RaiseLiveSyncStateChanged();
    }

    private void ResetLiveSyncDiagnostics()
    {
        _lastLiveSyncOperationDuration = null;
        _lastLiveSyncOperationCompletedUtc = DateTime.MinValue;
        _lastCompletedScanDuration = null;
        _lastCompletedScanUtc = DateTime.MinValue;
        _lastCompletedScanOrigin = string.Empty;
        _lastCompletedScanSucceeded = false;
        RaiseLiveSyncStateChanged();
    }

    private void ResetProcessMetrics()
    {
        _lastProcessMetricsSnapshot = null;
        RaiseLiveSyncStateChanged();
    }

    private void SetLiveSyncLastOperation(RepositoryLiveSyncOperationSnapshot operation, TimeSpan? duration = null)
    {
        _liveSyncLastOperation = operation;

        if (duration is not null)
        {
            _lastLiveSyncOperationDuration = duration.Value;
            _lastLiveSyncOperationCompletedUtc = DateTime.UtcNow;
        }

        RaiseLiveSyncStateChanged();
    }

    private void RecordCompletedScanMetrics(string origin, TimeSpan duration, bool success)
    {
        _lastCompletedScanOrigin = origin;
        _lastCompletedScanDuration = duration;
        _lastCompletedScanUtc = DateTime.UtcNow;
        _lastCompletedScanSucceeded = success;
        RaiseLiveSyncStateChanged();
    }

    private void UpdateLiveSyncFallbackOutcome(ScanExecutionOutcome outcome)
    {
        if (_liveSyncLastOperation.Kind != RepositoryLiveSyncOperationKind.FallbackRequested)
            return;

        var nextKind = outcome switch
        {
            ScanExecutionOutcome.Completed => RepositoryLiveSyncOperationKind.FallbackCompleted,
            ScanExecutionOutcome.Failed => RepositoryLiveSyncOperationKind.FallbackFailed,
            _ => RepositoryLiveSyncOperationKind.FallbackRequested
        };

        _liveSyncLastOperation = _liveSyncLastOperation with { Kind = nextKind };
        RaiseLiveSyncStateChanged();
    }

    private static string ResolveScanDiagnosticsOrigin(
        bool saveFileVersions,
        string triggerOverride,
        string? diagnosticsOriginOverride)
    {
        if (!string.IsNullOrWhiteSpace(diagnosticsOriginOverride))
            return diagnosticsOriginOverride.Trim();

        var normalizedTrigger = (triggerOverride ?? string.Empty).Trim().ToLowerInvariant();
        return normalizedTrigger switch
        {
            "sync_index_manual" => "manual_rescan",
            "manual_snapshot" => "manual_snapshot",
            _ when normalizedTrigger.Contains("live_watcher", StringComparison.Ordinal) => "live_sync_fallback",
            _ when saveFileVersions => "snapshot_scan",
            _ => "full_rescan"
        };
    }

    private static string LocalizeScanDiagnosticsOrigin(string origin)
    {
        return (origin ?? string.Empty).Trim() switch
        {
            "live_sync_fallback" => Loc.T("explorer.monitoring_scan_origin_live_sync_fallback"),
            "manual_rescan" => Loc.T("explorer.monitoring_scan_origin_manual_rescan"),
            "manual_snapshot" => Loc.T("explorer.monitoring_scan_origin_manual_snapshot"),
            "snapshot_scan" => Loc.T("explorer.monitoring_scan_origin_snapshot_scan"),
            _ => Loc.T("explorer.monitoring_scan_origin_full_rescan")
        };
    }

    private static string FormatMonitoringDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
            return Loc.F("explorer.monitoring_duration_ms", Math.Max(1, (int)Math.Round(duration.TotalMilliseconds)));

        if (duration.TotalMinutes < 1)
            return Loc.F("explorer.monitoring_duration_seconds", duration.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture));

        if (duration.TotalHours < 1)
            return Loc.F("explorer.monitoring_duration_minutes_seconds", (int)duration.TotalMinutes, duration.Seconds);

        return Loc.F("explorer.monitoring_duration_hours_minutes", (int)duration.TotalHours, duration.Minutes);
    }

    private void PublishLiveSyncStatus()
    {
        if (RepositoryId <= 0)
            return;

        _liveSyncStatusStore.Update(new RepositoryLiveSyncStatusSnapshot(
            RepositoryId,
            RepositoryName,
            _liveSyncRootPath,
            IsLiveSyncActive,
            IsLiveSyncPaused,
            IsScanRunning,
            _autoCaptureFileVersions,
            string.IsNullOrWhiteSpace(_liveSyncRootPath) || Directory.Exists(_liveSyncRootPath),
            _liveSyncQueueStatus.PendingCount,
            _liveSyncQueueStatus.RunningCount,
            _liveSyncQueueStatus.TotalCount,
            _liveSyncQueueStatus.MaxRetryCount,
            _lastLiveSyncUtc,
            string.IsNullOrWhiteSpace(LiveSyncLastIssue) ? null : LiveSyncLastIssue,
            _liveSyncLastOperation));
    }

    private async Task<RepositoryDto?> EnsureRepositoryDetailAsync(CancellationToken ct = default)
    {
        if (_repositoryDetail is { Id: > 0 } detail && detail.Id == RepositoryId)
            return detail;

        if (RepositoryId <= 0)
            return null;

        _repositoryDetail = await _mediator.Send(new GetRepositoryDetailQuery(RepositoryId), ct);
        return _repositoryDetail;
    }

    private async Task ApplyRepositoryRelinkAsync(RepositoryDto repository, string newPath)
    {
        var command = RepositoryRelinkCommandFactory.Create(repository, newPath);
        var result = await _scopeExecutor.ExecuteAsync<IMediator, OperationResult>(
            (mediator, token) => mediator.Send(command, token));

        if (!result.Success)
        {
            RepositoryRelinkStatusMessage = UserFacingMessageLocalizer.LocalizeOrFallback(
                result.Error,
                "repo_relink.error_apply_failed");
            return;
        }

        await LoadAsync(repository.Id);
    }

    private void RefreshFilterOptionBindings()
    {
        RebuildStaticFilterOptions(EntryTypeFilters, SelectedEntryTypeFilter, ["all", "files", "folders"], value => SelectedEntryTypeFilter = value);
        RebuildStaticFilterOptions(ModifiedWindowFilters, SelectedModifiedWindowFilter, ["all", "24h", "7d", "30d"], value => SelectedModifiedWindowFilter = value);
        RebuildStaticFilterOptions(SnapshotFileChangeKindFilters, SelectedSnapshotFileChangeKindFilter, ["all", "added", "modified", "removed"], value => SelectedSnapshotFileChangeKindFilter = value);

        var selectedKind = SelectedSnapshotKindFilter;
        var currentKinds = SnapshotKindFilters.ToList();
        RebuildStaticFilterOptions(SnapshotKindFilters, selectedKind, currentKinds, value => SelectedSnapshotKindFilter = value);

        var selectedTrigger = SelectedSnapshotTriggerFilter;
        var currentTriggers = SnapshotTriggerFilters.ToList();
        RebuildStaticFilterOptions(SnapshotTriggerFilters, selectedTrigger, currentTriggers, value => SelectedSnapshotTriggerFilter = value);
        RebuildSnapshotTagFilters();
    }

    private void RebuildStaticFilterOptions(
        ObservableCollection<string> collection,
        string selected,
        IEnumerable<string> items,
        Action<string> restoreSelection)
    {
        var values = items.ToList();
        if (values.Count == 0)
            return;

        collection.Clear();
        foreach (var item in values)
            collection.Add(item);

        restoreSelection(values.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? selected
            : values[0]);
    }

    private void RefreshPendingChangesSummary()
    {
        if (_pendingBaselineSnapshotAtUtc is null)
        {
            LastSnapshotLabel = Loc.T("explorer.snapshot.none_yet");
            PendingChangesSummary = HasPendingChanges
                ? Loc.P("explorer.pending.ready_to_snapshot", PendingChanges.Count, PendingChanges.Count)
                : Loc.T("explorer.pending.first_snapshot_hint");
            return;
        }

        var local = _pendingBaselineSnapshotAtUtc.Value.ToLocalTime();
        LastSnapshotLabel = Loc.F("explorer.snapshot.last_at", local);

        var total = _pendingAddedCount + _pendingModifiedCount + _pendingDeletedCount;
        PendingChangesSummary = total == 0
            ? Loc.T("explorer.pending.none_since_snapshot")
            : Loc.F("explorer.pending.change_counts", _pendingAddedCount, _pendingModifiedCount, _pendingDeletedCount);
    }

    private void RefreshPendingChangesBindings()
    {
        if (PendingChanges.Count == 0)
            return;

        foreach (var item in PendingChanges)
            item.RefreshLocalization();
    }

    private void RefreshFileVersionBindings()
    {
        if (FileVersions.Count == 0)
        {
            RebuildVisibleFileVersions();
            return;
        }

        var selectedVersionId = SelectedVersion?.FileVersionId;
        var compareLeftId = CompareLeftVersion?.FileVersionId;
        var compareRightId = CompareRightVersion?.FileVersionId;

        foreach (var version in FileVersions)
            version.RefreshLocalization();

        SelectedVersion = selectedVersionId is > 0
            ? FileVersions.FirstOrDefault(x => x.FileVersionId == selectedVersionId.Value)
            : null;

        ReplaceComparableFileVersions(
            FileVersions.Where(v => v.HasContentBlocks && !v.IsDeletionMarker)
                .OrderByDescending(v => v.CreatedAtUtc)
                .ThenByDescending(v => v.FileVersionId)
                .ToList());

        CompareLeftVersion = compareLeftId is > 0
            ? ComparableFileVersions.FirstOrDefault(x => x.FileVersionId == compareLeftId.Value)
            : null;
        CompareRightVersion = compareRightId is > 0
            ? ComparableFileVersions.FirstOrDefault(x => x.FileVersionId == compareRightId.Value)
            : null;

        RebuildVisibleFileVersions();
        OnPropertyChanged(nameof(CanCompareSelectedVersionPair));
    }

    private async Task LoadSnapshotHistoryAsync(long preferredSnapshotId = 0, bool forceSnapshotFilesReload = false)
    {
        if (RepositoryId == 0)
            return;

        IsSnapshotHistoryLoading = true;
        await Task.Yield();

        try
        {
            var history = await _mediator.Send(new GetRepositorySnapshotHistoryQuery(RepositoryId, 200));

            _snapshotHistorySource.Clear();
            foreach (var item in history)
            {
                _snapshotHistorySource.Add(new RepositorySnapshotHistoryEntryViewModel
                {
                    SnapshotId = item.SnapshotId,
                    Title = item.Title,
                    CreatedAtUtc = item.CreatedAtUtc,
                    Kind = item.Kind,
                    IsArchived = item.IsArchived,
                    Trigger = item.Trigger,
                    ChangedFilesCount = item.ChangedFilesCount,
                    Tags = item.Tags ?? Array.Empty<string>()
                });
            }

            var targetSnapshotId = preferredSnapshotId > 0
                ? preferredSnapshotId
                : _preferredSnapshotId ?? SelectedSnapshot?.SnapshotId ?? 0;
            RebuildSnapshotKindFilters();
            RebuildSnapshotTriggerFilters();
            RebuildSnapshotTagFilters();
            ApplySnapshotHistoryFilters(targetSnapshotId, forceSnapshotFilesReload);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load snapshot history for repository {RepositoryId}", RepositoryId);
            _snapshotHistorySource.Clear();
            SnapshotHistory.Clear();
            _snapshotFilesSource.Clear();
            SnapshotFiles.Clear();
            SelectedSnapshot = null;
            _isClearingSnapshotFileSelection = true;
            SelectedSnapshotFile = null;
            _isClearingSnapshotFileSelection = false;
            HasSnapshotHistory = false;
            HasSnapshotFiles = false;
            _preferredSnapshotId = null;
            _preferredSnapshotFileRelativePath = null;
            _preferredSnapshotFileVersionId = null;
        }
        finally
        {
            IsSnapshotHistoryLoading = false;
        }
    }

    private async Task LoadSnapshotFilesForSelectedSnapshotAsync(long? snapshotId)
    {
        var (requestId, ct) = BeginSnapshotFilesLoadRequest();
        var preferredFilePath = _preferredSnapshotFileRelativePath;

        if (snapshotId is null || snapshotId <= 0 || RepositoryId == 0)
        {
            if (IsLatestSnapshotFilesLoadRequest(requestId))
            {
                ClearSnapshotFilesState();
                IsSnapshotFilesLoading = false;
            }

            return;
        }

        IsSnapshotFilesLoading = true;
        await Task.Yield();

        try
        {
            var files = await _mediator.Send(new GetRepositorySnapshotChangedFilesQuery(
                RepositoryId,
                snapshotId.Value,
                2000), ct);

            if (!IsLatestSnapshotFilesLoadRequest(requestId) || ct.IsCancellationRequested)
                return;

            var rows = new List<RepositorySnapshotFileChangeViewModel>(files.Count);
            foreach (var file in files)
            {
                rows.Add(new RepositorySnapshotFileChangeViewModel
                {
                    SnapshotId = file.SnapshotId,
                    FileIdentityId = file.FileIdentityId,
                    FileVersionId = file.FileVersionId,
                    RelativePath = file.RelativePath,
                    Name = file.Name,
                    ChangeKind = file.ChangeKind,
                    CurrentSizeBytes = file.CurrentSizeBytes,
                    PreviousSizeBytes = file.PreviousSizeBytes,
                    VersionCreatedAtUtc = file.VersionCreatedAtUtc
                });
            }

            _snapshotFilesSource.Clear();
            _snapshotFilesSource.AddRange(rows);
            RebuildSnapshotFileExtensionFilters();
            ApplySnapshotFilesFilters(preferredFilePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // quick navigation cancels stale snapshot-file loads
        }
        catch (Exception ex)
        {
            if (!IsLatestSnapshotFilesLoadRequest(requestId))
                return;

            _log.LogError(
                ex,
                "Failed to load snapshot files for repository {RepositoryId}, snapshot {SnapshotId}",
                RepositoryId,
                snapshotId);

            ClearSnapshotFilesState();
            _preferredSnapshotFileRelativePath = null;
            _preferredSnapshotFileVersionId = null;
        }
        finally
        {
            if (IsLatestSnapshotFilesLoadRequest(requestId))
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
            MaintenanceActionName = actionName;
            IsMaintenanceRunning = true;
            ErrorMessage = null;
            VersionActionMessage = startedMessage;

            var result = await operation();

            if (!result.Success || result.Value is null)
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(
                    result.Error,
                    "explorer.maintenance.operation_failed",
                    GetMaintenanceActionDisplayName(actionName));
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
            ErrorMessage = Loc.F(
                "explorer.maintenance.operation_failed_with_details",
                GetMaintenanceActionDisplayName(actionName),
                UserFacingMessageLocalizer.TryLocalize(ex.Message) ?? ex.Message);
            VersionActionMessage = null;
        }
        finally
        {
            IsMaintenanceRunning = false;
            MaintenanceActionName = string.Empty;
        }
    }
    private async Task RefreshEntriesAndTreeAsync(bool clearSelection)
    {
        if (RepositoryId == 0)
            return;

        _entries = await _mediator.Send(new GetRepositoryLatestEntriesQuery(RepositoryId));
        await RefreshEntriesPresentationAsync(
            clearSelection,
            reloadPendingChanges: true,
            allowSnapshotHistoryReload: true);
    }

    private async Task RefreshEntriesPresentationAsync(
        bool clearSelection,
        bool reloadPendingChanges,
        bool allowSnapshotHistoryReload)
    {
        var previousDirectoryPath = clearSelection ? null : _selectedDirectoryPath;
        var previousItemPath = clearSelection ? null : SelectedItem?.RelativePath;
        var previousSnapshotId = clearSelection ? 0 : SelectedSnapshot?.SnapshotId ?? 0;
        var previousSnapshotFilePath = clearSelection
            ? null
            : SelectedSnapshotFile?.RelativePath ?? _preferredSnapshotFileRelativePath;
        long? previousVersionId = clearSelection
            ? null
            : SelectedVersion?.FileVersionId ?? _preferredSnapshotFileVersionId;

        _preferredSnapshotId = previousSnapshotId > 0 ? previousSnapshotId : null;
        _preferredSnapshotFileRelativePath = previousSnapshotFilePath;
        _preferredSnapshotFileVersionId = previousVersionId;

        RebuildExtensionFilters();
        var buildTreeTask = BuildTreeAsync();
        var pendingChangesTask = reloadPendingChanges
            ? LoadPendingChangesAsync()
            : Task.CompletedTask;
        var shouldRefreshSnapshots = allowSnapshotHistoryReload
            && (IsSnapshotHistoryMenuOpen
                || previousSnapshotId > 0
                || !string.IsNullOrWhiteSpace(previousSnapshotFilePath));
        var snapshotHistoryTask = shouldRefreshSnapshots
            ? LoadSnapshotHistoryAsync(previousSnapshotId, forceSnapshotFilesReload: true)
            : Task.CompletedTask;

        await buildTreeTask;

        var normalizedPreviousDirectoryPath = NormalizeTreePathKey(previousDirectoryPath);
        if (!string.IsNullOrWhiteSpace(normalizedPreviousDirectoryPath))
            EnsureTreePathMaterialized(normalizedPreviousDirectoryPath);

        if (!string.IsNullOrWhiteSpace(normalizedPreviousDirectoryPath)
            && _nodeByPath.TryGetValue(normalizedPreviousDirectoryPath, out var previousNode))
        {
            previousNode.IsExpanded = true;
            SelectedTreeNode = previousNode;
            _selectedDirectoryPath = normalizedPreviousDirectoryPath;
            await ShowItemsForPathAsync(normalizedPreviousDirectoryPath, debounce: false);
        }
        else
        {
            _selectedDirectoryPath = null;
            await ShowItemsForPathAsync(null, debounce: false);
            SelectedTreeNode = TreeNodes.FirstOrDefault();
        }

        if (!clearSelection && !string.IsNullOrWhiteSpace(previousItemPath))
        {
            EnsureExplorerItemVisible(previousItemPath);
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

        await pendingChangesTask;
        await snapshotHistoryTask;
    }

    private void PrepareScanUi(string fallbackMessage)
    {
        IsScanRunning = true;
        ScanPercent = 0;
        ScanMessage = fallbackMessage;
        ScanProgressDetail = Loc.T("explorer.scan.progress_waiting");
        ScanIsIndeterminate = true;
    }

    private void FinishScanUi(bool success)
    {
        if (success)
        {
            ScanPercent = 100;
            ScanMessage = Loc.T("common.done");
        }

        ScanIsIndeterminate = false;
        ScanProgressDetail = string.Empty;
        IsScanRunning = false;
    }

    private void RefreshScanProgressDetail()
    {
        if (_scanStatus.GetSnapshot(RepositoryId) is { IsActive: true } snapshot)
            ScanProgressDetail = FormatScanProgressDetail(snapshot);
    }

    private string FormatScanProgressDetail(RepositoryScanStatusSnapshot snapshot)
    {
        var parts = new List<string>();

        if (snapshot.FilesTotal > 0)
            parts.Add(Loc.F("explorer.scan.progress_files", snapshot.FilesProcessed, snapshot.FilesTotal));
        else if (snapshot.FilesProcessed > 0)
            parts.Add(Loc.F("explorer.scan.progress_files_seen", snapshot.FilesProcessed));

        if (snapshot.EstimatedRemaining is { } eta)
            parts.Add(Loc.F("explorer.scan.progress_eta", FormatScanEta(eta)));
        else if (snapshot.IsIndeterminate)
            parts.Add(Loc.T("explorer.scan.progress_counting"));

        return parts.Count == 0
            ? Loc.T("explorer.scan.progress_working")
            : string.Join(" · ", parts);
    }

    private static string FormatScanEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
            return $"{(int)eta.TotalHours}h {eta.Minutes:D2}m";

        if (eta.TotalMinutes >= 1)
            return $"{(int)eta.TotalMinutes}m {eta.Seconds:D2}s";

        return $"{Math.Max(1, eta.Seconds)}s";
    }

    private void CancelPanelLoadRequests()
    {
        CancelSnapshotFilesLoadRequest();
        CancelVersionsLoadRequest();
        CancelItemsLoadRequest();
    }

    private (long RequestId, CancellationToken Token) BeginSnapshotFilesLoadRequest()
    {
        CancelSnapshotFilesLoadRequest();

        _snapshotFilesLoadCts = new CancellationTokenSource();
        var requestId = Interlocked.Increment(ref _snapshotFilesLoadRequestId);
        return (requestId, _snapshotFilesLoadCts.Token);
    }

    private bool IsLatestSnapshotFilesLoadRequest(long requestId)
        => requestId == Volatile.Read(ref _snapshotFilesLoadRequestId);

    private void CancelSnapshotFilesLoadRequest()
    {
        if (_snapshotFilesLoadCts is null)
            return;

        try
        {
            _snapshotFilesLoadCts.Cancel();
        }
        catch
        {
        }
        finally
        {
            _snapshotFilesLoadCts.Dispose();
            _snapshotFilesLoadCts = null;
        }
    }

    private (long RequestId, CancellationToken Token) BeginVersionsLoadRequest()
    {
        CancelVersionsLoadRequest();

        _versionsLoadCts = new CancellationTokenSource();
        var requestId = Interlocked.Increment(ref _versionsLoadRequestId);
        return (requestId, _versionsLoadCts.Token);
    }

    private bool IsLatestVersionsLoadRequest(long requestId)
        => requestId == Volatile.Read(ref _versionsLoadRequestId);

    private void CancelVersionsLoadRequest()
    {
        if (_versionsLoadCts is null)
            return;

        try
        {
            _versionsLoadCts.Cancel();
        }
        catch
        {
        }
        finally
        {
            _versionsLoadCts.Dispose();
            _versionsLoadCts = null;
        }
    }

    private (long RequestId, CancellationToken Token) BeginItemsLoadRequest()
    {
        CancelItemsLoadRequest();

        _itemsLoadCts = new CancellationTokenSource();
        var requestId = Interlocked.Increment(ref _itemsLoadRequestId);
        return (requestId, _itemsLoadCts.Token);
    }

    private bool IsLatestItemsLoadRequest(long requestId)
        => requestId == Volatile.Read(ref _itemsLoadRequestId);

    private void CancelItemsLoadRequest()
    {
        if (_itemsLoadCts is null)
            return;

        try
        {
            _itemsLoadCts.Cancel();
        }
        catch
        {
        }
        finally
        {
            _itemsLoadCts.Dispose();
            _itemsLoadCts = null;
        }
    }

    private void StartLiveSync(string rootPath, IReadOnlyCollection<string> linkedFormats)
    {
        _liveSyncRootPath = rootPath?.Trim() ?? string.Empty;
        _liveSyncTrackedFormats.Clear();
        _liveSyncTrackedFormats.AddRange(linkedFormats ?? Array.Empty<string>());
        DisposeLiveSyncInfrastructure();
        IsLiveSyncPaused = false;
        LiveSyncLastIssue = string.Empty;

        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            if (!string.IsNullOrWhiteSpace(_liveSyncRootPath))
                LiveSyncLastIssue = Loc.T("explorer.monitoring_issue_root_missing");

            _ = RefreshLiveSyncIndicatorsAsync();
            return;
        }

        _liveSyncExtensions.Clear();
        foreach (var ext in NormalizeTrackedExtensions(linkedFormats ?? Array.Empty<string>()))
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

            _visualRefreshTimer = new Timer(
                _ => Dispatcher.UIThread.Post(() => ShowItemsForPath(_selectedDirectoryPath)),
                null,
                Timeout.Infinite,
                Timeout.Infinite);

            _lastLiveSyncUtc = DateTime.MinValue;
            IsLiveSyncActive = true;
            _ = RefreshLiveSyncIndicatorsAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to start live sync watcher for {Path}", rootPath);
            LiveSyncLastIssue = UserFacingMessageLocalizer.TryLocalize(ex.Message)?.Trim()
                                ?? Loc.T("explorer.monitoring_issue_root_missing");
            DisposeLiveSyncInfrastructure();
            _ = RefreshLiveSyncIndicatorsAsync();
        }
    }

    private void StopLiveSync()
    {
        _liveSyncRootPath = string.Empty;
        _liveSyncTrackedFormats.Clear();
        IsLiveSyncPaused = false;
        LiveSyncLastIssue = string.Empty;
        _liveSyncQueueStatus = RepositoryFsEventQueueStatus.Empty;
        _lastLiveSyncUtc = DateTime.MinValue;
        DisposeLiveSyncInfrastructure();
        RaiseLiveSyncStateChanged();
    }

    private void PauseLiveSync()
    {
        if (!IsLiveSyncActive)
            return;

        DisposeLiveSyncInfrastructure();
        IsLiveSyncPaused = true;
        _ = RefreshLiveSyncIndicatorsAsync();
    }

    private void ResumeLiveSync()
    {
        if (string.IsNullOrWhiteSpace(_liveSyncRootPath))
            return;

        StartLiveSync(_liveSyncRootPath, _liveSyncTrackedFormats);
    }

    private void DisposeLiveSyncInfrastructure()
    {
        IsLiveSyncActive = false;
        CancelPanelLoadRequests();

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
        _visualRefreshTimer?.Dispose();
        _visualRefreshTimer = null;
        _liveSyncExtensions.Clear();
    }

    private void OnLiveSyncChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldQueueLiveSync(e.FullPath))
            _ = QueueLiveSyncEventAsync(e.FullPath, "changed");
    }

    private void OnLiveSyncRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldQueueLiveSync(e.OldFullPath))
            _ = QueueLiveSyncEventAsync(e.OldFullPath!, "renamed");

        if (ShouldQueueLiveSync(e.FullPath))
            _ = QueueLiveSyncEventAsync(e.FullPath, "renamed");
    }

    private void OnLiveSyncError(object sender, ErrorEventArgs e)
    {
        _log.LogWarning(e.GetException(), "Live sync watcher error for repository {RepositoryId}", RepositoryId);
        Dispatcher.UIThread.Post(() =>
        {
            var message = e.GetException()?.Message;
            LiveSyncLastIssue = string.IsNullOrWhiteSpace(message)
                ? Loc.T("explorer.monitoring_issue_watcher_error")
                : Loc.F("explorer.monitoring_issue_watcher_error_with_reason", message.Trim());
        });
        _ = QueueLiveSyncEventAsync(RepositoryPath, "error");
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

    private async Task QueueLiveSyncEventAsync(string fullPath, string eventKind)
    {
        if (!IsLiveSyncActive || RepositoryId == 0)
            return;

        try
        {
            await _fsEventQueue.EnqueueAsync(RepositoryId, fullPath, eventKind);
            ArmLiveSyncTimer(GetAdaptiveLiveSyncDelayMs(_liveSyncQueueStatus.TotalCount + 1, afterCompletedScan: false));
            ArmVisualRefreshTimer();
            _ = RefreshLiveSyncIndicatorsAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Failed to enqueue live-sync FS event. RepositoryId {RepositoryId}. Path {Path}. Kind {Kind}",
                RepositoryId,
                fullPath,
                eventKind);
            await Dispatcher.UIThread.InvokeAsync(() =>
                LiveSyncLastIssue = Loc.F("explorer.monitoring_issue_queue_error", ex.Message));
        }
    }

    private async Task FlushQueuedLiveSyncAsync(bool allowPaused = false, bool manualTrigger = false)
    {
        if ((!IsLiveSyncActive && !allowPaused) || RepositoryId == 0)
            return;

        if (IsLoading || IsScanRunning)
        {
            if (IsLiveSyncActive)
                ArmLiveSyncTimer(GetAdaptiveLiveSyncDelayMs(_liveSyncQueueStatus.TotalCount, afterCompletedScan: false));
            return;
        }

        var adaptiveMinIntervalMs = GetAdaptiveLiveSyncDelayMs(_liveSyncQueueStatus.TotalCount, afterCompletedScan: true);
        var elapsedMs = (DateTime.UtcNow - _lastLiveSyncUtc).TotalMilliseconds;
        if (elapsedMs < adaptiveMinIntervalMs)
        {
            ArmLiveSyncTimer(adaptiveMinIntervalMs - (int)elapsedMs);
            return;
        }

        RepositoryFsEventLease lease;
        try
        {
            lease = await _fsEventQueue.LeaseAsync(
                RepositoryId,
                maxItems: 256,
                staleRunningAfter: TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to lease durable FS events for repository {RepositoryId}", RepositoryId);
            await Dispatcher.UIThread.InvokeAsync(() =>
                LiveSyncLastIssue = Loc.F("explorer.monitoring_issue_queue_error", ex.Message));

            if (IsLiveSyncActive)
                ArmLiveSyncTimer(GetAdaptiveLiveSyncDelayMs(_liveSyncQueueStatus.TotalCount, afterCompletedScan: false));
            return;
        }

        if (lease.Count == 0)
        {
            await RefreshLiveSyncIndicatorsAsync();
            return;
        }

        _lastLiveSyncUtc = DateTime.UtcNow;

        var incrementalOutcome = await ExecuteIncrementalLiveSyncAsync(lease, _autoCaptureFileVersions);
        var scanOutcome = incrementalOutcome switch
        {
            IncrementalLiveSyncOutcome.Completed => ScanExecutionOutcome.Completed,
            IncrementalLiveSyncOutcome.Deferred => ScanExecutionOutcome.Deferred,
            IncrementalLiveSyncOutcome.Failed => ScanExecutionOutcome.Failed,
            IncrementalLiveSyncOutcome.FallbackToFullScan => await ExecuteScanAsync(
                saveFileVersions: _autoCaptureFileVersions,
                triggerOverride: _autoCaptureFileVersions ? "auto_snapshot_live_watcher" : "sync_live_watcher",
                fallbackMessage: Loc.T("explorer.scan.syncing_file_changes"),
                showErrors: false,
                diagnosticsOriginOverride: "live_sync_fallback",
                successMessage: null),
            _ => ScanExecutionOutcome.Failed
        };

        if (incrementalOutcome == IncrementalLiveSyncOutcome.FallbackToFullScan)
            UpdateLiveSyncFallbackOutcome(scanOutcome);

        if (scanOutcome == ScanExecutionOutcome.Completed)
            await Dispatcher.UIThread.InvokeAsync(() => LiveSyncLastIssue = string.Empty);
        else if (scanOutcome == ScanExecutionOutcome.Failed && manualTrigger)
            await Dispatcher.UIThread.InvokeAsync(() => LiveSyncLastIssue = Loc.T("explorer.monitoring_issue_scan_failed"));

        try
        {
            if (scanOutcome == ScanExecutionOutcome.Completed)
                await _fsEventQueue.CompleteAsync(RepositoryId, lease.ItemIds);
            else if (scanOutcome == ScanExecutionOutcome.Deferred)
                await _fsEventQueue.RequeueAsync(RepositoryId, lease.ItemIds, "live_sync_scan_deferred");
            else
                await _fsEventQueue.RequeueAsync(RepositoryId, lease.ItemIds, "live_sync_scan_failed");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to finalize durable FS event lease for repository {RepositoryId}", RepositoryId);
            await Dispatcher.UIThread.InvokeAsync(() =>
                LiveSyncLastIssue = Loc.F("explorer.monitoring_issue_queue_error", ex.Message));
        }

        await RefreshLiveSyncIndicatorsAsync();

        if (IsLiveSyncActive && await _fsEventQueue.HasPendingAsync(RepositoryId))
            ArmLiveSyncTimer(GetAdaptiveLiveSyncDelayMs(_liveSyncQueueStatus.TotalCount, scanOutcome == ScanExecutionOutcome.Completed));
    }

    private async Task RefreshLiveSyncIndicatorsAsync(CancellationToken ct = default)
    {
        RepositoryFsEventQueueStatus status;
        try
        {
            status = RepositoryId <= 0
                ? RepositoryFsEventQueueStatus.Empty
                : await _fsEventQueue.GetStatusAsync(RepositoryId, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to load live sync queue status for repository {RepositoryId}", RepositoryId);
            status = RepositoryFsEventQueueStatus.Empty;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _liveSyncQueueStatus = status;

            if (!IsLiveSyncActive
                && !IsLiveSyncPaused
                && !string.IsNullOrWhiteSpace(_liveSyncRootPath)
                && !Directory.Exists(_liveSyncRootPath))
            {
                LiveSyncLastIssue = Loc.T("explorer.monitoring_issue_root_missing");
            }
            else if (string.IsNullOrWhiteSpace(LiveSyncLastIssue)
                     && !string.IsNullOrWhiteSpace(status.LastError)
                     && !IsBenignLiveSyncQueueError(status.LastError))
            {
                LiveSyncLastIssue = Loc.F("explorer.monitoring_issue_queue_error", status.LastError);
            }

            RaiseLiveSyncStateChanged();
        });
    }

    private void ArmLiveSyncTimer(int dueMs)
    {
        var delay = Math.Max(100, dueMs);
        lock (_liveSyncLock)
        {
            try
            {
                _liveSyncTimer?.Change(delay, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void ArmVisualRefreshTimer()
    {
        lock (_liveSyncLock)
        {
            try
            {
                _visualRefreshTimer?.Change(VisualRefreshDebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static bool IsBenignLiveSyncQueueError(string? error)
        => string.Equals(error, "live_sync_scan_deferred", StringComparison.OrdinalIgnoreCase)
           || string.Equals(error, "live_sync_scan_in_progress", StringComparison.OrdinalIgnoreCase);

    private static int GetAdaptiveLiveSyncDelayMs(int queueCount, bool afterCompletedScan)
    {
        var baseDelay = afterCompletedScan ? LiveSyncMinIntervalMs : LiveSyncDebounceMs;

        if (queueCount >= LiveSyncHighPressureQueueThreshold)
            return Math.Max(baseDelay, LiveSyncHighPressureDebounceMs);

        if (queueCount >= LiveSyncBurstQueueThreshold)
            return Math.Max(baseDelay, LiveSyncBurstDebounceMs);

        return baseDelay;
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

    private async Task BuildTreeAsync()
    {
        var expandedPaths = _nodeByPath
            .Where(x => x.Value.IsExpanded)
            .Select(x => NormalizeRelativePath(x.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = _entries;
        var includeUntrackedDirectories = ShowAllRepositoryFiles;
        var repositoryRoot = RepositoryPath;
        var directoryIndex = await Task.Run(() =>
            CreateDirectoryIndex(BuildDirectoryEntriesForTree(entries, repositoryRoot, includeUntrackedDirectories)));

        _directoryByPath.Clear();
        foreach (var (path, directory) in directoryIndex.DirectoriesByPath)
            _directoryByPath[path] = directory;

        _directoryChildrenByParent.Clear();
        foreach (var (parentPath, children) in directoryIndex.ChildrenByParent)
            _directoryChildrenByParent[parentPath] = children;

        _nodeByPath.Clear();
        TreeNodes.Clear();

        var root = CreateTreeNodeCore(string.Empty, RepositoryName);
        root.IsExpanded = true;
        EnsureTreeChildrenLoaded(root);
        TreeNodes.Add(root);

        foreach (var path in expandedPaths.OrderBy(path => path.Count(ch => ch == '/')))
        {
            EnsureTreePathMaterialized(path);

            if (_nodeByPath.TryGetValue(path, out var expandedNode))
                expandedNode.IsExpanded = true;
        }
    }

    private void OnTreeNodeExpansionChanged(ExplorerTreeNodeViewModel node, bool isExpanded)
    {
        if (isExpanded)
            EnsureTreeChildrenLoaded(node);
    }

    private void EnsureTreePathMaterialized(string? relativePath)
    {
        var normalized = NormalizeTreePathKey(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (!_nodeByPath.TryGetValue(string.Empty, out var currentNode))
            return;

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = string.Empty;

        foreach (var segment in segments)
        {
            currentPath = string.IsNullOrWhiteSpace(currentPath)
                ? segment
                : $"{currentPath}/{segment}";

            EnsureTreeChildrenLoaded(currentNode);
            if (!_nodeByPath.TryGetValue(currentPath, out currentNode))
                return;
        }
    }

    private void EnsureTreeChildrenLoaded(ExplorerTreeNodeViewModel node)
    {
        if (node.IsPlaceholder)
            return;

        var pathKey = NormalizeTreePathKey(node.RelativePath);
        var shouldLoad = node.HasUnrealizedChildren
                         || (string.IsNullOrWhiteSpace(pathKey) && node.Children.Count == 0);
        if (!shouldLoad)
            return;

        node.Children.Clear();

        if (!_directoryChildrenByParent.TryGetValue(pathKey, out var children) || children.Count == 0)
        {
            node.HasUnrealizedChildren = false;
            return;
        }

        foreach (var child in children)
            node.Children.Add(CreateTreeNode(child));

        node.HasUnrealizedChildren = false;
    }

    private ExplorerTreeNodeViewModel CreateTreeNode(RepositoryScanEntryDto directory)
    {
        var pathKey = NormalizeTreePathKey(directory.RelativePath);
        if (_nodeByPath.TryGetValue(pathKey, out var existing))
            return existing;

        return CreateTreeNodeCore(directory.RelativePath, directory.Name);
    }

    private ExplorerTreeNodeViewModel CreateTreeNodeCore(string relativePath, string name)
    {
        var pathKey = NormalizeTreePathKey(relativePath);
        if (_nodeByPath.TryGetValue(pathKey, out var existing))
            return existing;

        var hasChildren = _directoryChildrenByParent.TryGetValue(pathKey, out var children)
                          && children.Count > 0;

        var node = new ExplorerTreeNodeViewModel
        {
            RelativePath = relativePath,
            Name = name,
            HasUnrealizedChildren = hasChildren,
            ExpansionChanged = OnTreeNodeExpansionChanged
        };

        if (hasChildren)
            node.Children.Add(CreatePlaceholderNode());

        _nodeByPath[pathKey] = node;
        return node;
    }

    private static ExplorerTreeNodeViewModel CreatePlaceholderNode()
    {
        return new ExplorerTreeNodeViewModel
        {
            RelativePath = "__placeholder__",
            Name = string.Empty,
            IsPlaceholder = true
        };
    }

    private static IReadOnlyList<RepositoryScanEntryDto> BuildDirectoryEntriesForTree(
        IReadOnlyList<RepositoryScanEntryDto> entries,
        string repositoryRoot,
        bool includeUntrackedDirectories)
    {
        var directoriesByPath = entries
            .Where(e => e.IsDirectory && !ShouldHideFromExplorer(e.RelativePath))
            .GroupBy(e => NormalizeTreePathKey(e.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (!includeUntrackedDirectories || string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return directoriesByPath.Values.ToList();

        var root = Path.GetFullPath(repositoryRoot);
        foreach (var directoryPath in SafeEnumerateDirectories(root, SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, directoryPath));
            if (string.IsNullOrWhiteSpace(relativePath) || ShouldHideFromExplorer(relativePath))
                continue;

            directoriesByPath.TryAdd(relativePath, CreateFileSystemEntry(new DirectoryInfo(directoryPath), root));
        }

        return directoriesByPath.Values.ToList();
    }

    private static DirectoryIndexResult CreateDirectoryIndex(IEnumerable<RepositoryScanEntryDto> directories)
    {
        var directoriesByPath = new Dictionary<string, RepositoryScanEntryDto>(StringComparer.OrdinalIgnoreCase);
        var groupedChildren = new Dictionary<string, List<RepositoryScanEntryDto>>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var pathKey = NormalizeTreePathKey(directory.RelativePath);
            directoriesByPath[pathKey] = directory;

            var parentKey = NormalizeTreePathKey(directory.ParentRelativePath);
            if (!groupedChildren.TryGetValue(parentKey, out var bucket))
            {
                bucket = [];
                groupedChildren[parentKey] = bucket;
            }

            bucket.Add(directory);
        }

        var childrenByParent = groupedChildren.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<RepositoryScanEntryDto>)pair.Value
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        return new DirectoryIndexResult(directoriesByPath, childrenByParent);
    }

    private sealed record DirectoryIndexResult(
        IReadOnlyDictionary<string, RepositoryScanEntryDto> DirectoriesByPath,
        IReadOnlyDictionary<string, IReadOnlyList<RepositoryScanEntryDto>> ChildrenByParent);

    private void UpdateTreeSelectionState(ExplorerTreeNodeViewModel? selectedNode)
    {
        var selectedPath = NormalizeRelativePath(selectedNode?.RelativePath ?? string.Empty);

        foreach (var (path, node) in _nodeByPath)
            node.IsSelected = string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyExplorerFiltersIfNeeded()
    {
        if (_suppressExplorerFilterApply)
            return;

        _ = ShowItemsForPathAsync(_selectedDirectoryPath);
    }

    private void ApplySnapshotHistoryFiltersIfNeeded()
    {
        if (_suppressSnapshotFilterApply)
            return;

        ApplySnapshotHistoryFilters();
    }

    private void ApplySnapshotFilesFiltersIfNeeded()
    {
        if (_suppressSnapshotFilterApply)
            return;

        ApplySnapshotFilesFilters();
    }

    private void RebuildSnapshotTriggerFilters()
    {
        var selected = NormalizeSnapshotTriggerFilter(SelectedSnapshotTriggerFilter);
        var triggers = _snapshotHistorySource
            .Select(x => NormalizeSnapshotTriggerFilter(x.Trigger))
            .Where(x => x != "all")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SnapshotTriggerFilters.Clear();
        SnapshotTriggerFilters.Add("all");
        foreach (var trigger in triggers)
            SnapshotTriggerFilters.Add(trigger);

        _suppressSnapshotFilterApply = true;
        try
        {
            SelectedSnapshotTriggerFilter = SnapshotTriggerFilters.Contains(selected, StringComparer.OrdinalIgnoreCase)
                ? selected
                : "all";
        }
        finally
        {
            _suppressSnapshotFilterApply = false;
        }
    }

    private void RebuildSnapshotKindFilters()
    {
        var selected = NormalizeSnapshotKindFilter(SelectedSnapshotKindFilter);
        var kinds = _snapshotHistorySource
            .Select(x => NormalizeSnapshotKindFilter(x.Kind))
            .Where(x => x != "all")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SnapshotKindFilters.Clear();
        SnapshotKindFilters.Add("all");
        foreach (var kind in kinds)
            SnapshotKindFilters.Add(kind);

        _suppressSnapshotFilterApply = true;
        try
        {
            SelectedSnapshotKindFilter = SnapshotKindFilters.Contains(selected, StringComparer.OrdinalIgnoreCase)
                ? selected
                : "all";
        }
        finally
        {
            _suppressSnapshotFilterApply = false;
        }
    }

    private void RebuildSnapshotTagFilters()
    {
        var selected = NormalizeSnapshotTagFilter(SelectedSnapshotTagFilter);
        var tags = _snapshotHistorySource
            .SelectMany(x => x.Tags)
            .Select(NormalizeSnapshotTagFilter)
            .Where(x => x != "all")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SnapshotTagFilters.Clear();
        SnapshotTagFilters.Add("all");
        foreach (var tag in tags)
            SnapshotTagFilters.Add(tag);

        _suppressSnapshotFilterApply = true;
        try
        {
            SelectedSnapshotTagFilter = SnapshotTagFilters.Contains(selected, StringComparer.OrdinalIgnoreCase)
                ? selected
                : "all";
        }
        finally
        {
            _suppressSnapshotFilterApply = false;
        }
    }

    private void RebuildSnapshotFileExtensionFilters()
    {
        var selected = NormalizeSnapshotFileExtensionFilter(SelectedSnapshotFileExtensionFilter);
        var ext = _snapshotFilesSource
            .Select(x => NormalizeSnapshotFileExtensionFilter(Path.GetExtension(x.RelativePath)))
            .Where(x => x != "all")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SnapshotFileExtensionFilters.Clear();
        SnapshotFileExtensionFilters.Add("all");
        foreach (var value in ext)
            SnapshotFileExtensionFilters.Add(value);

        _suppressSnapshotFilterApply = true;
        try
        {
            SelectedSnapshotFileExtensionFilter = EnsureSnapshotFileExtensionFilterOption(selected);
        }
        finally
        {
            _suppressSnapshotFilterApply = false;
        }
    }

    private string EnsureSnapshotFileExtensionFilterOption(string value)
    {
        if (!SnapshotFileExtensionFilters.Contains(value, StringComparer.OrdinalIgnoreCase))
            SnapshotFileExtensionFilters.Add(value);

        return value;
    }

    private void ApplySnapshotHistoryFilters(long preferredSnapshotId = 0, bool forceSnapshotFilesReload = false)
    {
        var directives = ParseSnapshotHistoryDirectives(SnapshotHistorySearchQuery.Trim());
        var textQuery = directives.TextQuery;

        var kindFilter = NormalizeSnapshotKindFilter(SelectedSnapshotKindFilter);
        if (kindFilter == "all" && !string.IsNullOrWhiteSpace(directives.KindFilter))
            kindFilter = directives.KindFilter;

        var triggerFilter = NormalizeSnapshotTriggerFilter(SelectedSnapshotTriggerFilter);
        if (triggerFilter == "all" && !string.IsNullOrWhiteSpace(directives.TriggerFilter))
            triggerFilter = directives.TriggerFilter;

        var snapshotTagFilter = NormalizeSnapshotTagFilter(SelectedSnapshotTagFilter);
        if (snapshotTagFilter == "all" && !string.IsNullOrWhiteSpace(directives.TagFilter))
            snapshotTagFilter = directives.TagFilter;

        var minChangedFiles = ParseNullableInt(SnapshotHistoryMinChangedFiles) ?? directives.MinChangedFiles;
        if (minChangedFiles < 0)
            minChangedFiles = null;

        IEnumerable<RepositorySnapshotHistoryEntryViewModel> filtered = _snapshotHistorySource;

        if (!string.IsNullOrWhiteSpace(textQuery))
        {
            filtered = filtered.Where(x =>
                x.DisplayTitle.Contains(textQuery, StringComparison.OrdinalIgnoreCase) ||
                x.Kind.Contains(textQuery, StringComparison.OrdinalIgnoreCase) ||
                x.KindLabel.Contains(textQuery, StringComparison.OrdinalIgnoreCase) ||
                x.Trigger.Contains(textQuery, StringComparison.OrdinalIgnoreCase) ||
                x.Tags.Any(tag => tag.Contains(textQuery, StringComparison.OrdinalIgnoreCase)) ||
                x.DisplayTime.Contains(textQuery, StringComparison.OrdinalIgnoreCase));
        }

        if (kindFilter != "all")
            filtered = filtered.Where(x => string.Equals(NormalizeSnapshotKindFilter(x.Kind), kindFilter, StringComparison.OrdinalIgnoreCase));

        if (triggerFilter != "all")
            filtered = filtered.Where(x => string.Equals(NormalizeSnapshotTriggerFilter(x.Trigger), triggerFilter, StringComparison.OrdinalIgnoreCase));

        if (snapshotTagFilter != "all")
            filtered = filtered.Where(x => x.Tags.Any(tag => string.Equals(NormalizeSnapshotTagFilter(tag), snapshotTagFilter, StringComparison.OrdinalIgnoreCase)));

        if (minChangedFiles is > 0)
            filtered = filtered.Where(x => x.ChangedFilesCount >= minChangedFiles.Value);

        var rows = filtered
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .ToList();

        ReplaceCollectionItems(SnapshotHistory, rows, AreReferenceItemsEquivalent);

        HasSnapshotHistory = SnapshotHistory.Count > 0;

        var targetSnapshotId = preferredSnapshotId > 0
            ? preferredSnapshotId
            : _preferredSnapshotId ?? SelectedSnapshot?.SnapshotId ?? 0;
        var previousSelectedSnapshotId = SelectedSnapshot?.SnapshotId ?? 0;

        var selected = targetSnapshotId > 0
            ? SnapshotHistory.FirstOrDefault(x => x.SnapshotId == targetSnapshotId)
            : null;

        if (selected is null && SnapshotHistory.Count > 0)
            selected = SnapshotHistory[0];

        _suppressSnapshotSelectionLoad = true;
        try
        {
            SelectedSnapshot = selected;
            _preferredSnapshotId = selected?.SnapshotId;
        }
        finally
        {
            _suppressSnapshotSelectionLoad = false;
        }

        if (selected is null)
        {
            CancelSnapshotFilesLoadRequest();
            ClearSnapshotFilesState();
            _preferredSnapshotFileRelativePath = null;
            _preferredSnapshotFileVersionId = null;
        }
        else if (forceSnapshotFilesReload || selected.SnapshotId != previousSelectedSnapshotId || _snapshotFilesSource.Count == 0)
        {
            _ = LoadSnapshotFilesForSelectedSnapshotAsync(selected.SnapshotId);
        }
    }

    private void ApplySnapshotFilesFilters(string? preferredFilePath = null)
    {
        var directives = ParseSnapshotFilesDirectives(SnapshotFilesSearchQuery.Trim());
        var textQuery = directives.TextQuery;

        var kindFilter = NormalizeSnapshotFileChangeKindFilter(SelectedSnapshotFileChangeKindFilter);
        if (kindFilter == "all" && !string.IsNullOrWhiteSpace(directives.ChangeKindFilter))
            kindFilter = directives.ChangeKindFilter;

        var extensionFilter = NormalizeSnapshotFileExtensionFilter(SelectedSnapshotFileExtensionFilter);
        if (extensionFilter == "all" && !string.IsNullOrWhiteSpace(directives.ExtensionFilter))
            extensionFilter = EnsureSnapshotFileExtensionFilterOption(directives.ExtensionFilter);

        var minDeltaKb = ParseNullableDouble(SnapshotFilesMinSizeDeltaKb) ?? directives.MinSizeDeltaKb;

        IEnumerable<RepositorySnapshotFileChangeViewModel> filtered = _snapshotFilesSource;

        if (!string.IsNullOrWhiteSpace(textQuery))
        {
            filtered = filtered.Where(x =>
                x.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase) ||
                x.RelativePath.Contains(textQuery, StringComparison.OrdinalIgnoreCase));
        }

        if (kindFilter != "all")
            filtered = filtered.Where(x => string.Equals(NormalizeSnapshotFileChangeKindFilter(x.ChangeKind), kindFilter, StringComparison.OrdinalIgnoreCase));

        if (extensionFilter != "all")
        {
            filtered = filtered.Where(x =>
                string.Equals(
                    NormalizeSnapshotFileExtensionFilter(Path.GetExtension(x.RelativePath)),
                    extensionFilter,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (minDeltaKb is > 0)
        {
            filtered = filtered.Where(x =>
            {
                var deltaBytes = Math.Abs(x.CurrentSizeBytes - x.PreviousSizeBytes);
                return (deltaBytes / 1024d) >= minDeltaKb.Value;
            });
        }

        var rows = filtered
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ReplaceCollectionItems(SnapshotFiles, rows, AreReferenceItemsEquivalent);

        HasSnapshotFiles = SnapshotFiles.Count > 0;

        if (HasSnapshotFiles)
        {
            var selected = !string.IsNullOrWhiteSpace(preferredFilePath)
                ? SnapshotFiles.FirstOrDefault(x => x.RelativePath.Equals(preferredFilePath, StringComparison.OrdinalIgnoreCase))
                : null;

            selected ??= !string.IsNullOrWhiteSpace(_preferredSnapshotFileRelativePath)
                ? SnapshotFiles.FirstOrDefault(x => x.RelativePath.Equals(_preferredSnapshotFileRelativePath, StringComparison.OrdinalIgnoreCase))
                : null;

            selected ??= SnapshotFiles[0];
            SelectedSnapshotFile = selected;
            _preferredSnapshotFileRelativePath = selected.RelativePath;
            _preferredSnapshotFileVersionId = selected.FileVersionId;
        }
        else
        {
            _isClearingSnapshotFileSelection = true;
            SelectedSnapshotFile = null;
            _isClearingSnapshotFileSelection = false;
            _preferredSnapshotFileRelativePath = null;
            _preferredSnapshotFileVersionId = null;
        }
    }

    private void ClearSnapshotFilesState()
    {
        _snapshotFilesSource.Clear();
        SnapshotFiles.Clear();
        _isClearingSnapshotFileSelection = true;
        SelectedSnapshotFile = null;
        _isClearingSnapshotFileSelection = false;
        HasSnapshotFiles = false;
    }

    private async Task LoadSavedExplorerFiltersAsync(CancellationToken ct = default)
    {
        var presets = await _filterStore.LoadAsync(ct);

        SavedExplorerFilters.Clear();
        foreach (var preset in presets)
            SavedExplorerFilters.Add(new RepositoryExplorerSavedFilterViewModel(preset));

        SortSavedExplorerFilters();
        HasExplorerSavedFilters = SavedExplorerFilters.Count > 0;
    }

    private async Task PersistSavedExplorerFiltersAsync(CancellationToken ct = default)
    {
        var presets = SavedExplorerFilters
            .Select(v => v.Preset)
            .ToList();

        await _filterStore.SaveAsync(presets, ct);
    }

    private RepositoryExplorerFilterPreset BuildCurrentExplorerPreset(string name)
    {
        return new RepositoryExplorerFilterPreset
        {
            Name = name,
            SearchQuery = SearchQuery,
            EntryTypeFilter = NormalizeEntryTypeFilter(SelectedEntryTypeFilter),
            ExtensionFilter = NormalizeExtensionFilter(SelectedExtensionFilter),
            ModifiedWindowFilter = NormalizeModifiedWindowFilter(SelectedModifiedWindowFilter),
            MinSizeMb = MinSizeMb,
            MaxSizeMb = MaxSizeMb,
            SavedAtUtc = DateTime.UtcNow
        };
    }

    private void SortSavedExplorerFilters()
    {
        var sorted = SavedExplorerFilters
            .OrderByDescending(f => f.SavedAtUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SavedExplorerFilters.Clear();
        foreach (var item in sorted)
            SavedExplorerFilters.Add(item);
    }

    private void ApplyExplorerPreset(RepositoryExplorerFilterPreset preset)
    {
        _suppressExplorerFilterApply = true;
        try
        {
            SearchQuery = preset.SearchQuery;
            SelectedEntryTypeFilter = NormalizeEntryTypeFilter(preset.EntryTypeFilter);
            SelectedExtensionFilter = EnsureExtensionFilterOption(NormalizeExtensionFilter(preset.ExtensionFilter));
            SelectedModifiedWindowFilter = NormalizeModifiedWindowFilter(preset.ModifiedWindowFilter);
            MinSizeMb = preset.MinSizeMb;
            MaxSizeMb = preset.MaxSizeMb;
            SavedExplorerFilterName = preset.Name;
        }
        finally
        {
            _suppressExplorerFilterApply = false;
        }

        ShowItemsForPath(_selectedDirectoryPath);
    }

    private void RebuildExtensionFilters()
    {
        var selected = NormalizeExtensionFilter(SelectedExtensionFilter);

        var extensions = _entries
            .Where(e => !e.IsDirectory && !string.IsNullOrWhiteSpace(e.Extension))
            .Select(e => NormalizeExtensionFilter(e.Extension ?? string.Empty))
            .Where(e => e != "all")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ExtensionFilters.Clear();
        ExtensionFilters.Add("all");
        foreach (var ext in extensions)
            ExtensionFilters.Add(ext);

        SelectedExtensionFilter = EnsureExtensionFilterOption(selected);
    }

    private string EnsureExtensionFilterOption(string value)
    {
        if (!ExtensionFilters.Contains(value, StringComparer.OrdinalIgnoreCase))
            ExtensionFilters.Add(value);

        return value;
    }

    private void ShowItemsForPath(string? directoryRelativePath)
        => _ = ShowItemsForPathAsync(directoryRelativePath);

    private static IReadOnlyList<ExplorerEntryRow> BuildExplorerRowsForDirectory(
        IReadOnlyList<RepositoryScanEntryDto> entries,
        string repositoryRoot,
        string? directoryRelativePath,
        bool includeUntrackedFiles,
        IReadOnlyCollection<string> trackedExtensions)
    {
        var normalizedDirectoryPath = NormalizeParent(directoryRelativePath);
        var rowsByPath = entries
            .Where(e =>
                !ShouldHideFromExplorer(e.RelativePath) &&
                string.Equals(NormalizeParent(e.ParentRelativePath), normalizedDirectoryPath, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => NormalizeRelativePath(e.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var entry = group.First();
                    return new ExplorerEntryRow(entry, IsTracked: true, ResolveTrackedEntryStatus(repositoryRoot, entry));
                },
                StringComparer.OrdinalIgnoreCase);

        if (!includeUntrackedFiles || string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return rowsByPath.Values.ToList();

        var root = Path.GetFullPath(repositoryRoot);
        var directoryFullPath = BuildFullPathWithinRoot(root, normalizedDirectoryPath);
        if (string.IsNullOrWhiteSpace(directoryFullPath) || !Directory.Exists(directoryFullPath))
            return rowsByPath.Values.ToList();

        foreach (var fileSystemInfo in SafeEnumerateFileSystemInfos(directoryFullPath))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, fileSystemInfo.FullName));
            if (string.IsNullOrWhiteSpace(relativePath) || ShouldHideFromExplorer(relativePath))
                continue;

            rowsByPath.TryAdd(relativePath, new ExplorerEntryRow(
                CreateFileSystemEntry(fileSystemInfo, root),
                IsTracked: false,
                StatusKind: ResolveLocalOnlyEntryStatus(fileSystemInfo, trackedExtensions)));
        }

        return rowsByPath.Values.ToList();
    }

    private static string ResolveLocalOnlyEntryStatus(
        FileSystemInfo fileSystemInfo,
        IReadOnlyCollection<string> trackedExtensions)
    {
        if (fileSystemInfo is DirectoryInfo)
            return "untracked";

        if (trackedExtensions.Count == 0)
            return "new";

        var extension = fileSystemInfo.Extension;
        if (string.IsNullOrWhiteSpace(extension))
            return "untracked";

        var normalized = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        return trackedExtensions.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? "new"
            : "untracked";
    }

    private static string? BuildFullPathWithinRoot(string repositoryRoot, string? relativePath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var normalizedRelative = (relativePath ?? string.Empty)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    private static RepositoryScanEntryDto CreateFileSystemEntry(FileSystemInfo fileSystemInfo, string repositoryRoot)
    {
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, fileSystemInfo.FullName));
        var isDirectory = fileSystemInfo is DirectoryInfo;
        var sizeBytes = fileSystemInfo is FileInfo fileInfo ? fileInfo.Length : 0L;

        return new RepositoryScanEntryDto(
            relativePath,
            GetParentRelativePath(relativePath),
            fileSystemInfo.Name,
            isDirectory,
            isDirectory ? null : fileSystemInfo.Extension,
            sizeBytes,
            fileSystemInfo.LastWriteTimeUtc,
            ContentHashSha256: null);
    }

    private static string ResolveTrackedEntryStatus(string repositoryRoot, RepositoryScanEntryDto entry)
    {
        if (entry.IsDirectory || string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return "tracked";

        var fullPath = BuildFullPathWithinRoot(repositoryRoot, entry.RelativePath);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return "deleted";

        try
        {
            var info = new FileInfo(fullPath);
            var lastWriteDelta = (info.LastWriteTimeUtc - entry.LastWriteUtc).Duration();
            return info.Length != entry.SizeBytes || lastWriteDelta > TimeSpan.FromSeconds(2)
                ? "modified"
                : "tracked";
        }
        catch
        {
            return "tracked";
        }
    }

    private static bool ShouldHideFromExplorer(string? relativePath)
        => RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(relativePath);

    private static IEnumerable<FileSystemInfo> SafeEnumerateFileSystemInfos(string directoryFullPath)
    {
        try
        {
            return new DirectoryInfo(directoryFullPath).EnumerateFileSystemInfos().ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateDirectories(root, "*", searchOption).ToList();
        }
        catch
        {
            return [];
        }
    }

    [RelayCommand]
    private void LoadMoreExplorerItems()
    {
        if (!HasMoreExplorerItems)
            return;

        _visibleExplorerItemLimit = Math.Min(
            FilteredExplorerItemCount,
            Math.Max(_visibleExplorerItemLimit, VisibleExplorerItemCount) + ExplorerItemsWindowStep);

        RebuildVisibleExplorerItems();
    }

    [RelayCommand]
    private void ShowAllExplorerItems()
    {
        if (FilteredExplorerItemCount <= 0)
            return;

        _visibleExplorerItemLimit = FilteredExplorerItemCount;
        RebuildVisibleExplorerItems();
    }

    private async Task ShowItemsForPathAsync(string? directoryRelativePath, bool debounce = true)
    {
        var normalizedDirectoryPath = NormalizeParent(directoryRelativePath);
        var directives = ParseSearchDirectives((SearchQuery ?? string.Empty).Trim());

        var textQuery = directives.TextQuery;
        var entryTypeFilter = NormalizeEntryTypeFilter(SelectedEntryTypeFilter);
        if (entryTypeFilter == "all" && !string.IsNullOrWhiteSpace(directives.EntryTypeFilter))
            entryTypeFilter = directives.EntryTypeFilter;

        var extensionFilter = NormalizeExtensionFilter(SelectedExtensionFilter);
        if (extensionFilter == "all" && !string.IsNullOrWhiteSpace(directives.ExtensionFilter))
            extensionFilter = EnsureExtensionFilterOption(directives.ExtensionFilter);

        var modifiedWindowFilter = NormalizeModifiedWindowFilter(SelectedModifiedWindowFilter);
        if (modifiedWindowFilter == "all" && !string.IsNullOrWhiteSpace(directives.ModifiedWindowFilter))
            modifiedWindowFilter = directives.ModifiedWindowFilter;

        var minSizeMb = ParseNullableDouble(MinSizeMb) ?? directives.MinSizeMb;
        var maxSizeMb = ParseNullableDouble(MaxSizeMb) ?? directives.MaxSizeMb;
        var entries = _entries;
        var includeUntrackedFiles = ShowAllRepositoryFiles;
        var repositoryRoot = RepositoryPath;
        var trackedExtensions = NormalizeTrackedExtensions(_repositoryDetail?.LinkedFormats ?? _liveSyncTrackedFormats);
        var (requestId, ct) = BeginItemsLoadRequest();

        try
        {
            if (debounce)
                await Task.Delay(ExplorerFilterDebounceMs, ct);

            var rows = await Task.Run(() =>
            {
                IEnumerable<ExplorerEntryRow> visible = BuildExplorerRowsForDirectory(
                    entries,
                    repositoryRoot,
                    normalizedDirectoryPath,
                    includeUntrackedFiles,
                    trackedExtensions);

                if (minSizeMb is > 0 && maxSizeMb is > 0 && minSizeMb > maxSizeMb)
                    (minSizeMb, maxSizeMb) = (maxSizeMb, minSizeMb);

                if (!string.IsNullOrWhiteSpace(textQuery))
                {
                    visible = visible.Where(row =>
                        row.Entry.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase) ||
                        row.Entry.RelativePath.Contains(textQuery, StringComparison.OrdinalIgnoreCase));
                }

                if (entryTypeFilter != "all")
                {
                    visible = entryTypeFilter switch
                    {
                        "files" => visible.Where(row => !row.Entry.IsDirectory),
                        "folders" => visible.Where(row => row.Entry.IsDirectory),
                        _ => visible
                    };
                }

                if (extensionFilter != "all")
                {
                    visible = visible.Where(row =>
                        !row.Entry.IsDirectory &&
                        string.Equals(NormalizeExtensionFilter(row.Entry.Extension ?? string.Empty), extensionFilter, StringComparison.OrdinalIgnoreCase));
                }

                if (modifiedWindowFilter != "all")
                {
                    var threshold = modifiedWindowFilter switch
                    {
                        "24h" => DateTime.UtcNow.AddHours(-24),
                        "7d" => DateTime.UtcNow.AddDays(-7),
                        "30d" => DateTime.UtcNow.AddDays(-30),
                        _ => DateTime.MinValue
                    };

                    if (threshold > DateTime.MinValue)
                        visible = visible.Where(row => ResolveEntryActivityUtc(row.Entry) >= threshold);
                }

                if (minSizeMb is > 0)
                {
                    visible = visible.Where(row =>
                        !row.Entry.IsDirectory &&
                        (row.Entry.SizeBytes / (1024d * 1024d)) >= minSizeMb.Value);
                }

                if (maxSizeMb is > 0)
                {
                    visible = visible.Where(row =>
                        !row.Entry.IsDirectory &&
                        (row.Entry.SizeBytes / (1024d * 1024d)) <= maxSizeMb.Value);
                }

                return visible
                    .OrderByDescending(row => row.Entry.IsDirectory)
                    .ThenBy(row => row.Entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }, ct);

            if (!IsLatestItemsLoadRequest(requestId) || ct.IsCancellationRequested)
                return;

            _filteredExplorerEntries = rows;
            FilteredExplorerItemCount = rows.Count;
            _visibleExplorerItemLimit = Math.Min(rows.Count, ExplorerItemsInitialWindowSize);
            RebuildVisibleExplorerItems();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh explorer items for repository {RepositoryId}", RepositoryId);
        }
    }

    private int GetActiveExplorerFilterCount()
    {
        var count = 0;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
            count++;
        if (!string.Equals(SelectedEntryTypeFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.Equals(SelectedExtensionFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.Equals(SelectedModifiedWindowFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.IsNullOrWhiteSpace(MinSizeMb))
            count++;
        if (!string.IsNullOrWhiteSpace(MaxSizeMb))
            count++;

        return count;
    }

    private void NotifyExplorerChromeStateChanged()
    {
        OnPropertyChanged(nameof(HasActiveExplorerFilters));
        OnPropertyChanged(nameof(ExplorerFilterButtonLabel));
        OnPropertyChanged(nameof(ExplorerResultsSummary));
        OnPropertyChanged(nameof(HasMoreExplorerItems));
        OnPropertyChanged(nameof(ExplorerLoadMoreLabel));
        OnPropertyChanged(nameof(ExplorerWindowHintText));
        OnPropertyChanged(nameof(ShowBlockingOverlay));
        OnPropertyChanged(nameof(BlockingOverlayTitle));
        OnPropertyChanged(nameof(BlockingOverlayDetail));
    }

    private void RebuildVisibleExplorerItems()
    {
        var visibleCount = Math.Min(_visibleExplorerItemLimit, _filteredExplorerEntries.Count);
        var rows = _filteredExplorerEntries
            .Take(visibleCount)
            .Select(MapToItem)
            .ToList();

        ReplaceExplorerItems(rows);
        VisibleExplorerItemCount = visibleCount;
        IsEmpty = visibleCount == 0;
        NotifyExplorerChromeStateChanged();
    }

    private void EnsureExplorerItemVisible(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || _filteredExplorerEntries.Count == 0)
            return;

        var index = -1;
        for (var i = 0; i < _filteredExplorerEntries.Count; i++)
        {
            if (_filteredExplorerEntries[i].Entry.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;

        var requiredVisibleCount = index + 1;
        if (requiredVisibleCount <= VisibleExplorerItemCount)
            return;

        _visibleExplorerItemLimit = Math.Min(
            _filteredExplorerEntries.Count,
            Math.Max(requiredVisibleCount, Math.Max(_visibleExplorerItemLimit, VisibleExplorerItemCount)));

        RebuildVisibleExplorerItems();
    }

    private void ReplaceExplorerItems(IReadOnlyList<ExplorerItemViewModel> rows)
    {
        ReplaceCollectionItems(Items, rows, AreExplorerItemsEquivalent);
    }

    private static bool AreExplorerItemsEquivalent(
        ExplorerItemViewModel left,
        ExplorerItemViewModel right)
    {
        return left.IsDirectory == right.IsDirectory
            && left.IsTracked == right.IsTracked
            && left.RelativePath.Equals(right.RelativePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
            && string.Equals(left.SizeDisplay, right.SizeDisplay, StringComparison.Ordinal)
            && string.Equals(left.ModifiedDisplay, right.ModifiedDisplay, StringComparison.Ordinal)
            && string.Equals(left.HashSha256, right.HashSha256, StringComparison.Ordinal)
            && string.Equals(left.StatusText, right.StatusText, StringComparison.Ordinal)
            && string.Equals(left.StatusKind, right.StatusKind, StringComparison.Ordinal)
            && string.Equals(left.StatusGlyph, right.StatusGlyph, StringComparison.Ordinal)
            && string.Equals(left.TooltipText, right.TooltipText, StringComparison.Ordinal)
            && Math.Abs(left.RowOpacity - right.RowOpacity) < 0.001d;
    }

    private async Task RunTransientPreparationAsync(
        string titleKey,
        string detailKey,
        Func<Task> action,
        Action<Exception> onError)
    {
        if (IsTransientActionBusy)
            return;

        try
        {
            IsTransientActionBusy = true;
            TransientActionTitle = Loc.T(titleKey);
            TransientActionDetail = Loc.T(detailKey);
            await Task.Yield();
            await action();
        }
        catch (Exception ex)
        {
            onError(ex);
        }
        finally
        {
            IsTransientActionBusy = false;
            TransientActionTitle = string.Empty;
            TransientActionDetail = string.Empty;
        }
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
            return Loc.T("explorer.type.file");

        return ext.TrimStart('.').ToUpperInvariant();
    }
    private static ExplorerItemViewModel MapToItem(ExplorerEntryRow row)
    {
        var entry = row.Entry;
        var type = entry.IsDirectory
            ? Loc.T("explorer.type.folder")
            : string.IsNullOrWhiteSpace(entry.Extension)
                ? Loc.T("explorer.type.file")
                : entry.Extension.TrimStart('.').ToUpperInvariant();
        var statusText = LocalizeExplorerFileStatus(row.StatusKind);
        var (statusBackground, statusForeground) = ResolveExplorerStatusColors(row.StatusKind);
        var statusGlyph = ResolveExplorerStatusGlyph(row.StatusKind);

        return new ExplorerItemViewModel
        {
            RelativePath = entry.RelativePath,
            ParentRelativePath = entry.ParentRelativePath,
            IsDirectory = entry.IsDirectory,
            IsTracked = row.IsTracked,
            Name = entry.Name,
            Type = type,
            SizeDisplay = entry.IsDirectory ? "-" : FormatSize(entry.SizeBytes),
            ModifiedDisplay = FormatLastActivity(ResolveEntryActivityUtc(entry)),
            HashSha256 = entry.ContentHashSha256,
            StatusText = statusText,
            StatusKind = row.StatusKind,
            StatusBackground = statusBackground,
            StatusForeground = statusForeground,
            StatusGlyph = statusGlyph,
            RowOpacity = row.IsTracked ? 1d : 0.58d,
            TooltipText = BuildExplorerItemTooltip(entry, type, statusText)
        };
    }

    private static string BuildExplorerItemTooltip(RepositoryScanEntryDto entry, string type, string statusText)
    {
        return Loc.F(
            "explorer.file_tooltip",
            entry.Name,
            string.IsNullOrWhiteSpace(entry.RelativePath) ? "/" : entry.RelativePath,
            type,
            entry.IsDirectory ? "-" : FormatSize(entry.SizeBytes),
            statusText,
            FormatLastActivity(ResolveEntryActivityUtc(entry)));
    }

    private static string LocalizeExplorerFileStatus(string statusKind)
    {
        return statusKind switch
        {
            "modified" => Loc.T("explorer.file_status.modified"),
            "deleted" => Loc.T("explorer.file_status.deleted"),
            "new" => Loc.T("explorer.file_status.new"),
            "untracked" => Loc.T("explorer.file_status.untracked"),
            "ignored" => Loc.T("explorer.file_status.ignored"),
            _ => Loc.T("explorer.file_status.tracked")
        };
    }

    private static (string Background, string Foreground) ResolveExplorerStatusColors(string statusKind)
    {
        return statusKind switch
        {
            "modified" => ("#3A2B11", "#F6C15E"),
            "deleted" => ("#3A1D24", "#FF8FA6"),
            "new" => ("#183B26", "#6DDB93"),
            "untracked" => ("#212936", "#9FAEC2"),
            "ignored" => ("#202020", "#8A8A8A"),
            _ => ("#173D36", "#5EE0B5")
        };
    }

    private static string ResolveExplorerStatusGlyph(string statusKind)
        => statusKind is "untracked" or "ignored" or "deleted" ? "\u2571" : "\u2713";

    private static DateTime ResolveEntryActivityUtc(RepositoryScanEntryDto entry)
    {
        if (entry.IndexedAtUtc is { } indexedAtUtc && indexedAtUtc > entry.LastWriteUtc)
            return indexedAtUtc;

        return entry.LastWriteUtc;
    }

    private static string? NormalizeParent(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string NormalizeTreePathKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : NormalizeRelativePath(value);

    private void OnFileVersionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressFileVersionsCollectionChanged)
            return;

        RebuildVisibleFileVersions();
        OnPropertyChanged(nameof(HasNoFileVersions));
        OnPropertyChanged(nameof(CanToggleFileVersionsView));
        OnPropertyChanged(nameof(FileVersionsToggleLabel));
    }

    private void OnComparableVersionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressComparableVersionsCollectionChanged)
            return;

        OnPropertyChanged(nameof(HasComparableVersions));
    }

    private void ReplaceFileVersions(IReadOnlyList<ExplorerFileVersionViewModel> versions)
    {
        _suppressFileVersionsCollectionChanged = true;
        try
        {
            ReplaceCollectionItems(FileVersions, versions, AreExplorerFileVersionsEquivalent);
        }
        finally
        {
            _suppressFileVersionsCollectionChanged = false;
        }

        RebuildVisibleFileVersions();
        OnPropertyChanged(nameof(HasNoFileVersions));
        OnPropertyChanged(nameof(CanToggleFileVersionsView));
        OnPropertyChanged(nameof(FileVersionsToggleLabel));
    }

    private void ReplaceComparableFileVersions(IReadOnlyList<ExplorerFileVersionViewModel> versions)
    {
        _suppressComparableVersionsCollectionChanged = true;
        try
        {
            ReplaceCollectionItems(ComparableFileVersions, versions, AreReferenceItemsEquivalent);
        }
        finally
        {
            _suppressComparableVersionsCollectionChanged = false;
        }

        OnPropertyChanged(nameof(HasComparableVersions));
    }

    private static void ReplaceCollectionItems<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> rows,
        Func<T, T, bool> areEquivalent)
    {
        var sharedCount = Math.Min(collection.Count, rows.Count);

        for (var i = 0; i < sharedCount; i++)
        {
            if (!areEquivalent(collection[i], rows[i]))
                collection[i] = rows[i];
        }

        while (collection.Count > rows.Count)
            collection.RemoveAt(collection.Count - 1);

        for (var i = sharedCount; i < rows.Count; i++)
            collection.Add(rows[i]);
    }

    private static bool AreReferenceItemsEquivalent<T>(T left, T right)
        where T : class
        => ReferenceEquals(left, right);

    private static bool AreExplorerFileVersionsEquivalent(
        ExplorerFileVersionViewModel left,
        ExplorerFileVersionViewModel right)
    {
        return left.FileVersionId == right.FileVersionId
            && left.CreatedAtUtc == right.CreatedAtUtc
            && left.SizeBytes == right.SizeBytes
            && left.IsDeletionMarker == right.IsDeletionMarker
            && left.HasContentBlocks == right.HasContentBlocks
            && string.Equals(left.ContentHashSha256, right.ContentHashSha256, StringComparison.Ordinal)
            && string.Equals(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private void OnDiffPreviewRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasDiffPreviewRows));
        OnPropertyChanged(nameof(HasNoDiffPreviewRows));
        OnPropertyChanged(nameof(ShowDiffRowsPanel));
        OnPropertyChanged(nameof(ShowNoDiffPreviewMessage));
    }


    private async Task LoadFullFilePreviewAsync()
    {
        if (_diffPreviewBeforeVersionId is not > 0 || _diffPreviewAfterVersionId is not > 0)
            return;

        IsFullFilePreviewMode = true;
        IsFullFilePreviewLoading = true;
        FullPreviewBeforeText = string.Empty;
        FullPreviewAfterText = string.Empty;
        FullPreviewSummary = Loc.T("compare.full_preview.loading");

        try
        {
            var beforeTask = _mediator.Send(new GetFileVersionTextContentQuery(_diffPreviewBeforeVersionId.Value, 4_000_000));
            var afterTask = _mediator.Send(new GetFileVersionTextContentQuery(_diffPreviewAfterVersionId.Value, 4_000_000));

            await Task.WhenAll(beforeTask, afterTask);

            var before = await beforeTask;
            var after = await afterTask;
            var summaryParts = new List<string>(2);

            if (before.Success && before.Value is not null)
            {
                FullPreviewBeforeText = before.Value.Content;
                summaryParts.Add(Loc.F(
                    "compare.full_preview.before_summary",
                    FormatSize(before.Value.SizeBytes),
                    before.Value.IsTruncated ? Loc.T("compare.full_preview.truncated_suffix") : string.Empty));
            }
            else
            {
                FullPreviewBeforeText = Loc.F("compare.full_preview.before_load_failed", before.Error ?? Loc.T("common.not_available_short"));
                summaryParts.Add(Loc.T("compare.full_preview.before_unavailable"));
            }

            if (after.Success && after.Value is not null)
            {
                FullPreviewAfterText = after.Value.Content;
                summaryParts.Add(Loc.F(
                    "compare.full_preview.after_summary",
                    FormatSize(after.Value.SizeBytes),
                    after.Value.IsTruncated ? Loc.T("compare.full_preview.truncated_suffix") : string.Empty));
            }
            else
            {
                FullPreviewAfterText = Loc.F("compare.full_preview.after_load_failed", after.Error ?? Loc.T("common.not_available_short"));
                summaryParts.Add(Loc.T("compare.full_preview.after_unavailable"));
            }

            FullPreviewSummary = string.Join(" | ", summaryParts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to load full-file preview. RepositoryId {RepositoryId}. BeforeVersion {BeforeVersion}. AfterVersion {AfterVersion}",
                RepositoryId,
                _diffPreviewBeforeVersionId,
                _diffPreviewAfterVersionId);

            FullPreviewBeforeText = string.Empty;
            FullPreviewAfterText = string.Empty;
            FullPreviewSummary = Loc.T("compare.full_preview.failed");
            VersionPanelError = Loc.T("compare.full_preview.load_failed");
        }
        finally
        {
            IsFullFilePreviewLoading = false;
        }
    }

    private string? GetSelectedFileFullPath()
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath))
            return null;

        var relativePath = SelectedItem?.RelativePath;
        if (string.IsNullOrWhiteSpace(relativePath))
            relativePath = _diffPreviewRelativePath;

        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var root = Path.GetFullPath(RepositoryPath);
        var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    private string? GetFullPathForItem(ExplorerItemViewModel? item)
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath) || item is null)
            return null;

        return GetFullPathForRelativePath(item.RelativePath);
    }

    private string? GetFullPathForTreeNode(ExplorerTreeNodeViewModel? node)
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath))
            return null;

        return GetFullPathForRelativePath(node?.RelativePath ?? string.Empty);
    }

    private string? GetFullPathForRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath))
            return null;

        var root = Path.GetFullPath(RepositoryPath);
        var normalizedRelative = (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    private string? TryGetRepositoryRelativePath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath))
            return null;

        var root = Path.GetFullPath(RepositoryPath);
        var candidate = Path.GetFullPath(fullPath);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return null;

        return NormalizeRelativePath(Path.GetRelativePath(root, candidate));
    }

    private async Task CopyOrMoveItemToFolderAsync(ExplorerItemViewModel? item, bool move)
    {
        item ??= SelectedItem;
        ErrorMessage = null;
        var sourcePath = GetFullPathForItem(item);
        if (item is null || string.IsNullOrWhiteSpace(sourcePath))
        {
            ErrorMessage = Loc.T("explorer.file_operation.select_item");
            return;
        }

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            ErrorMessage = Loc.T("explorer.version_error.file_not_found_on_disk");
            return;
        }

        var targetFolder = await PickRepositoryFolderAsync(move
            ? Loc.T("explorer.file_operation.move_select_target")
            : Loc.T("explorer.file_operation.copy_select_target"));

        if (string.IsNullOrWhiteSpace(targetFolder))
            return;

        if (TryGetRepositoryRelativePath(targetFolder) is null)
        {
            ErrorMessage = Loc.T("explorer.file_operation.target_outside_repository");
            return;
        }

        if (item.IsDirectory && IsSameOrChildPath(sourcePath, targetFolder))
        {
            ErrorMessage = Loc.T("explorer.file_operation.target_inside_source");
            return;
        }

        var targetPath = Path.Combine(targetFolder, item.Name);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            ErrorMessage = Loc.F("explorer.file_operation.target_exists", item.Name);
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                if (item.IsDirectory)
                {
                    if (move)
                        Directory.Move(sourcePath, targetPath);
                    else
                        CopyDirectory(sourcePath, targetPath);

                    return;
                }

                if (move)
                    File.Move(sourcePath, targetPath);
                else
                    File.Copy(sourcePath, targetPath, overwrite: false);
            });

            var targetRelativePath = TryGetRepositoryRelativePath(targetPath);
            if (!string.IsNullOrWhiteSpace(targetRelativePath))
            {
                var parentRelativePath = GetParentRelativePath(targetRelativePath);
                if (!string.IsNullOrWhiteSpace(parentRelativePath))
                    ExpandAndSelectTreePath(parentRelativePath);

                ShowAllRepositoryFiles = true;
            }

            await RefreshEntriesAndTreeAsync(clearSelection: false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Repository explorer file operation failed. RepositoryId {RepositoryId}. Move {Move}. Source {Source}. Target {Target}",
                RepositoryId,
                move,
                sourcePath,
                targetPath);
            ErrorMessage = move
                ? Loc.T("explorer.file_operation.move_failed")
                : Loc.T("explorer.file_operation.copy_failed");
        }
    }

    private async Task<string?> PickRepositoryFolderAsync(string title)
    {
        var owner = _windows.GetActiveWindow();
        if (owner?.StorageProvider is null)
        {
            ErrorMessage = Loc.T("common.error_generic");
            return null;
        }

        var selection = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return StoragePathResolver.TryGetLocalPath(selection.FirstOrDefault());
    }

    private static string CreateUniqueDirectory(string parentPath, string baseName)
    {
        var safeBaseName = string.IsNullOrWhiteSpace(baseName) ? "New folder" : baseName.Trim();
        var candidate = Path.Combine(parentPath, safeBaseName);
        var index = 2;

        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(parentPath, $"{safeBaseName} {index}");
            index++;
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static bool IsSameOrChildPath(string parentPath, string candidatePath)
    {
        var parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentWithSeparator = parent + Path.DirectorySeparatorChar;

        return string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directoryPath);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(filePath, targetPath, overwrite: false);
        }
    }

    private void OpenFileOnDisk(ExplorerItemViewModel? item)
    {
        var fullPath = item is null
            ? GetSelectedFileFullPath()
            : GetFullPathForItem(item);

        if (string.IsNullOrWhiteSpace(fullPath))
        {
            VersionPanelError = Loc.T("explorer.version_error.select_file_to_open");
            return;
        }

        if (!File.Exists(fullPath))
        {
            VersionPanelError = Loc.T("explorer.version_error.file_not_found_on_disk");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open file {Path}", fullPath);
            VersionPanelError = Loc.T("explorer.version_error.open_default_app_failed");
        }
    }

    private void ExpandAndSelectTreePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        EnsureTreePathMaterialized(normalized);

        var current = normalized;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (_nodeByPath.TryGetValue(current, out var currentNode))
                currentNode.IsExpanded = true;

            current = GetParentRelativePath(current);
        }

        if (_nodeByPath.TryGetValue(normalized, out var node))
        {
            node.IsExpanded = true;
            SelectedTreeNode = node;
            SelectTreeNode(node);
            return;
        }

        var fallback = GetParentRelativePath(normalized);
        while (!string.IsNullOrWhiteSpace(fallback))
        {
            if (_nodeByPath.TryGetValue(fallback, out var fallbackNode))
            {
                fallbackNode.IsExpanded = true;
                SelectedTreeNode = fallbackNode;
                SelectTreeNode(fallbackNode);
                return;
            }

            fallback = GetParentRelativePath(fallback);
        }
    }

    private static string NormalizeRelativePath(string value)
        => value.Replace('\\', '/').Trim('/');

    private static string GetLeafName(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
    }

    private void ResetFullFilePreviewState()
    {
        IsFullFilePreviewMode = false;
        IsFullFilePreviewLoading = false;
        FullPreviewBeforeText = string.Empty;
        FullPreviewAfterText = string.Empty;
        FullPreviewSummary = string.Empty;
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

        var kindBadge = (leftKind, rightKind) switch
        {
            ("remove", "add") => "~",
            ("remove", _) => "-",
            (_, "add") => "+",
            _ => "="
        };

        return new DiffPreviewRowViewModel
        {
            KindBadge = kindBadge,
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

    private static SearchDirectives ParseSearchDirectives(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
            return SearchDirectives.Empty;

        var directives = SearchDirectives.Empty;
        var plainTokens = new List<string>();

        var tokens = rawQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var normalized = token.Trim();
            var lower = normalized.ToLowerInvariant();

            if (lower.StartsWith("tag:", StringComparison.Ordinal) || lower.StartsWith("ext:", StringComparison.Ordinal))
            {
                var value = lower.StartsWith("tag:", StringComparison.Ordinal)
                    ? normalized[4..]
                    : normalized[4..];
                var ext = NormalizeExtensionFilter(value);
                if (ext != "all")
                    directives = directives with { ExtensionFilter = ext };
                continue;
            }

            if (lower.StartsWith("type:", StringComparison.Ordinal))
            {
                var value = NormalizeEntryTypeFilter(normalized[5..]);
                if (value != "all")
                    directives = directives with { EntryTypeFilter = value };
                continue;
            }

            if (lower.StartsWith("modified:", StringComparison.Ordinal))
            {
                var value = NormalizeModifiedWindowFilter(normalized[9..]);
                if (value != "all")
                    directives = directives with { ModifiedWindowFilter = value };
                continue;
            }

            if (TryParseSizeDirective(lower, out var isMin, out var sizeMb))
            {
                directives = isMin
                    ? directives with { MinSizeMb = sizeMb }
                    : directives with { MaxSizeMb = sizeMb };
                continue;
            }

            plainTokens.Add(normalized);
        }

        directives = directives with { TextQuery = string.Join(' ', plainTokens) };
        return directives;
    }

    private static bool TryParseSizeDirective(string token, out bool isMin, out double sizeMb)
    {
        isMin = false;
        sizeMb = 0;
        string? payload = null;

        if (token.StartsWith("size>=", StringComparison.Ordinal))
        {
            payload = token[6..];
            isMin = true;
        }
        else if (token.StartsWith("size>", StringComparison.Ordinal))
        {
            payload = token[5..];
            isMin = true;
        }
        else if (token.StartsWith("size<=", StringComparison.Ordinal))
        {
            payload = token[6..];
            isMin = false;
        }
        else if (token.StartsWith("size<", StringComparison.Ordinal))
        {
            payload = token[5..];
            isMin = false;
        }

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        payload = payload.TrimEnd('m', 'b');
        if (!double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out sizeMb))
            return false;

        return sizeMb > 0;
    }

    private static double? ParseNullableDouble(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return null;

        return parsed > 0 ? parsed : null;
    }

    private static string NormalizeEntryTypeFilter(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "file" => "files",
            "dir" => "folders",
            "directory" => "folders",
            "files" => "files",
            "folders" => "folders",
            _ => "all"
        };
    }

    private static string NormalizeExtensionFilter(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "all")
            return "all";

        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }

    private static string NormalizeModifiedWindowFilter(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "1d" => "24h",
            "24h" => "24h",
            "7d" => "7d",
            "30d" => "30d",
            _ => "all"
        };
    }

    private static SearchSnapshotHistoryDirectives ParseSnapshotHistoryDirectives(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
            return SearchSnapshotHistoryDirectives.Empty;

        var directives = SearchSnapshotHistoryDirectives.Empty;
        var plainTokens = new List<string>();

        var tokens = rawQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var normalized = token.Trim();
            var lower = normalized.ToLowerInvariant();

            if (lower.StartsWith("trigger:", StringComparison.Ordinal))
            {
                var trigger = NormalizeSnapshotTriggerFilter(normalized[8..]);
                if (trigger != "all")
                    directives = directives with { TriggerFilter = trigger };
                continue;
            }

            if (lower.StartsWith("kind:", StringComparison.Ordinal))
            {
                var kind = NormalizeSnapshotKindFilter(normalized[5..]);
                if (kind != "all")
                    directives = directives with { KindFilter = kind };
                continue;
            }

            if (lower.StartsWith("tag:", StringComparison.Ordinal))
            {
                var tag = NormalizeSnapshotTagFilter(normalized[4..]);
                if (tag != "all")
                    directives = directives with { TagFilter = tag };
                continue;
            }

            if (TryParseChangedDirective(lower, out var minChanged))
            {
                directives = directives with { MinChangedFiles = minChanged };
                continue;
            }

            plainTokens.Add(normalized);
        }

        directives = directives with { TextQuery = string.Join(' ', plainTokens) };
        return directives;
    }

    private static SearchSnapshotFileDirectives ParseSnapshotFilesDirectives(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
            return SearchSnapshotFileDirectives.Empty;

        var directives = SearchSnapshotFileDirectives.Empty;
        var plainTokens = new List<string>();

        var tokens = rawQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var normalized = token.Trim();
            var lower = normalized.ToLowerInvariant();

            if (lower.StartsWith("kind:", StringComparison.Ordinal))
            {
                var kind = NormalizeSnapshotFileChangeKindFilter(normalized[5..]);
                if (kind != "all")
                    directives = directives with { ChangeKindFilter = kind };
                continue;
            }

            if (lower.StartsWith("tag:", StringComparison.Ordinal) || lower.StartsWith("ext:", StringComparison.Ordinal))
            {
                var ext = NormalizeSnapshotFileExtensionFilter(normalized[4..]);
                if (ext != "all")
                    directives = directives with { ExtensionFilter = ext };
                continue;
            }

            if (TryParseSizeDirective(lower, out var isMin, out var sizeKb))
            {
                if (isMin)
                    directives = directives with { MinSizeDeltaKb = sizeKb };
                continue;
            }

            plainTokens.Add(normalized);
        }

        directives = directives with { TextQuery = string.Join(' ', plainTokens) };
        return directives;
    }

    private static bool TryParseChangedDirective(string token, out int minChanged)
    {
        minChanged = 0;
        string? payload = null;

        if (token.StartsWith("changed>=", StringComparison.Ordinal))
            payload = token[9..];
        else if (token.StartsWith("changed>", StringComparison.Ordinal))
            payload = token[8..];
        else if (token.StartsWith("files>=", StringComparison.Ordinal))
            payload = token[7..];
        else if (token.StartsWith("files>", StringComparison.Ordinal))
            payload = token[6..];

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        return int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out minChanged)
               && minChanged >= 0;
    }

    private static int? ParseNullableInt(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return null;

        return parsed >= 0 ? parsed : null;
    }

    private static string NormalizeSnapshotTriggerFilter(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_');
        return string.IsNullOrWhiteSpace(normalized) || normalized == "all"
            ? "all"
            : normalized;
    }

    private static string NormalizeSnapshotKindFilter(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_');
        return normalized switch
        {
            "manual" => RepositorySnapshotKind.Manual,
            "automatic" => RepositorySnapshotKind.Automatic,
            "working" => RepositorySnapshotKind.Working,
            _ => "all"
        };
    }

    private static string NormalizeSnapshotTagFilter(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('#').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) || normalized == "all"
            ? "all"
            : normalized;
    }

    private static string NormalizeSnapshotFileChangeKindFilter(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "add" => "added",
            "added" => "added",
            "modify" => "modified",
            "modified" => "modified",
            "remove" => "removed",
            "removed" => "removed",
            "delete" => "removed",
            "deleted" => "removed",
            _ => "all"
        };
    }

    private static string NormalizeSnapshotFileExtensionFilter(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "all")
            return "all";

        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }

    private static string FormatLastActivity(DateTime utc)
    {
        var delta = DateTime.UtcNow - utc;
        if (delta.TotalSeconds < 60) return Loc.T("dashboard.just_now");
        if (delta.TotalMinutes < 60) return Loc.F("dashboard.minutes_ago", (int)delta.TotalMinutes);
        if (delta.TotalHours < 24) return Loc.F("dashboard.hours_ago", (int)delta.TotalHours);
        return Loc.F("dashboard.days_ago", (int)delta.TotalDays);
    }
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static string GetMaintenanceActionDisplayName(string actionName)
    {
        return actionName switch
        {
            "export" => Loc.T("explorer.maintenance.action.export"),
            "import" => Loc.T("explorer.maintenance.action.import"),
            "repair" => Loc.T("explorer.maintenance.action.repair"),
            "reindex" => Loc.T("explorer.maintenance.action.reindex"),
            "relink" => Loc.T("explorer.maintenance.action.relink"),
            _ => actionName
        };
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
