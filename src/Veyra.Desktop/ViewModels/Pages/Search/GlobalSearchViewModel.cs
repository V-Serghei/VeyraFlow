using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.DTOs;
using Veyra.Application.Queries;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Pages.Search;

public sealed record GlobalSearchRepositoryFilterOptionViewModel(int? RepositoryId, string Label);

public sealed record GlobalSearchRepositoryResultItemViewModel(
    int RepositoryId,
    string Name,
    string DirectoryPath,
    string Description,
    string FormatsText,
    string MetricsText,
    string StatusText,
    string LastScannedText,
    bool HasDescription,
    bool HasFormats);

public sealed record GlobalSearchFileResultItemViewModel(
    int RepositoryId,
    string RepositoryName,
    string RelativePath,
    bool IsDirectory,
    string Name,
    string ParentPath,
    string KindText,
    string ExtensionText,
    string SizeText,
    string ModifiedText,
    string RepositoryBadgeText,
    bool HasExtension,
    bool HasParentPath);

public sealed record GlobalSearchSnapshotResultItemViewModel(
    int RepositoryId,
    long SnapshotId,
    string RepositoryName,
    string Title,
    string TriggerText,
    string CreatedText,
    string ChangedFilesText,
    string TagsText,
    bool HasTitle,
    bool HasTags);

public sealed partial class GlobalSearchViewModel : ObservableObject
{
    private const int MaxVisibleFileResults = 500;

    private sealed record IndexedEntry(RepositoryDto Repository, RepositoryScanEntryDto Entry);
    private sealed record IndexedSnapshot(RepositoryDto Repository, RepositorySnapshotHistoryItemDto Snapshot);

