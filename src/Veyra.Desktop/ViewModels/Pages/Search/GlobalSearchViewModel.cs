using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.DTOs;
using Veyra.Application.Queries.Repository;
using Veyra.Application.Queries.Search;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Execution;
using Veyra.Desktop.Services.State;

namespace Veyra.Desktop.ViewModels.Pages.Search;

public sealed partial class GlobalSearchViewModel : ObservableObject
{
    private const int MaxVisibleFileResults = 500;
    private const int MaxVisibleSnapshotResults = 200;
    private const int FilterDebounceMs = 120;

    private readonly IServiceScopeExecutor _scopeExecutor;
    private readonly ILogger<GlobalSearchViewModel> _log;
    private readonly IGlobalSearchFilterStore _filterStore;
    private readonly LocalizationManager _localization;
    private readonly List<RepositoryDto> _repositorySource = [];
    private readonly List<GlobalSearchIndexedEntry> _entrySource = [];
    private readonly List<GlobalSearchIndexedSnapshot> _snapshotSource = [];
    private readonly List<GlobalSearchIndexedSnapshotFileChange> _linkedSnapshotFileSource = [];
    private readonly Dictionary<(int RepositoryId, string RelativePath), IReadOnlyList<string>> _fileTagsByPath = [];
    private readonly Dictionary<int, GlobalSearchRepositoryResultItemViewModel> _repositoryResultCache = [];
    private readonly Dictionary<(int RepositoryId, string RelativePath), GlobalSearchFileResultItemViewModel> _fileResultCache = [];
    private readonly Dictionary<(int RepositoryId, long SnapshotId), GlobalSearchSnapshotResultItemViewModel> _snapshotResultCache = [];
    private int _matchedRepositoryCount;
    private int _matchedFileCount;
    private int _matchedSnapshotCount;
    private bool _fileResultsLimited;
    private CancellationTokenSource? _loadCts;
    private long _loadRequestId;
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _linkedSnapshotCts;
    private long _filterRequestId;
    private long _linkedSnapshotRequestId;
    private bool _suppressFilterApply;
    private bool _suppressLinkedSelection;
    private bool _presetsLoaded;
    private int? _linkedRepositoryId;
    private long? _linkedSnapshotId;

