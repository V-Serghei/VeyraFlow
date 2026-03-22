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
using Veyra.Application.Queries.Search;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Execution;

namespace Veyra.Desktop.ViewModels.Pages.Search;

public sealed partial class GlobalSearchViewModel : ObservableObject
{
    private const int MaxVisibleFileResults = 500;
    private const int MaxVisibleSnapshotResults = 200;
    private const int FilterDebounceMs = 120;

    private readonly IServiceScopeExecutor _scopeExecutor;
    private readonly ILogger<GlobalSearchViewModel> _log;
    private readonly LocalizationManager _localization;
    private readonly List<RepositoryDto> _repositorySource = [];
    private readonly List<GlobalSearchIndexedEntry> _entrySource = [];
    private readonly List<GlobalSearchIndexedSnapshot> _snapshotSource = [];
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
    private long _filterRequestId;
    private bool _suppressFilterApply;

    public event Action? BackRequested;
    public event Func<int, Task>? OpenRepositoryRequested;
    public event Func<int, Task>? OpenRepositorySettingsRequested;
    public event Func<int, string, bool, Task>? OpenEntryRequested;
    public event Func<int, long, Task>? OpenSnapshotRequested;

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
    [ObservableProperty] private string _lastUpdatedText = string.Empty;
    [ObservableProperty] private string _loadingStatusText = string.Empty;

    public ObservableCollection<GlobalSearchRepositoryFilterOptionViewModel> RepositoryFilters { get; } = [];
    public ObservableCollection<string> TrackedFormatFilters { get; } = ["all"];
    public ObservableCollection<string> EntryTypeFilters { get; } = ["all", "files", "folders"];
    public ObservableCollection<string> ExtensionFilters { get; } = ["all"];
    public ObservableCollection<string> ModifiedWindowFilters { get; } = ["all", "24h", "7d", "30d"];
    public ObservableCollection<string> SnapshotTagFilters { get; } = ["all"];
    public ObservableCollection<GlobalSearchRepositoryResultItemViewModel> RepositoryResults { get; } = [];
    public ObservableCollection<GlobalSearchFileResultItemViewModel> FileResults { get; } = [];
    public ObservableCollection<GlobalSearchSnapshotResultItemViewModel> SnapshotResults { get; } = [];