    private readonly IMediator _mediator;
    private readonly ILogger<GlobalSearchViewModel> _log;
    private readonly LocalizationManager _localization;
    private readonly List<RepositoryDto> _repositorySource = [];
    private readonly List<IndexedEntry> _entrySource = [];
    private readonly List<IndexedSnapshot> _snapshotSource = [];
    private int _matchedRepositoryCount;
    private int _matchedFileCount;
    private int _matchedSnapshotCount;
    private bool _fileResultsLimited;

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
        IMediator mediator,
        ILogger<GlobalSearchViewModel> log)
    {
        _mediator = mediator;
        _log = log;
        _localization = LocalizationManager.Instance;
        _localization.LanguageChanged += OnLanguageChanged;
        RebuildRepositoryFilterOptions();
        LastUpdatedText = Loc.T("common.not_available_short");
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilters();
    partial void OnSelectedRepositoryFilterChanged(GlobalSearchRepositoryFilterOptionViewModel? value) => ApplyFilters();
    partial void OnSelectedTrackedFormatFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedEntryTypeFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedExtensionFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedModifiedWindowFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedSnapshotTagFilterChanged(string value) => ApplyFilters();
    partial void OnMinSizeMbChanged(string value) => ApplyFilters();
    partial void OnMaxSizeMbChanged(string value) => ApplyFilters();

    public async Task LoadAsync(bool forceRefresh = false)
    {
        if (IsLoading || (!forceRefresh && (IsRefreshing || HasLoadedData)))
        {
            if (!forceRefresh)
                ApplyFilters();

            return;
        }

        var failedRepositories = new List<string>();

        try
        {
            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = string.Empty;
            LoadingStatusText = Loc.T("search.loading_prepare");

            var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
            _repositorySource.Clear();
            _repositorySource.AddRange(repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase));

            _entrySource.Clear();
            _snapshotSource.Clear();
            for (var index = 0; index < _repositorySource.Count; index++)
            {
                var repository = _repositorySource[index];
                LoadingStatusText = Loc.F("search.loading_progress_format", index + 1, _repositorySource.Count, repository.Name);

                try
                {
                    var entries = await _mediator.Send(new GetRepositoryLatestEntriesQuery(repository.Id));
                    _entrySource.AddRange(entries.Select(entry => new IndexedEntry(repository, entry)));

                    var snapshots = await _mediator.Send(new GetRepositorySnapshotHistoryQuery(repository.Id, 60));
                    _snapshotSource.AddRange(snapshots.Select(snapshot => new IndexedSnapshot(repository, snapshot)));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Global search failed to load latest entries. RepositoryId {RepositoryId}", repository.Id);
                    failedRepositories.Add(repository.Name);
                }
            }

            RebuildFilterOptions();
            ApplyFilters();
            LastUpdatedText = Loc.F("search.last_updated_format", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            StatusMessage = Loc.F("search.refresh_done", _repositorySource.Count, _entrySource.Count);

            if (failedRepositories.Count > 0)
            {
                ErrorMessage = Loc.F(
                    "search.partial_load_warning",
                    failedRepositories.Count,
                    string.Join(", ", failedRepositories.Take(3)));
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load global search index");
            ErrorMessage = Loc.T("search.load_failed");
            RepositoryResults.Clear();
            FileResults.Clear();
            SnapshotResults.Clear();
            _matchedRepositoryCount = 0;
            _matchedFileCount = 0;
            _matchedSnapshotCount = 0;
            _fileResultsLimited = false;
            NotifyResultStateChanged();
        }
        finally
        {
            LoadingStatusText = string.Empty;
            IsLoading = false;
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
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void ClearFilters()
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
        StatusMessage = string.Empty;
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

    private void ApplyFilters()
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

        if (minSizeMb is > 0 && maxSizeMb is > 0 && minSizeMb > maxSizeMb)
            (minSizeMb, maxSizeMb) = (maxSizeMb, minSizeMb);

        var repositoryMatches = _repositorySource
            .Where(repo => !repositoryFilterId.HasValue || repo.Id == repositoryFilterId.Value)
            .Where(repo => trackedFormatFilter == "all" || repo.LinkedFormats.Contains(trackedFormatFilter, StringComparer.OrdinalIgnoreCase))
            .Where(repo => MatchesRepositoryText(repo, textQuery))
            .OrderBy(repo => repo.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RepositoryResults.Clear();
        foreach (var repository in repositoryMatches)
            RepositoryResults.Add(MapRepository(repository));

        var snapshotMatches = _snapshotSource
            .Where(row => !repositoryFilterId.HasValue || row.Repository.Id == repositoryFilterId.Value)
            .Where(row => trackedFormatFilter == "all" || row.Repository.LinkedFormats.Contains(trackedFormatFilter, StringComparer.OrdinalIgnoreCase))
            .Where(row => MatchesSnapshotText(row, textQuery))
            .Where(row => MatchesSnapshotTag(row.Snapshot, snapshotTagFilter))
            .Where(row => MatchesSnapshotWindow(row.Snapshot, modifiedWindowFilter))
            .OrderByDescending(row => row.Snapshot.CreatedAtUtc)
            .ToList();

        SnapshotResults.Clear();
        foreach (var snapshot in snapshotMatches.Take(200))
            SnapshotResults.Add(MapSnapshot(snapshot));

        var fileMatches = _entrySource
            .Where(row => !repositoryFilterId.HasValue || row.Repository.Id == repositoryFilterId.Value)
            .Where(row => trackedFormatFilter == "all" || row.Repository.LinkedFormats.Contains(trackedFormatFilter, StringComparer.OrdinalIgnoreCase))
            .Where(row => MatchesEntryText(row, textQuery))
            .Where(row => MatchesEntryType(row.Entry, entryTypeFilter))
            .Where(row => MatchesExtension(row.Entry, extensionFilter))
            .Where(row => MatchesModifiedWindow(row.Entry, modifiedWindowFilter))
            .Where(row => MatchesSize(row.Entry, minSizeMb, maxSizeMb))
            .OrderByDescending(row => row.Entry.LastWriteUtc)
            .ThenBy(row => row.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _matchedRepositoryCount = repositoryMatches.Count;
        _matchedSnapshotCount = snapshotMatches.Count;
        _matchedFileCount = fileMatches.Count;
        _fileResultsLimited = fileMatches.Count > MaxVisibleFileResults;

        FileResults.Clear();
        foreach (var row in fileMatches.Take(MaxVisibleFileResults))
            FileResults.Add(MapEntry(row));

        NotifyResultStateChanged();
    }

    private void RebuildFilterOptions()
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

        TrackedFormatFilters.Clear();
        TrackedFormatFilters.Add("all");
        foreach (var trackedFormat in trackedFormats)
            TrackedFormatFilters.Add(trackedFormat);
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

        ExtensionFilters.Clear();
        ExtensionFilters.Add("all");
        foreach (var extension in extensions)
            ExtensionFilters.Add(extension);
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

        SnapshotTagFilters.Clear();
        SnapshotTagFilters.Add("all");
        foreach (var tag in snapshotTags)
            SnapshotTagFilters.Add(tag);
        SelectedSnapshotTagFilter = SnapshotTagFilters.Contains(selectedSnapshotTag, StringComparer.OrdinalIgnoreCase)
            ? selectedSnapshotTag
            : "all";
    }

    private void RebuildRepositoryFilterOptions()
    {
        var selectedRepositoryId = SelectedRepositoryFilter?.RepositoryId;
        RepositoryFilters.Clear();
        RepositoryFilters.Add(new GlobalSearchRepositoryFilterOptionViewModel(null, Loc.T("filter.option.all")));
        foreach (var repository in _repositorySource.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            RepositoryFilters.Add(new GlobalSearchRepositoryFilterOptionViewModel(repository.Id, repository.Name));

        SelectedRepositoryFilter = RepositoryFilters.FirstOrDefault(option => option.RepositoryId == selectedRepositoryId)
            ?? RepositoryFilters.FirstOrDefault();
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
        RebuildRepositoryFilterOptions();
        ApplyFilters();
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

    private GlobalSearchFileResultItemViewModel MapEntry(IndexedEntry row)
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

    private GlobalSearchSnapshotResultItemViewModel MapSnapshot(IndexedSnapshot row)
    {
        var title = row.Snapshot.Title ?? string.Empty;
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

    private static bool MatchesEntryText(IndexedEntry row, string textQuery)
    {
        if (string.IsNullOrWhiteSpace(textQuery))
            return true;

        return row.Entry.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Entry.RelativePath.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Repository.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || row.Repository.DirectoryPath.Contains(textQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSnapshotText(IndexedSnapshot row, string textQuery)
    {
        if (string.IsNullOrWhiteSpace(textQuery))
            return true;

        return (!string.IsNullOrWhiteSpace(row.Snapshot.Title)
                   && row.Snapshot.Title.Contains(textQuery, StringComparison.OrdinalIgnoreCase))
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

    private static bool MatchesModifiedWindow(RepositoryScanEntryDto entry, string modifiedWindowFilter)
    {
        var threshold = modifiedWindowFilter switch
        {
            "24h" => DateTime.UtcNow.AddHours(-24),
            "7d" => DateTime.UtcNow.AddDays(-7),
            "30d" => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.MinValue
        };

        return threshold == DateTime.MinValue || entry.LastWriteUtc >= threshold;
    }

    private static bool MatchesSnapshotWindow(RepositorySnapshotHistoryItemDto snapshot, string modifiedWindowFilter)
    {
        var threshold = modifiedWindowFilter switch
        {
            "24h" => DateTime.UtcNow.AddHours(-24),
            "7d" => DateTime.UtcNow.AddDays(-7),
            "30d" => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.MinValue
        };

        return threshold == DateTime.MinValue || snapshot.CreatedAtUtc >= threshold;
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
}