    public event Action? BackRequested;
    public event Func<int, Task>? OpenRepositoryRequested;
    public event Func<int, Task>? OpenRepositorySettingsRequested;
    public event Func<int, string, bool, Task>? OpenEntryRequested;
    public event Func<int, long, Task>? OpenSnapshotRequested;
    public event Func<string, Task>? SnapshotTagSelectedRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(LoadingOverlayTitle))]
    [NotifyPropertyChangedFor(nameof(LoadingOverlayDetail))]
    private bool _isTransientActionBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingOverlayTitle))]
    private string _transientActionTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingOverlayDetail))]
    private string _transientActionDetail = string.Empty;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private GlobalSearchRepositoryFilterOptionViewModel? _selectedRepositoryFilter;
    [ObservableProperty] private string _selectedTrackedFormatFilter = "all";
    [ObservableProperty] private string _selectedEntryTypeFilter = "all";
    [ObservableProperty] private string _selectedExtensionFilter = "all";
    [ObservableProperty] private string _selectedModifiedWindowFilter = "all";
    [ObservableProperty] private string _selectedSnapshotTagFilter = "all";
    [ObservableProperty] private string _minSizeMb = string.Empty;
    [ObservableProperty] private string _maxSizeMb = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastUpdatedText))]
    private string _lastUpdatedText = string.Empty;
    [ObservableProperty] private string _loadingStatusText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterButtonLabel))]
    private bool _isFilterPanelVisible;
    [ObservableProperty] private bool _isSectionPanelVisible;
    [ObservableProperty] private bool _isTagPickerVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTagPickerSearchQuery))]
    private string _tagPickerSearchQuery = string.Empty;
    [ObservableProperty] private bool _isRepositoriesSectionVisible = true;
    [ObservableProperty] private bool _isSnapshotsSectionVisible = true;
    [ObservableProperty] private bool _isFilesSectionVisible = true;
    [ObservableProperty] private bool _isLinkedSearchEnabled = true;
    [ObservableProperty] private GlobalSearchRepositoryResultItemViewModel? _selectedRepositoryResult;
    [ObservableProperty] private GlobalSearchSnapshotResultItemViewModel? _selectedSnapshotResult;
    [ObservableProperty] private string _savedFilterName = string.Empty;
    [ObservableProperty] private GlobalSearchSavedFilterViewModel? _selectedSavedFilter;
    [ObservableProperty] private bool _hasSavedFilters;

    public ObservableCollection<GlobalSearchRepositoryFilterOptionViewModel> RepositoryFilters { get; } = [];
    public ObservableCollection<string> TrackedFormatFilters { get; } = ["all"];
    public ObservableCollection<string> EntryTypeFilters { get; } = ["all", "files", "folders"];
    public ObservableCollection<string> ExtensionFilters { get; } = ["all"];
    public ObservableCollection<string> ModifiedWindowFilters { get; } = ["all", "24h", "7d", "30d"];
    public ObservableCollection<string> SnapshotTagFilters { get; } = ["all"];
    public ObservableCollection<GlobalSearchRepositoryResultItemViewModel> RepositoryResults { get; } = [];
    public ObservableCollection<GlobalSearchFileResultItemViewModel> FileResults { get; } = [];
    public ObservableCollection<GlobalSearchSnapshotResultItemViewModel> SnapshotResults { get; } = [];
    public ObservableCollection<GlobalSearchSavedFilterViewModel> SavedFilters { get; } = [];
    public ObservableCollection<string> TagPickerItems { get; } = [];

    public bool HasRepositoryResults => RepositoryResults.Count > 0;
    public bool HasFileResults => FileResults.Count > 0;
    public bool HasSnapshotResults => SnapshotResults.Count > 0;
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasTagPickerItems => TagPickerItems.Count > 0;
    public bool HasTagPickerSearchQuery => !string.IsNullOrWhiteSpace(TagPickerSearchQuery);
    public bool HasLastUpdatedText => !string.IsNullOrWhiteSpace(LastUpdatedText);
    public bool CanRefresh => !IsLoading && !IsRefreshing && !IsTransientActionBusy;
    public bool IsBusy => IsLoading || IsRefreshing || IsTransientActionBusy;
    public bool HasAnyVisibleSection => IsRepositoriesSectionVisible || IsSnapshotsSectionVisible || IsFilesSectionVisible;
    public int VisibleSectionCount => new[] { IsRepositoriesSectionVisible, IsSnapshotsSectionVisible, IsFilesSectionVisible }.Count(v => v);
    public string SectionButtonLabel => Loc.F("search.sections_button", VisibleSectionCount);
    public string RepositorySectionStateText => IsRepositoriesSectionVisible ? Loc.T("search.section_visible") : Loc.T("search.section_hidden");
    public string SnapshotSectionStateText => IsSnapshotsSectionVisible ? Loc.T("search.section_visible") : Loc.T("search.section_hidden");
    public string FileSectionStateText => IsFilesSectionVisible ? Loc.T("search.section_visible") : Loc.T("search.section_hidden");
    public string LinkedSelectionText
    {
        get
        {
            if (!IsLinkedSearchEnabled)
                return Loc.T("search.linked_search_off");

            if (_linkedSnapshotId is > 0 && SelectedSnapshotResult is not null)
                return Loc.F("search.linked_snapshot_selected", SelectedSnapshotResult.RepositoryName, SelectedSnapshotResult.CreatedText);

            if (_linkedRepositoryId is > 0 && SelectedRepositoryResult is not null)
                return Loc.F("search.linked_repository_selected", SelectedRepositoryResult.Name);

            return Loc.T("search.linked_search_hint");
        }
    }
    public int RepositorySectionRow => 0;
    public int RepositorySectionColumn => ResolveRepositoryLayout().Column;
    public int RepositorySectionRowSpan => ResolveRepositoryLayout().RowSpan;
    public int RepositorySectionColumnSpan => ResolveRepositoryLayout().ColumnSpan;
    public int SnapshotSectionRow => ResolveSnapshotLayout().Row;
    public int SnapshotSectionColumn => ResolveSnapshotLayout().Column;
    public int SnapshotSectionRowSpan => ResolveSnapshotLayout().RowSpan;
    public int SnapshotSectionColumnSpan => ResolveSnapshotLayout().ColumnSpan;
    public int FileSectionRow => ResolveFileLayout().Row;
    public int FileSectionColumn => ResolveFileLayout().Column;
    public int FileSectionRowSpan => ResolveFileLayout().RowSpan;
    public int FileSectionColumnSpan => ResolveFileLayout().ColumnSpan;
    public bool HasActiveFilters => GetActiveFilterCount() > 0;
    public string FilterButtonLabel => HasActiveFilters
        ? Loc.F("search.filters_active_button", GetActiveFilterCount())
        : Loc.T("search.filters_button");
    public string LoadingOverlayTitle => IsTransientActionBusy
        ? TransientActionTitle
        : Loc.T("search.loading_title");
    public string LoadingOverlayDetail => IsTransientActionBusy
        ? TransientActionDetail
        : Loc.T("search.loading_detail");
    public bool HasLoadedData => _repositorySource.Count > 0 || _entrySource.Count > 0;

    public string RepositoryResultsSummary => _repositorySource.Count == 0
        ? Loc.T("search.repositories_summary_empty")
        : Loc.F("search.repositories_summary", _matchedRepositoryCount, _repositorySource.Count);

    public string FileResultsSummary => _entrySource.Count == 0
        ? Loc.T("search.files_summary_empty")
        : _fileResultsLimited
            ? Loc.F("search.files_summary_limited", FileResults.Count, _matchedFileCount)
            : Loc.F("search.files_summary", _matchedFileCount, _entrySource.Count);

    public string SnapshotResultsSummary => _snapshotSource.Count == 0
        ? Loc.T("search.snapshots_summary_empty")
        : Loc.F("search.snapshots_summary", _matchedSnapshotCount, _snapshotSource.Count);

    public GlobalSearchViewModel(
        IServiceScopeExecutor scopeExecutor,
        IGlobalSearchFilterStore filterStore,
        ILogger<GlobalSearchViewModel> log)
    {
        _scopeExecutor = scopeExecutor;
        _filterStore = filterStore;
        _log = log;
        _localization = LocalizationManager.Instance;
        _localization.LanguageChanged += OnLanguageChanged;
        RebuildRepositoryFilterOptions();
    }

    partial void OnSearchQueryChanged(string value) => OnFilterStateChanged();
    partial void OnSelectedRepositoryFilterChanged(GlobalSearchRepositoryFilterOptionViewModel? value) => OnFilterStateChanged();
    partial void OnSelectedTrackedFormatFilterChanged(string value) => OnFilterStateChanged();
    partial void OnSelectedEntryTypeFilterChanged(string value) => OnFilterStateChanged();
    partial void OnSelectedExtensionFilterChanged(string value) => OnFilterStateChanged();
    partial void OnSelectedModifiedWindowFilterChanged(string value) => OnFilterStateChanged();
    partial void OnSelectedSnapshotTagFilterChanged(string value) => OnFilterStateChanged();
    partial void OnTagPickerSearchQueryChanged(string value) => RebuildTagPickerItems();
    partial void OnMinSizeMbChanged(string value) => OnFilterStateChanged();
    partial void OnMaxSizeMbChanged(string value) => OnFilterStateChanged();

    partial void OnIsRepositoriesSectionVisibleChanged(bool value) => NotifySectionLayoutStateChanged();
    partial void OnIsSnapshotsSectionVisibleChanged(bool value) => NotifySectionLayoutStateChanged();
    partial void OnIsFilesSectionVisibleChanged(bool value) => NotifySectionLayoutStateChanged();

    partial void OnIsLinkedSearchEnabledChanged(bool value)
    {
        if (!value)
            ClearLinkedSelectionCore();
        else
            CaptureLinkedSelection();

        ApplyFiltersIfNeeded(debounce: false);
        NotifyLinkedSelectionChanged();
    }

    partial void OnSelectedRepositoryResultChanged(GlobalSearchRepositoryResultItemViewModel? value)
    {
        if (_suppressLinkedSelection || !IsLinkedSearchEnabled)
            return;

        _linkedRepositoryId = value?.RepositoryId;
        _linkedSnapshotId = null;
        _linkedSnapshotFileSource.Clear();
        _suppressLinkedSelection = true;
        try
        {
            SelectedSnapshotResult = null;
        }
        finally
        {
            _suppressLinkedSelection = false;
        }

        ApplyFiltersIfNeeded(debounce: false);
        NotifyLinkedSelectionChanged();
    }

    partial void OnSelectedSnapshotResultChanged(GlobalSearchSnapshotResultItemViewModel? value)
    {
        if (_suppressLinkedSelection || !IsLinkedSearchEnabled)
            return;

        _linkedRepositoryId = value?.RepositoryId ?? _linkedRepositoryId;
        _linkedSnapshotId = value?.SnapshotId;
        NotifyLinkedSelectionChanged();

        if (value is null)
        {
            _linkedSnapshotFileSource.Clear();
            ApplyFiltersIfNeeded(debounce: false);
            return;
        }

        _ = LoadLinkedSnapshotFilesAsync(value);
    }

    private void OnFilterStateChanged()
    {
        NotifyFilterChromeChanged();
        ApplyFiltersIfNeeded();
    }

    public async Task LoadAsync(bool forceRefresh = false)
    {
        if (IsLoading || (!forceRefresh && (IsRefreshing || HasLoadedData)))
        {
            if (!forceRefresh)
                ApplyFiltersIfNeeded(debounce: false);

            return;
        }

        var (requestId, ct) = BeginLoadRequest();

        try
        {
            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = string.Empty;
            LoadingStatusText = Loc.T("search.loading_prepare");
            await Task.Yield();

            if (!_presetsLoaded)
            {
                await LoadSavedFiltersAsync(ct);
                _presetsLoaded = true;
            }

            var searchIndex = await SendIsolatedAsync(new GetGlobalSearchIndexQuery(60), ct);
            if (!IsLatestLoadRequest(requestId) || ct.IsCancellationRequested)
                return;

            var repositorySource = searchIndex.Repositories
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var repositoryLookup = repositorySource.ToDictionary(repository => repository.Id);
            var indexedEntries = searchIndex.RepositoryEntries
                .SelectMany(group =>
                {
                    if (!repositoryLookup.TryGetValue(group.RepositoryId, out var repository))
                        return Array.Empty<GlobalSearchIndexedEntry>();

                    return group.Entries.Select(entry => new GlobalSearchIndexedEntry(repository, entry));
                })
                .OrderByDescending(row => row.Entry.LastWriteUtc)
                .ThenBy(row => row.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Repository.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var indexedSnapshots = searchIndex.RepositorySnapshots
                .SelectMany(group =>
                {
                    if (!repositoryLookup.TryGetValue(group.RepositoryId, out var repository))
                        return Array.Empty<GlobalSearchIndexedSnapshot>();

                    return group.Snapshots.Select(snapshot => new GlobalSearchIndexedSnapshot(repository, snapshot));
                })
                .OrderByDescending(row => row.Snapshot.CreatedAtUtc)
                .ThenBy(row => row.Repository.Name, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(row => row.Snapshot.SnapshotId)
                .ToList();

            _repositorySource.Clear();
            _repositorySource.AddRange(repositorySource);
            _entrySource.Clear();
            _entrySource.AddRange(indexedEntries);
            _snapshotSource.Clear();
            _snapshotSource.AddRange(indexedSnapshots);
            RebuildFileTags(searchIndex.TaggedFiles ?? Array.Empty<GlobalSearchTaggedFileDto>());
            ResetResultCaches();

            RebuildFilterOptions();
            await ApplyFiltersAsync(debounce: false);
            LastUpdatedText = Loc.F("search.last_updated_format", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            StatusMessage = Loc.F("search.refresh_done", _repositorySource.Count, _entrySource.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load global search index");
            ErrorMessage = Loc.T("search.load_failed");
            if (_repositorySource.Count == 0)
                ResetVisibleResults();
        }
        finally
        {
            if (IsLatestLoadRequest(requestId))
            {
                LoadingStatusText = string.Empty;
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!CanRefresh)
            return;

        try
        {
            IsRefreshing = true;
            await LoadAsync(forceRefresh: true);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(LoadingOverlayTitle));
        OnPropertyChanged(nameof(LoadingOverlayDetail));
    }

    partial void OnIsRefreshingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(LoadingOverlayTitle));
        OnPropertyChanged(nameof(LoadingOverlayDetail));
    }

    partial void OnIsTransientActionBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(LoadingOverlayTitle));
        OnPropertyChanged(nameof(LoadingOverlayDetail));
    }

    [RelayCommand]
    private void Back()
    {
        CancelActiveLoad();
        BackRequested?.Invoke();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilterApply = true;
        try
        {
            SearchQuery = string.Empty;
            SelectedRepositoryFilter = RepositoryFilters.FirstOrDefault();
            SelectedTrackedFormatFilter = "all";
            SelectedEntryTypeFilter = "all";
            SelectedExtensionFilter = "all";
            SelectedModifiedWindowFilter = "all";
            SelectedSnapshotTagFilter = "all";
            MinSizeMb = string.Empty;
            MaxSizeMb = string.Empty;
        }
        finally
        {
            _suppressFilterApply = false;
        }

        StatusMessage = string.Empty;
        ApplyFiltersIfNeeded(debounce: false);
        NotifyFilterChromeChanged();
    }

    [RelayCommand]
    private void ToggleFilterPanel()
    {
        IsFilterPanelVisible = !IsFilterPanelVisible;
        if (IsFilterPanelVisible)
        {
            IsSectionPanelVisible = false;
            IsTagPickerVisible = false;
        }
    }

    [RelayCommand]
    private void ToggleSectionPanel()
    {
        IsSectionPanelVisible = !IsSectionPanelVisible;
        if (IsSectionPanelVisible)
        {
            IsFilterPanelVisible = false;
            IsTagPickerVisible = false;
        }
    }

    [RelayCommand]
    private void ToggleTagPicker()
    {
        IsTagPickerVisible = !IsTagPickerVisible;
        if (IsTagPickerVisible)
        {
            IsFilterPanelVisible = false;
            IsSectionPanelVisible = false;
            RebuildTagPickerItems();
        }
    }

    [RelayCommand]
    private void CloseTagPicker()
        => IsTagPickerVisible = false;

    [RelayCommand]
    private void ClearTagPickerSearch()
        => TagPickerSearchQuery = string.Empty;

    [RelayCommand]
    private async Task SelectSnapshotTagAsync(string? tag)
    {
        var normalized = NormalizeTagFilter(tag);
        if (normalized == "all")
            return;

        SelectedSnapshotTagFilter = normalized;
        SearchQuery = string.Empty;
        IsSnapshotsSectionVisible = true;
        IsFilesSectionVisible = true;
        IsTagPickerVisible = false;
        ApplyFiltersIfNeeded(debounce: false);

        if (SnapshotTagSelectedRequested is not null)
            await SnapshotTagSelectedRequested.Invoke(normalized);
    }

    public void CloseFloatingPanels()
    {
        IsFilterPanelVisible = false;
        IsSectionPanelVisible = false;
        IsTagPickerVisible = false;
    }

    [RelayCommand]
    private void ToggleRepositoriesSection() => IsRepositoriesSectionVisible = !IsRepositoriesSectionVisible;

    [RelayCommand]
    private void ToggleSnapshotsSection() => IsSnapshotsSectionVisible = !IsSnapshotsSectionVisible;

    [RelayCommand]
    private void ToggleFilesSection() => IsFilesSectionVisible = !IsFilesSectionVisible;

    [RelayCommand]
    private void ClearLinkedSelection()
    {
        ClearLinkedSelectionCore();
        ApplyFiltersIfNeeded(debounce: false);
        NotifyLinkedSelectionChanged();
    }

    [RelayCommand]
    private async Task SaveCurrentFilterAsync()
    {
        var name = (SavedFilterName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = Loc.T("dashboard.error_preset_name_required");
            return;
        }

        var preset = BuildCurrentPreset(name);
        var existing = SavedFilters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var index = SavedFilters.IndexOf(existing);
            SavedFilters.RemoveAt(index);
            SavedFilters.Insert(index, new GlobalSearchSavedFilterViewModel(preset));
        }
        else
        {
            SavedFilters.Add(new GlobalSearchSavedFilterViewModel(preset));
        }

        SortSavedFilters();
        await PersistSavedFiltersAsync();
        SelectedSavedFilter = SavedFilters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        HasSavedFilters = SavedFilters.Count > 0;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void ApplySavedFilter(GlobalSearchSavedFilterViewModel? filter)
    {
        var target = filter ?? SelectedSavedFilter;
        if (target is null)
            return;

        ApplyPreset(target.Preset);
    }

    [RelayCommand]
    private async Task DeleteSavedFilterAsync(GlobalSearchSavedFilterViewModel? filter)
    {
        var target = filter ?? SelectedSavedFilter;
        if (target is null)
            return;

        SavedFilters.Remove(target);
        HasSavedFilters = SavedFilters.Count > 0;
        if (ReferenceEquals(SelectedSavedFilter, target))
            SelectedSavedFilter = null;

        await PersistSavedFiltersAsync();
    }

    [RelayCommand]
    private async Task OpenRepositoryAsync(GlobalSearchRepositoryResultItemViewModel? item)
    {
        if (item is null || OpenRepositoryRequested is null)
            return;

        await RunTransientActionAsync(
            "search.open_repository_title",
            "search.open_repository_detail",
            () => OpenRepositoryRequested.Invoke(item.RepositoryId),
            ex =>
            {
                _log.LogError(ex, "Failed to open repository from search. RepositoryId {RepositoryId}", item.RepositoryId);
                ErrorMessage = Loc.T("search.load_failed");
            });
    }

    [RelayCommand]
    private async Task OpenRepositorySettingsAsync(GlobalSearchRepositoryResultItemViewModel? item)
    {
        if (item is null || OpenRepositorySettingsRequested is null)
            return;

        await RunTransientActionAsync(
            "search.open_settings_title",
            "search.open_settings_detail",
            () => OpenRepositorySettingsRequested.Invoke(item.RepositoryId),
            ex =>
            {
                _log.LogError(ex, "Failed to open repository settings from search. RepositoryId {RepositoryId}", item.RepositoryId);
                ErrorMessage = Loc.T("search.load_failed");
            });
    }

    [RelayCommand]
    private async Task OpenEntryAsync(GlobalSearchFileResultItemViewModel? item)
    {
        if (item is null || OpenEntryRequested is null)
            return;

        await RunTransientActionAsync(
            "search.open_result_title",
            "search.open_result_detail",
            () => OpenEntryRequested.Invoke(item.RepositoryId, item.RelativePath, item.IsDirectory),
            ex =>
            {
                _log.LogError(ex, "Failed to open search result entry. RepositoryId {RepositoryId}. Path {RelativePath}", item.RepositoryId, item.RelativePath);
                ErrorMessage = Loc.T("search.load_failed");
            });
    }

    [RelayCommand]
    private async Task OpenSnapshotAsync(GlobalSearchSnapshotResultItemViewModel? item)
    {
        if (item is null || OpenSnapshotRequested is null)
            return;

        await RunTransientActionAsync(
            "search.open_snapshot_title",
            "search.open_snapshot_detail",
            () => OpenSnapshotRequested.Invoke(item.RepositoryId, item.SnapshotId),
            ex =>
            {
                _log.LogError(ex, "Failed to open snapshot from search. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}", item.RepositoryId, item.SnapshotId);
                ErrorMessage = Loc.T("search.load_failed");
            });
    }

    private async Task LoadLinkedSnapshotFilesAsync(GlobalSearchSnapshotResultItemViewModel snapshot)
    {
        var (requestId, ct) = BeginLinkedSnapshotRequest();

        try
        {
            StatusMessage = Loc.T("search.linked_snapshot_loading");
            var files = await SendIsolatedAsync(
                new GetRepositorySnapshotChangedFilesQuery(snapshot.RepositoryId, snapshot.SnapshotId, MaxVisibleFileResults),
                ct);

            if (!IsLatestLinkedSnapshotRequest(requestId) || ct.IsCancellationRequested)
                return;

            var repository = _repositorySource.FirstOrDefault(r => r.Id == snapshot.RepositoryId);
            _linkedSnapshotFileSource.Clear();
            if (repository is not null)
            {
                _linkedSnapshotFileSource.AddRange(files.Select(file =>
                    new GlobalSearchIndexedSnapshotFileChange(repository, snapshot, file)));
            }

            StatusMessage = Loc.F("search.linked_snapshot_loaded", _linkedSnapshotFileSource.Count);
            ApplyFiltersIfNeeded(debounce: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Failed to load linked snapshot files. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}",
                snapshot.RepositoryId,
                snapshot.SnapshotId);
            ErrorMessage = Loc.T("search.linked_snapshot_failed");
        }
    }

    private void ClearLinkedSelectionCore()
    {
        _linkedRepositoryId = null;
        _linkedSnapshotId = null;
        _linkedSnapshotFileSource.Clear();

        _suppressLinkedSelection = true;
        try
        {
            SelectedRepositoryResult = null;
            SelectedSnapshotResult = null;
        }
        finally
        {
            _suppressLinkedSelection = false;
        }
    }

    private void CaptureLinkedSelection()
    {
        _linkedRepositoryId = SelectedRepositoryResult?.RepositoryId;
        _linkedSnapshotId = SelectedSnapshotResult?.SnapshotId;
        if (_linkedSnapshotId is null)
            _linkedSnapshotFileSource.Clear();
    }

    private void ApplyFiltersIfNeeded(bool debounce = true)
    {
        if (_suppressFilterApply)
            return;

        ScheduleApplyFilters(debounce);
    }

    private void ScheduleApplyFilters(bool debounce = true)
        => _ = ApplyFiltersAsync(debounce);

    private async Task ApplyFiltersAsync(bool debounce = true)
    {
        var repositoryFilterId = SelectedRepositoryFilter?.RepositoryId;
        var trackedFormatFilter = NormalizeFilter(SelectedTrackedFormatFilter);
        var entryTypeFilter = NormalizeFilter(SelectedEntryTypeFilter);
        var extensionFilter = NormalizeExtensionFilter(SelectedExtensionFilter);
        var modifiedWindowFilter = NormalizeFilter(SelectedModifiedWindowFilter);
        var snapshotTagFilter = NormalizeTagFilter(SelectedSnapshotTagFilter);
        var textQuery = (SearchQuery ?? string.Empty).Trim();
        var textTagQuery = NormalizeSearchTagText(textQuery);
        var minSizeMb = ParseNullableDouble(MinSizeMb);
        var maxSizeMb = ParseNullableDouble(MaxSizeMb);
        var repositories = _repositorySource.ToArray();
        var entries = _entrySource.ToArray();
        var snapshots = _snapshotSource.ToArray();
        var linkedSnapshotFiles = _linkedSnapshotFileSource.ToArray();
        var linkedRepositoryId = IsLinkedSearchEnabled ? _linkedRepositoryId : null;
        var linkedSnapshotId = IsLinkedSearchEnabled ? _linkedSnapshotId : null;
        var (requestId, ct) = BeginFilterRequest();

        try
        {
            if (debounce)
                await Task.Delay(FilterDebounceMs, ct);

            var result = await Task.Run(() =>
            {
                if (minSizeMb is > 0 && maxSizeMb is > 0 && minSizeMb > maxSizeMb)
                    (minSizeMb, maxSizeMb) = (maxSizeMb, minSizeMb);

                var modifiedThresholdUtc = ResolveModifiedWindowThreshold(modifiedWindowFilter);

                var repositoryMatches = new List<RepositoryDto>(repositories.Length);
                foreach (var repository in repositories)
                {
                    if (repositoryFilterId.HasValue && repository.Id != repositoryFilterId.Value)
                        continue;

                    if (trackedFormatFilter != "all"
                        && !repository.LinkedFormats.Contains(trackedFormatFilter, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!MatchesRepositoryText(repository, textQuery))
                        continue;

                    repositoryMatches.Add(repository);
                }

                var snapshotVisible = new List<GlobalSearchIndexedSnapshot>(Math.Min(snapshots.Length, MaxVisibleSnapshotResults));
                var snapshotMatchCount = 0;
                foreach (var row in snapshots)
                {
                    if (repositoryFilterId.HasValue && row.Repository.Id != repositoryFilterId.Value)
                        continue;

                    if (linkedRepositoryId.HasValue && row.Repository.Id != linkedRepositoryId.Value)
                        continue;

                    if (trackedFormatFilter != "all"
                        && !row.Repository.LinkedFormats.Contains(trackedFormatFilter, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if ((!MatchesSnapshotText(row, textQuery)
                         && !MatchesSnapshotTagText(row.Snapshot, textTagQuery))
                        || !MatchesSnapshotTag(row.Snapshot, snapshotTagFilter)
                        || !MatchesSnapshotWindow(row.Snapshot, modifiedThresholdUtc))
                    {
                        continue;
                    }

                    snapshotMatchCount++;
                    if (snapshotVisible.Count < MaxVisibleSnapshotResults)
                        snapshotVisible.Add(row);
                }

                var fileMatchCount = 0;
                var fileVisible = new List<GlobalSearchIndexedEntry>(Math.Min(entries.Length, MaxVisibleFileResults));
                var snapshotFileVisible = new List<GlobalSearchIndexedSnapshotFileChange>(Math.Min(linkedSnapshotFiles.Length, MaxVisibleFileResults));

                if (linkedSnapshotId.HasValue)
                {
                    foreach (var row in linkedSnapshotFiles)
                    {
                        if (repositoryFilterId.HasValue && row.Repository.Id != repositoryFilterId.Value)
                            continue;

                        if (linkedRepositoryId.HasValue && row.Repository.Id != linkedRepositoryId.Value)
                            continue;

                        if (trackedFormatFilter != "all"
                            && !row.Repository.LinkedFormats.Contains(trackedFormatFilter, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if ((!MatchesSnapshotFileText(row, textQuery)
                             && !MatchesFileTagText(row.Repository.Id, row.Change.RelativePath, textTagQuery))
                            || !MatchesSnapshotFileType(entryTypeFilter)
                            || !MatchesSnapshotFileExtension(row.Change, extensionFilter)
                            || !MatchesFileTags(row.Repository.Id, row.Change.RelativePath, snapshotTagFilter)
                            || !MatchesSnapshotFileWindow(row.Change, modifiedThresholdUtc)
                            || !MatchesSnapshotFileSize(row.Change, minSizeMb, maxSizeMb))
                        {
                            continue;
                        }

                        fileMatchCount++;
                        if (snapshotFileVisible.Count < MaxVisibleFileResults)
                            snapshotFileVisible.Add(row);
                    }
                }
                else
                {
                    foreach (var row in entries)
                    {
                        if (repositoryFilterId.HasValue && row.Repository.Id != repositoryFilterId.Value)
                            continue;

                        if (linkedRepositoryId.HasValue && row.Repository.Id != linkedRepositoryId.Value)
                            continue;

                        if (trackedFormatFilter != "all"
                            && !row.Repository.LinkedFormats.Contains(trackedFormatFilter, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if ((!MatchesEntryText(row, textQuery)
                             && !MatchesFileTagText(row.Repository.Id, row.Entry.RelativePath, textTagQuery))
                            || !MatchesEntryType(row.Entry, entryTypeFilter)
                            || !MatchesExtension(row.Entry, extensionFilter)
                            || !MatchesFileTags(row.Repository.Id, row.Entry.RelativePath, snapshotTagFilter)
                            || !MatchesModifiedWindow(row.Entry, modifiedThresholdUtc)
                            || !MatchesSize(row.Entry, minSizeMb, maxSizeMb))
                        {
                            continue;
                        }

                        fileMatchCount++;
                        if (fileVisible.Count < MaxVisibleFileResults)
                            fileVisible.Add(row);
                    }
                }

                return (
                    RepositoryMatches: repositoryMatches,
                    SnapshotVisible: snapshotVisible,
                    SnapshotMatchCount: snapshotMatchCount,
                    FileVisible: fileVisible,
                    SnapshotFileVisible: snapshotFileVisible,
                    FileMatchCount: fileMatchCount);
            }, ct);

            if (!IsLatestFilterRequest(requestId) || ct.IsCancellationRequested)
                return;

            var repositoryResults = result.RepositoryMatches
                .Select(MapRepositoryCached)
                .ToList();
            var snapshotResults = result.SnapshotVisible
                .Select(MapSnapshotCached)
                .ToList();
            var fileResults = linkedSnapshotId.HasValue
                ? result.SnapshotFileVisible.Select(MapSnapshotFileChange).ToList()
                : result.FileVisible.Select(MapEntryCached).ToList();

            ReplaceCollectionIfChanged(RepositoryResults, repositoryResults);
            ReplaceCollectionIfChanged(SnapshotResults, snapshotResults);

            _matchedRepositoryCount = result.RepositoryMatches.Count;
            _matchedSnapshotCount = result.SnapshotMatchCount;
            _matchedFileCount = result.FileMatchCount;
            _fileResultsLimited = result.FileMatchCount > MaxVisibleFileResults;

            ReplaceCollectionIfChanged(FileResults, fileResults);

            NotifyResultStateChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to apply global search filters");
        }
    }

    private void RebuildFilterOptions()
    {
        _suppressFilterApply = true;
        try
        {
            RebuildRepositoryFilterOptions();

            var selectedTrackedFormat = NormalizeFilter(SelectedTrackedFormatFilter);
            var trackedFormats = _repositorySource
                .SelectMany(repo => repo.LinkedFormats)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ReplaceCollectionIfChanged(
                TrackedFormatFilters,
                ["all", .. trackedFormats]);
            SelectedTrackedFormatFilter = TrackedFormatFilters.Contains(selectedTrackedFormat, StringComparer.OrdinalIgnoreCase)
                ? selectedTrackedFormat
                : "all";

            var selectedExtension = NormalizeExtensionFilter(SelectedExtensionFilter);
            var extensions = _entrySource
                .Where(row => !row.Entry.IsDirectory)
                .Select(row => NormalizeExtensionFilter(row.Entry.Extension ?? string.Empty))
                .Where(value => value != "all")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ReplaceCollectionIfChanged(
                ExtensionFilters,
                ["all", .. extensions]);
            SelectedExtensionFilter = ExtensionFilters.Contains(selectedExtension, StringComparer.OrdinalIgnoreCase)
                ? selectedExtension
                : "all";

            var selectedSnapshotTag = NormalizeTagFilter(SelectedSnapshotTagFilter);
            var snapshotTags = _snapshotSource
                .SelectMany(row => row.Snapshot.Tags ?? Array.Empty<string>())
                .Select(NormalizeTagFilter)
                .Where(value => value != "all")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ReplaceCollectionIfChanged(
                SnapshotTagFilters,
                ["all", .. snapshotTags]);
            SelectedSnapshotTagFilter = SnapshotTagFilters.Contains(selectedSnapshotTag, StringComparer.OrdinalIgnoreCase)
                ? selectedSnapshotTag
                : "all";

            RebuildTagPickerItems();
        }
        finally
        {
            _suppressFilterApply = false;
        }
    }

    private void RebuildFileTags(IReadOnlyList<GlobalSearchTaggedFileDto> taggedFiles)
    {
        _fileTagsByPath.Clear();

        foreach (var taggedFile in taggedFiles)
        {
            var tags = taggedFile.Tags
                .Select(NormalizeTagFilter)
                .Where(tag => tag != "all")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (tags.Length == 0)
                continue;

            _fileTagsByPath[(taggedFile.RepositoryId, taggedFile.RelativePath)] = tags;
        }
    }

    private void RebuildTagPickerItems()
    {
        var query = (TagPickerSearchQuery ?? string.Empty).Trim();
        var tags = SnapshotTagFilters
            .Where(tag => !string.Equals(NormalizeTagFilter(tag), "all", StringComparison.OrdinalIgnoreCase))
            .Where(tag => string.IsNullOrWhiteSpace(query)
                          || tag.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ReplaceCollectionIfChanged(TagPickerItems, tags);
        OnPropertyChanged(nameof(HasTagPickerItems));
    }

    private void RebuildRepositoryFilterOptions()
    {
        var selectedRepositoryId = SelectedRepositoryFilter?.RepositoryId;
        var options = new List<GlobalSearchRepositoryFilterOptionViewModel>(_repositorySource.Count + 1)
        {
            new(null, Loc.T("filter.option.all"))
        };

        options.AddRange(_repositorySource
            .Select(repository => new GlobalSearchRepositoryFilterOptionViewModel(repository.Id, repository.Name)));

        ReplaceCollectionIfChanged(RepositoryFilters, options);

        SelectedRepositoryFilter = RepositoryFilters.FirstOrDefault(option => option.RepositoryId == selectedRepositoryId)
            ?? RepositoryFilters.FirstOrDefault();
    }

    private async Task LoadSavedFiltersAsync(CancellationToken ct = default)
    {
        var presets = await _filterStore.LoadAsync(ct);
        var items = presets
            .Select(preset => new GlobalSearchSavedFilterViewModel(preset))
            .OrderByDescending(f => f.SavedAtUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SyncCollection(SavedFilters, items);
        HasSavedFilters = SavedFilters.Count > 0;
    }

    private async Task PersistSavedFiltersAsync(CancellationToken ct = default)
    {
        var presets = SavedFilters
            .Select(v => v.Preset)
            .ToList();

        await _filterStore.SaveAsync(presets, ct);
    }

    private GlobalSearchFilterPreset BuildCurrentPreset(string name)
        => new()
        {
            Name = name,
            SearchQuery = SearchQuery,
            RepositoryId = SelectedRepositoryFilter?.RepositoryId,
            TrackedFormatFilter = NormalizeFilter(SelectedTrackedFormatFilter),
            EntryTypeFilter = NormalizeFilter(SelectedEntryTypeFilter),
            ExtensionFilter = NormalizeExtensionFilter(SelectedExtensionFilter),
            ModifiedWindowFilter = NormalizeFilter(SelectedModifiedWindowFilter),
            SnapshotTagFilter = NormalizeTagFilter(SelectedSnapshotTagFilter),
            MinSizeMb = MinSizeMb,
            MaxSizeMb = MaxSizeMb,
            SavedAtUtc = DateTime.UtcNow
        };

    private void ApplyPreset(GlobalSearchFilterPreset preset)
    {
        _suppressFilterApply = true;
        try
        {
            SearchQuery = preset.SearchQuery;
            SelectedRepositoryFilter = RepositoryFilters.FirstOrDefault(option => option.RepositoryId == preset.RepositoryId)
                ?? RepositoryFilters.FirstOrDefault();
            SelectedTrackedFormatFilter = EnsureFilterOption(TrackedFormatFilters, NormalizeFilter(preset.TrackedFormatFilter));
            SelectedEntryTypeFilter = EnsureFilterOption(EntryTypeFilters, NormalizeFilter(preset.EntryTypeFilter));
            SelectedExtensionFilter = EnsureFilterOption(ExtensionFilters, NormalizeExtensionFilter(preset.ExtensionFilter));
            SelectedModifiedWindowFilter = EnsureFilterOption(ModifiedWindowFilters, NormalizeFilter(preset.ModifiedWindowFilter));
            SelectedSnapshotTagFilter = EnsureFilterOption(SnapshotTagFilters, NormalizeTagFilter(preset.SnapshotTagFilter));
            MinSizeMb = preset.MinSizeMb;
            MaxSizeMb = preset.MaxSizeMb;
            SavedFilterName = preset.Name;
        }
        finally
        {
            _suppressFilterApply = false;
        }

        ApplyFiltersIfNeeded(debounce: false);
        NotifyFilterChromeChanged();
    }

    private void SortSavedFilters()
    {
        var sorted = SavedFilters
            .OrderByDescending(f => f.SavedAtUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SyncCollection(SavedFilters, sorted);
    }

    private static string EnsureFilterOption(ObservableCollection<string> options, string value)
    {
        if (!options.Contains(value, StringComparer.OrdinalIgnoreCase))
            options.Add(value);

        return value;
    }

    private static void ReplaceCollectionIfChanged<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> items)
    {
        if (CollectionEquals(collection, items))
            return;

        SyncCollection(collection, items);
    }

    private static bool CollectionEquals<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> next)
    {
        if (current.Count != next.Count)
            return false;

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < current.Count; i++)
        {
            if (!comparer.Equals(current[i], next[i]))
                return false;
        }

        return true;
    }

    private void NotifyResultStateChanged()
    {
        OnPropertyChanged(nameof(HasRepositoryResults));
        OnPropertyChanged(nameof(HasSnapshotResults));
        OnPropertyChanged(nameof(HasFileResults));
        OnPropertyChanged(nameof(RepositoryResultsSummary));
        OnPropertyChanged(nameof(SnapshotResultsSummary));
        OnPropertyChanged(nameof(FileResultsSummary));
        NotifyFilterChromeChanged();
    }

    private void NotifyFilterChromeChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(FilterButtonLabel));
    }

    private void NotifyLinkedSelectionChanged()
    {
        OnPropertyChanged(nameof(LinkedSelectionText));
    }

    private void NotifySectionLayoutStateChanged()
    {
        OnPropertyChanged(nameof(HasAnyVisibleSection));
        OnPropertyChanged(nameof(VisibleSectionCount));
        OnPropertyChanged(nameof(SectionButtonLabel));
        OnPropertyChanged(nameof(RepositorySectionStateText));
        OnPropertyChanged(nameof(SnapshotSectionStateText));
        OnPropertyChanged(nameof(FileSectionStateText));
        OnPropertyChanged(nameof(RepositorySectionRow));
        OnPropertyChanged(nameof(RepositorySectionColumn));
        OnPropertyChanged(nameof(RepositorySectionRowSpan));
        OnPropertyChanged(nameof(RepositorySectionColumnSpan));
        OnPropertyChanged(nameof(SnapshotSectionRow));
        OnPropertyChanged(nameof(SnapshotSectionColumn));
        OnPropertyChanged(nameof(SnapshotSectionRowSpan));
        OnPropertyChanged(nameof(SnapshotSectionColumnSpan));
        OnPropertyChanged(nameof(FileSectionRow));
        OnPropertyChanged(nameof(FileSectionColumn));
        OnPropertyChanged(nameof(FileSectionRowSpan));
        OnPropertyChanged(nameof(FileSectionColumnSpan));
    }

    private SectionLayout ResolveRepositoryLayout()
    {
        if (IsRepositoriesSectionVisible && !IsSnapshotsSectionVisible && !IsFilesSectionVisible)
            return new(0, 0, 2, 2);

        if (IsRepositoriesSectionVisible && IsSnapshotsSectionVisible && !IsFilesSectionVisible)
            return new(0, 0, 1, 2);

        if (IsRepositoriesSectionVisible && !IsSnapshotsSectionVisible && IsFilesSectionVisible)
            return new(0, 0, 2, 1);

        return new(0, 0, 1, 1);
    }

    private SectionLayout ResolveSnapshotLayout()
    {
        if (!IsRepositoriesSectionVisible && IsSnapshotsSectionVisible && !IsFilesSectionVisible)
            return new(0, 0, 2, 2);

        if (IsRepositoriesSectionVisible && IsSnapshotsSectionVisible && !IsFilesSectionVisible)
            return new(1, 0, 1, 2);

        if (!IsRepositoriesSectionVisible && IsSnapshotsSectionVisible && IsFilesSectionVisible)
            return new(0, 0, 2, 1);

        return new(1, 0, 1, 1);
    }

    private SectionLayout ResolveFileLayout()
    {
        if (!IsRepositoriesSectionVisible && !IsSnapshotsSectionVisible && IsFilesSectionVisible)
            return new(0, 0, 2, 2);

        if (IsRepositoriesSectionVisible && !IsSnapshotsSectionVisible && IsFilesSectionVisible)
            return new(0, 1, 2, 1);

        if (!IsRepositoriesSectionVisible && IsSnapshotsSectionVisible && IsFilesSectionVisible)
            return new(0, 1, 2, 1);

        return new(0, 1, 2, 1);
    }

    private int GetActiveFilterCount()
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(SearchQuery))
            count++;
        if (SelectedRepositoryFilter?.RepositoryId is not null)
            count++;
        if (!string.Equals(SelectedTrackedFormatFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.Equals(SelectedEntryTypeFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.Equals(SelectedExtensionFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.Equals(SelectedModifiedWindowFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.Equals(SelectedSnapshotTagFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.IsNullOrWhiteSpace(MinSizeMb))
            count++;
        if (!string.IsNullOrWhiteSpace(MaxSizeMb))
            count++;

        return count;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ResetResultCaches();
        RebuildRepositoryFilterOptions();
        ApplyFiltersIfNeeded(debounce: false);
        if (string.IsNullOrWhiteSpace(LoadingStatusText))
            LastUpdatedText = HasLoadedData
                ? Loc.F("search.last_updated_format", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                : string.Empty;

        OnPropertyChanged(nameof(RepositoryResultsSummary));
        OnPropertyChanged(nameof(SnapshotResultsSummary));
        OnPropertyChanged(nameof(FileResultsSummary));
        OnPropertyChanged(nameof(HasErrorMessage));
        OnPropertyChanged(nameof(HasStatusMessage));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(LoadingOverlayTitle));
        OnPropertyChanged(nameof(LoadingOverlayDetail));
        NotifyFilterChromeChanged();
        NotifyLinkedSelectionChanged();
        NotifySectionLayoutStateChanged();
    }

    private async Task RunTransientActionAsync(
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

    private (long RequestId, CancellationToken Token) BeginLoadRequest()
    {
        CancelActiveFilter();
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        var requestId = Interlocked.Increment(ref _loadRequestId);
        return (requestId, cts.Token);
    }

    private (long RequestId, CancellationToken Token) BeginFilterRequest()
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _filterCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        var requestId = Interlocked.Increment(ref _filterRequestId);
        return (requestId, cts.Token);
    }

    private (long RequestId, CancellationToken Token) BeginLinkedSnapshotRequest()
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _linkedSnapshotCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        var requestId = Interlocked.Increment(ref _linkedSnapshotRequestId);
        return (requestId, cts.Token);
    }

    private void CancelActiveLoad()
    {
        var cts = Interlocked.Exchange(ref _loadCts, null);
        cts?.Cancel();
        cts?.Dispose();
        CancelActiveFilter();
        CancelActiveLinkedSnapshotLoad();
    }

    private bool IsLatestLoadRequest(long requestId)
        => requestId == Interlocked.Read(ref _loadRequestId);

    private bool IsLatestFilterRequest(long requestId)
        => requestId == Interlocked.Read(ref _filterRequestId);

    private bool IsLatestLinkedSnapshotRequest(long requestId)
        => requestId == Interlocked.Read(ref _linkedSnapshotRequestId);

    private void CancelActiveFilter()
    {
        var cts = Interlocked.Exchange(ref _filterCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    private void CancelActiveLinkedSnapshotLoad()
    {
        var cts = Interlocked.Exchange(ref _linkedSnapshotCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    private Task<TResponse> SendIsolatedAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        => _scopeExecutor.ExecuteAsync<IMediator, TResponse>((mediator, token) => mediator.Send(request, token), ct);

    private GlobalSearchRepositoryResultItemViewModel MapRepositoryCached(RepositoryDto repository)
    {
        if (_repositoryResultCache.TryGetValue(repository.Id, out var cached))
            return cached;

        var mapped = MapRepository(repository);
        _repositoryResultCache[repository.Id] = mapped;
        return mapped;
    }

    private GlobalSearchFileResultItemViewModel MapEntryCached(GlobalSearchIndexedEntry row)
    {
        var cacheKey = (row.Repository.Id, row.Entry.RelativePath);
        if (_fileResultCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var mapped = MapEntry(row);
        _fileResultCache[cacheKey] = mapped;
        return mapped;
    }

    private GlobalSearchSnapshotResultItemViewModel MapSnapshotCached(GlobalSearchIndexedSnapshot row)
    {
        var cacheKey = (row.Repository.Id, row.Snapshot.SnapshotId);
        if (_snapshotResultCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var mapped = MapSnapshot(row);
        _snapshotResultCache[cacheKey] = mapped;
        return mapped;
    }

    private GlobalSearchRepositoryResultItemViewModel MapRepository(RepositoryDto repository)
    {
        var formats = repository.LinkedFormats.Count == 0
            ? string.Empty
            : string.Join(", ", repository.LinkedFormats.OrderBy(v => v, StringComparer.OrdinalIgnoreCase));

        var metrics = Loc.F(
            "search.repository_metrics_format",
            repository.FileCount,
            repository.ChangedVersionCount,
            FormatBytes(repository.TotalSizeBytes));

        var status = string.IsNullOrWhiteSpace(repository.CloudSync?.LastStatus)
            ? Loc.T("common.not_available_short")
            : FormatCloudSyncStatus(repository.CloudSync!.LastStatus!);
        var lastScanned = repository.LastScannedAt.HasValue
            ? repository.LastScannedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : Loc.T("common.not_available_short");

        return new GlobalSearchRepositoryResultItemViewModel(
            repository.Id,
            repository.Name,
            repository.DirectoryPath,
            repository.Description ?? string.Empty,
            formats,
            metrics,
            status,
            lastScanned,
            !string.IsNullOrWhiteSpace(repository.Description),
            !string.IsNullOrWhiteSpace(formats));
    }

    private static string FormatCloudSyncStatus(string status)
    {
        var normalized = status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "queued" => Loc.T("dashboard.sync.queued"),
            "syncing" or "syncing_prepare" => Loc.T("dashboard.sync.syncing_prepare"),
            "syncing_snapshot" => Loc.T("dashboard.sync.syncing_snapshot"),
            "syncing_finalize" => Loc.T("dashboard.sync.syncing_finalize"),
            _ when normalized.StartsWith("syncing_upload", StringComparison.Ordinal) => Loc.T("dashboard.sync.syncing"),
            "offline_retry" => Loc.T("dashboard.sync.offline_retry"),
            "retrying" => Loc.T("dashboard.sync.retrying"),
            "auth_required" => Loc.T("dashboard.sync.auth_required"),
            "conflict" => Loc.T("dashboard.sync.conflict"),
            "dead_letter" => Loc.T("dashboard.sync.dead_letter"),
            "failed" => Loc.T("dashboard.sync.failed"),
            "linked" => Loc.T("dashboard.sync.linked"),
            "paused" => Loc.T("dashboard.sync.paused"),
            "cancelled" => Loc.T("dashboard.sync.cancelled"),
            "skipped" => Loc.T("dashboard.sync.skipped"),
            _ when normalized.StartsWith("synced", StringComparison.Ordinal) => Loc.T("dashboard.sync.synced"),
            _ => Loc.F("dashboard.sync.unknown_status", HumanizeStatusToken(status))
        };
    }

    private static string HumanizeStatusToken(string status)
    {
        var text = status.Trim().Replace('_', ' ').Replace('-', ' ');
        return string.Join(" ", text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()));
    }

    private GlobalSearchFileResultItemViewModel MapEntry(GlobalSearchIndexedEntry row)
    {
        var parentPath = NormalizeParent(row.Entry.ParentRelativePath) ?? string.Empty;
        var extension = row.Entry.IsDirectory ? string.Empty : NormalizeExtensionFilter(row.Entry.Extension ?? string.Empty);
        var tags = GetFileTagsText(row.Repository.Id, row.Entry.RelativePath);

        return new GlobalSearchFileResultItemViewModel(
            row.Repository.Id,
            row.Repository.Name,
            row.Entry.RelativePath,
            row.Entry.IsDirectory,
            row.Entry.Name,
            parentPath,
            row.Entry.IsDirectory ? Loc.T("filter.option.folders") : Loc.T("filter.option.files"),
            extension,
            row.Entry.IsDirectory ? "-" : FormatBytes(row.Entry.SizeBytes),
            row.Entry.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            row.Repository.Name,
            !string.IsNullOrWhiteSpace(extension) && extension != "all",
            !string.IsNullOrWhiteSpace(parentPath),
            tags,
            !string.IsNullOrWhiteSpace(tags));
    }

    private GlobalSearchSnapshotResultItemViewModel MapSnapshot(GlobalSearchIndexedSnapshot row)
    {
        var title = ResolveSnapshotTitle(row.Snapshot.Title, row.Snapshot.Trigger);
        var triggerText = Humanize(row.Snapshot.Trigger);
        var tags = string.Join(" ", (row.Snapshot.Tags ?? Array.Empty<string>()).Select(tag => "#" + tag));

        return new GlobalSearchSnapshotResultItemViewModel(
            row.Repository.Id,
            row.Snapshot.SnapshotId,
            row.Repository.Name,
            title,
            triggerText,
            row.Snapshot.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            Loc.F("search.snapshot_changed_files_format", row.Snapshot.ChangedFilesCount),
            tags,
            !string.IsNullOrWhiteSpace(title),
            !string.IsNullOrWhiteSpace(tags));
    }

    private GlobalSearchFileResultItemViewModel MapSnapshotFileChange(GlobalSearchIndexedSnapshotFileChange row)
    {
        var parentPath = NormalizeParent(Path.GetDirectoryName(row.Change.RelativePath)?.Replace(Path.DirectorySeparatorChar, '/')) ?? string.Empty;
        var extension = NormalizeExtensionFilter(Path.GetExtension(row.Change.Name));
        var tags = GetFileTagsText(row.Repository.Id, row.Change.RelativePath);

        return new GlobalSearchFileResultItemViewModel(
            row.Repository.Id,
            row.Repository.Name,
            row.Change.RelativePath,
            false,
            row.Change.Name,
            parentPath,
            Humanize(row.Change.ChangeKind),
            extension,
            FormatBytes(Math.Max(row.Change.CurrentSizeBytes, row.Change.PreviousSizeBytes)),
            row.Change.VersionCreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            row.Repository.Name,
            !string.IsNullOrWhiteSpace(extension) && extension != "all",
            !string.IsNullOrWhiteSpace(parentPath),
            tags,
            !string.IsNullOrWhiteSpace(tags));
    }

    private string GetFileTagsText(int repositoryId, string relativePath)
        => _fileTagsByPath.TryGetValue((repositoryId, relativePath), out var tags)
            ? string.Join(" ", tags.Select(tag => "#" + tag))
            : string.Empty;

    private static bool MatchesRepositoryText(RepositoryDto repository, string textQuery)
    {
        if (string.IsNullOrWhiteSpace(textQuery))
            return true;

        return repository.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || repository.DirectoryPath.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(repository.Description)
                   && repository.Description.Contains(textQuery, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesEntryText(GlobalSearchIndexedEntry row, string textQuery)
    {
        if (string.IsNullOrWhiteSpace(textQuery))
            return true;

        return row.Entry.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Entry.RelativePath.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Repository.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Repository.DirectoryPath.Contains(textQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSnapshotFileText(GlobalSearchIndexedSnapshotFileChange row, string textQuery)
    {
        if (string.IsNullOrWhiteSpace(textQuery))
            return true;

        return row.Change.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Change.RelativePath.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Change.ChangeKind.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Repository.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Repository.DirectoryPath.Contains(textQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSnapshotText(GlobalSearchIndexedSnapshot row, string textQuery)
    {
        if (string.IsNullOrWhiteSpace(textQuery))
            return true;

        return (!string.IsNullOrWhiteSpace(row.Snapshot.Title)
                   && row.Snapshot.Title.Contains(textQuery, StringComparison.OrdinalIgnoreCase))
               || row.Snapshot.Kind.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Snapshot.Trigger.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || (row.Snapshot.Tags?.Any(tag => tag.Contains(textQuery, StringComparison.OrdinalIgnoreCase)) ?? false)
               || row.Repository.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Repository.DirectoryPath.Contains(textQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSnapshotTag(RepositorySnapshotHistoryItemDto snapshot, string snapshotTagFilter)
    {
        if (snapshotTagFilter == "all")
            return true;

        return (snapshot.Tags?.Any(tag => string.Equals(NormalizeTagFilter(tag), snapshotTagFilter, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static bool MatchesSnapshotTagText(RepositorySnapshotHistoryItemDto snapshot, string textTagQuery)
    {
        if (string.IsNullOrWhiteSpace(textTagQuery))
            return false;

        return snapshot.Tags?.Any(tag => tag.Contains(textTagQuery, StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    private bool MatchesFileTags(int repositoryId, string relativePath, string snapshotTagFilter)
    {
        if (snapshotTagFilter == "all")
            return true;

        return _fileTagsByPath.TryGetValue((repositoryId, relativePath), out var tags)
               && tags.Any(tag => string.Equals(NormalizeTagFilter(tag), snapshotTagFilter, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesFileTagText(int repositoryId, string relativePath, string textQuery)
    {
        if (string.IsNullOrWhiteSpace(textQuery))
            return false;

        return _fileTagsByPath.TryGetValue((repositoryId, relativePath), out var tags)
               && tags.Any(tag => tag.Contains(textQuery, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesEntryType(RepositoryScanEntryDto entry, string entryTypeFilter)
    {
        return entryTypeFilter switch
        {
            "files" => !entry.IsDirectory,
            "folders" => entry.IsDirectory,
            _ => true
        };
    }

    private static bool MatchesSnapshotFileType(string entryTypeFilter)
        => entryTypeFilter is not "folders";

    private static bool MatchesExtension(RepositoryScanEntryDto entry, string extensionFilter)
    {
        if (extensionFilter == "all")
            return true;

        return !entry.IsDirectory
               && string.Equals(NormalizeExtensionFilter(entry.Extension ?? string.Empty), extensionFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSnapshotFileExtension(RepositorySnapshotFileChangeDto change, string extensionFilter)
    {
        if (extensionFilter == "all")
            return true;

        return string.Equals(NormalizeExtensionFilter(Path.GetExtension(change.Name)), extensionFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesModifiedWindow(RepositoryScanEntryDto entry, DateTime thresholdUtc)
        => thresholdUtc == DateTime.MinValue || entry.LastWriteUtc >= thresholdUtc;

    private static bool MatchesSnapshotWindow(RepositorySnapshotHistoryItemDto snapshot, DateTime thresholdUtc)
        => thresholdUtc == DateTime.MinValue || snapshot.CreatedAtUtc >= thresholdUtc;

    private static bool MatchesSnapshotFileWindow(RepositorySnapshotFileChangeDto change, DateTime thresholdUtc)
        => thresholdUtc == DateTime.MinValue || change.VersionCreatedAtUtc >= thresholdUtc;

    private static DateTime ResolveModifiedWindowThreshold(string modifiedWindowFilter)
    {
        return modifiedWindowFilter switch
        {
            "24h" => DateTime.UtcNow.AddHours(-24),
            "7d" => DateTime.UtcNow.AddDays(-7),
            "30d" => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.MinValue
        };
    }

    private static bool MatchesSize(RepositoryScanEntryDto entry, double? minSizeMb, double? maxSizeMb)
    {
        if (entry.IsDirectory)
            return !minSizeMb.HasValue && !maxSizeMb.HasValue;

        var sizeMb = entry.SizeBytes / (1024d * 1024d);
        if (minSizeMb is > 0 && sizeMb < minSizeMb.Value)
            return false;
        if (maxSizeMb is > 0 && sizeMb > maxSizeMb.Value)
            return false;

        return true;
    }

    private static bool MatchesSnapshotFileSize(RepositorySnapshotFileChangeDto change, double? minSizeMb, double? maxSizeMb)
    {
        var sizeMb = Math.Max(change.CurrentSizeBytes, change.PreviousSizeBytes) / (1024d * 1024d);
        if (minSizeMb is > 0 && sizeMb < minSizeMb.Value)
            return false;
        if (maxSizeMb is > 0 && sizeMb > maxSizeMb.Value)
            return false;

        return true;
    }

    private static string NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) ? "all" : value.Trim().ToLowerInvariant();

    private static string NormalizeExtensionFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "all";

        var normalized = value.Trim();
        if (string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase))
            return "all";

        return normalized.StartsWith('.') ? normalized.ToLowerInvariant() : "." + normalized.ToLowerInvariant();
    }

    private static string NormalizeTagFilter(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('#').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) || normalized == "all"
            ? "all"
            : normalized;
    }

    private static string NormalizeSearchTagText(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["tag:".Length..];

        return normalized.Trim().TrimStart('#');
    }

    private static double? ParseNullableDouble(string? value)
        => double.TryParse(value, out var parsed) && parsed >= 0 ? parsed : null;

    private static string? NormalizeParent(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024d):F1} MB";
        return $"{bytes / (1024d * 1024d * 1024d):F1} GB";
    }

    private static string Humanize(string value)
    {
        var text = (value ?? string.Empty).Replace('_', ' ').Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Loc.T("common.not_available_short");

        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string ResolveSnapshotTitle(string? title, string? trigger)
    {
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        return !string.IsNullOrWhiteSpace(trigger)
               && trigger.StartsWith("initial_snapshot", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("snapshot.initial_name")
            : string.Empty;
    }

    private void ResetVisibleResults()
    {
        RepositoryResults.Clear();
        FileResults.Clear();
        SnapshotResults.Clear();
        _matchedRepositoryCount = 0;
        _matchedFileCount = 0;
        _matchedSnapshotCount = 0;
        _fileResultsLimited = false;
        NotifyResultStateChanged();
    }

    private void ResetResultCaches()
    {
        _repositoryResultCache.Clear();
        _fileResultCache.Clear();
        _snapshotResultCache.Clear();
    }

    private static void SyncCollection<T>(ObservableCollection<T> collection, IReadOnlyList<T> items)
    {
        var comparer = EqualityComparer<T>.Default;
        var sharedPrefix = 0;
        var maxPrefix = Math.Min(collection.Count, items.Count);
        while (sharedPrefix < maxPrefix && comparer.Equals(collection[sharedPrefix], items[sharedPrefix]))
            sharedPrefix++;

        var sharedSuffix = 0;
        var maxSuffix = Math.Min(collection.Count - sharedPrefix, items.Count - sharedPrefix);
        while (sharedSuffix < maxSuffix &&
               comparer.Equals(
                   collection[collection.Count - 1 - sharedSuffix],
                   items[items.Count - 1 - sharedSuffix]))
        {
            sharedSuffix++;
        }

        var removeStart = sharedPrefix;
        var removeCount = collection.Count - sharedPrefix - sharedSuffix;
        for (var i = 0; i < removeCount; i++)
            collection.RemoveAt(removeStart);

        var insertCount = items.Count - sharedPrefix - sharedSuffix;
        for (var i = 0; i < insertCount; i++)
            collection.Insert(removeStart + i, items[sharedPrefix + i]);
    }

    private readonly record struct SectionLayout(int Row, int Column, int RowSpan, int ColumnSpan);
}