    public bool HasRepositoryResults => RepositoryResults.Count > 0;
    public bool HasFileResults => FileResults.Count > 0;
    public bool HasSnapshotResults => SnapshotResults.Count > 0;
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool CanRefresh => !IsLoading && !IsRefreshing && !IsTransientActionBusy;
    public bool IsBusy => IsLoading || IsRefreshing || IsTransientActionBusy;
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
        ILogger<GlobalSearchViewModel> log)
    {
        _scopeExecutor = scopeExecutor;
        _log = log;
        _localization = LocalizationManager.Instance;
        _localization.LanguageChanged += OnLanguageChanged;
        RebuildRepositoryFilterOptions();
        LastUpdatedText = Loc.T("common.not_available_short");
    }

    partial void OnSearchQueryChanged(string value) => ApplyFiltersIfNeeded();
    partial void OnSelectedRepositoryFilterChanged(GlobalSearchRepositoryFilterOptionViewModel? value) => ApplyFiltersIfNeeded();
    partial void OnSelectedTrackedFormatFilterChanged(string value) => ApplyFiltersIfNeeded();
    partial void OnSelectedEntryTypeFilterChanged(string value) => ApplyFiltersIfNeeded();
    partial void OnSelectedExtensionFilterChanged(string value) => ApplyFiltersIfNeeded();
    partial void OnSelectedModifiedWindowFilterChanged(string value) => ApplyFiltersIfNeeded();
    partial void OnSelectedSnapshotTagFilterChanged(string value) => ApplyFiltersIfNeeded();
    partial void OnMinSizeMbChanged(string value) => ApplyFiltersIfNeeded();
    partial void OnMaxSizeMbChanged(string value) => ApplyFiltersIfNeeded();

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
        var minSizeMb = ParseNullableDouble(MinSizeMb);
        var maxSizeMb = ParseNullableDouble(MaxSizeMb);
        var repositories = _repositorySource.ToArray();
        var entries = _entrySource.ToArray();
        var snapshots = _snapshotSource.ToArray();
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

                    if (trackedFormatFilter != "all"
                        && !row.Repository.LinkedFormats.Contains(trackedFormatFilter, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!MatchesSnapshotText(row, textQuery)
                        || !MatchesSnapshotTag(row.Snapshot, snapshotTagFilter)
                        || !MatchesSnapshotWindow(row.Snapshot, modifiedThresholdUtc))
                    {
                        continue;
                    }

                    snapshotMatchCount++;
                    if (snapshotVisible.Count < MaxVisibleSnapshotResults)
                        snapshotVisible.Add(row);
                }

                var fileVisible = new List<GlobalSearchIndexedEntry>(Math.Min(entries.Length, MaxVisibleFileResults));
                var fileMatchCount = 0;
                foreach (var row in entries)
                {
                    if (repositoryFilterId.HasValue && row.Repository.Id != repositoryFilterId.Value)
                        continue;

                    if (trackedFormatFilter != "all"
                        && !row.Repository.LinkedFormats.Contains(trackedFormatFilter, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!MatchesEntryText(row, textQuery)
                        || !MatchesEntryType(row.Entry, entryTypeFilter)
                        || !MatchesExtension(row.Entry, extensionFilter)
                        || !MatchesModifiedWindow(row.Entry, modifiedThresholdUtc)
                        || !MatchesSize(row.Entry, minSizeMb, maxSizeMb))
                    {
                        continue;
                    }

                    fileMatchCount++;
                    if (fileVisible.Count < MaxVisibleFileResults)
                        fileVisible.Add(row);
                }

                return (
                    RepositoryMatches: repositoryMatches,
                    SnapshotVisible: snapshotVisible,
                    SnapshotMatchCount: snapshotMatchCount,
                    FileVisible: fileVisible,
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
            var fileResults = result.FileVisible
                .Select(MapEntryCached)
                .ToList();

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
        }
        finally
        {
            _suppressFilterApply = false;
        }
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
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ResetResultCaches();
        RebuildRepositoryFilterOptions();
        ApplyFiltersIfNeeded(debounce: false);
        if (string.IsNullOrWhiteSpace(LoadingStatusText))
            LastUpdatedText = HasLoadedData
                ? Loc.F("search.last_updated_format", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                : Loc.T("common.not_available_short");

        OnPropertyChanged(nameof(RepositoryResultsSummary));
        OnPropertyChanged(nameof(SnapshotResultsSummary));
        OnPropertyChanged(nameof(FileResultsSummary));
        OnPropertyChanged(nameof(HasErrorMessage));
        OnPropertyChanged(nameof(HasStatusMessage));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(LoadingOverlayTitle));
        OnPropertyChanged(nameof(LoadingOverlayDetail));
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

    private void CancelActiveLoad()
    {
        var cts = Interlocked.Exchange(ref _loadCts, null);
        cts?.Cancel();
        cts?.Dispose();
        CancelActiveFilter();
    }

    private bool IsLatestLoadRequest(long requestId)
        => requestId == Interlocked.Read(ref _loadRequestId);

    private bool IsLatestFilterRequest(long requestId)
        => requestId == Interlocked.Read(ref _filterRequestId);

    private void CancelActiveFilter()
    {
        var cts = Interlocked.Exchange(ref _filterCts, null);
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
            repository.VersionCount,
            FormatBytes(repository.TotalSizeBytes));

        var status = string.IsNullOrWhiteSpace(repository.CloudSync?.LastStatus)
            ? Loc.T("common.not_available_short")
            : repository.CloudSync!.LastStatus!.Replace('_', ' ');
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

    private GlobalSearchFileResultItemViewModel MapEntry(GlobalSearchIndexedEntry row)
    {
        var parentPath = NormalizeParent(row.Entry.ParentRelativePath) ?? string.Empty;
        var extension = row.Entry.IsDirectory ? string.Empty : NormalizeExtensionFilter(row.Entry.Extension ?? string.Empty);

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
            !string.IsNullOrWhiteSpace(parentPath));
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

    private static bool MatchesEntryType(RepositoryScanEntryDto entry, string entryTypeFilter)
    {
        return entryTypeFilter switch
        {
            "files" => !entry.IsDirectory,
            "folders" => entry.IsDirectory,
            _ => true
        };
    }

    private static bool MatchesExtension(RepositoryScanEntryDto entry, string extensionFilter)
    {
        if (extensionFilter == "all")
            return true;

        return !entry.IsDirectory
               && string.Equals(NormalizeExtensionFilter(entry.Extension ?? string.Empty), extensionFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesModifiedWindow(RepositoryScanEntryDto entry, DateTime thresholdUtc)
        => thresholdUtc == DateTime.MinValue || entry.LastWriteUtc >= thresholdUtc;

    private static bool MatchesSnapshotWindow(RepositorySnapshotHistoryItemDto snapshot, DateTime thresholdUtc)
        => thresholdUtc == DateTime.MinValue || snapshot.CreatedAtUtc >= thresholdUtc;

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
}
