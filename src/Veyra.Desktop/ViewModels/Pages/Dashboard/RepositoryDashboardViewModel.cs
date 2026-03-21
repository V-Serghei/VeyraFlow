using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Application.Queries;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Security;
using Veyra.Desktop.Services.State;
using Veyra.Desktop.Styling;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

public sealed partial class RepositoryDashboardViewModel : ObservableObject
{
    private const int FilterDebounceMs = 120;

    private readonly IMediator _mediator;
    private readonly ILogger<RepositoryDashboardViewModel> _log;
    private readonly IWindowService _windows;
    private readonly ISensitiveActionGuard _sensitiveActionGuard;
    private readonly IRepositoryDashboardFilterStore _filterStore;
    private readonly LocalizationManager _localization;
    private readonly UserExperienceManager _experience;
    private readonly List<RepositoryCardViewModel> _allRepositories = [];

    private bool _presetsLoaded;
    private bool _suppressFilterApply;
    private CancellationTokenSource? _filterCts;
    private long _filterRequestId;

    public event Func<int, Task>? OpenRepositoryRequested;
    public event Func<int, Task>? OpenRepositorySettingsRequested;

    public ObservableCollection<RepositoryCardViewModel> Repositories { get; } = new();
    public ObservableCollection<RepositoryDashboardSavedFilterViewModel> SavedFilters { get; } = new();

    public ObservableCollection<string> AvailabilityFilters { get; } = ["all", "available", "unavailable"];
    public ObservableCollection<string> SyncStateFilters { get; } = ["all", "synced", "queued", "syncing", "retrying", "conflict", "auth_required", "dead_letter", "failed"];
    public ObservableCollection<string> FormatTagFilters { get; } = ["all"];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isTransientActionBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _transientActionTitle = string.Empty;
    [ObservableProperty] private string _transientActionDetail = string.Empty;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedAvailabilityFilter = "all";
    [ObservableProperty] private string _selectedSyncStateFilter = "all";
    [ObservableProperty] private string _selectedFormatTagFilter = "all";
    [ObservableProperty] private string _minSizeMb = string.Empty;
    [ObservableProperty] private string _maxSizeMb = string.Empty;
    [ObservableProperty] private bool _onlyQueueIssues;
    [ObservableProperty] private bool _isAdvancedFiltersVisible;
    [ObservableProperty] private string _savedFilterName = string.Empty;
    [ObservableProperty] private RepositoryDashboardSavedFilterViewModel? _selectedSavedFilter;
    [ObservableProperty] private bool _hasSavedFilters;

    public bool HasActiveFilters => GetActiveFilterCount() > 0;
    public bool ShowLoadingOverlay => IsLoading || IsTransientActionBusy;
    public string LoadingOverlayTitle => IsTransientActionBusy
        ? TransientActionTitle
        : Loc.T("dashboard.loading_title");
    public string LoadingOverlayDetail => IsTransientActionBusy
        ? TransientActionDetail
        : Loc.T("dashboard.loading_detail");
    public string FilterButtonLabel => HasActiveFilters
        ? Loc.F("dashboard.filters_active_button", GetActiveFilterCount())
        : Loc.T("dashboard.filters_button");
    public string RepositoryResultsSummary
        => _allRepositories.Count == 0
            ? Loc.T("dashboard.results_none")
            : Repositories.Count == _allRepositories.Count
                ? Loc.F("dashboard.results_summary", Repositories.Count)
                : Loc.F("dashboard.results_filtered_summary", Repositories.Count, _allRepositories.Count);

    public RepositoryDashboardViewModel(
        IMediator mediator,
        ILogger<RepositoryDashboardViewModel> log,
        IWindowService windows,
        ISensitiveActionGuard sensitiveActionGuard,
        IRepositoryDashboardFilterStore filterStore)
    {
        _mediator = mediator;
        _log = log;
        _windows = windows;
        _sensitiveActionGuard = sensitiveActionGuard;
        _filterStore = filterStore;
        _localization = LocalizationManager.Instance;
        _experience = UserExperienceManager.Instance;
        _localization.LanguageChanged += OnLanguageChanged;
        _experience.ModeChanged += OnExperienceModeChanged;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            CancelActiveFilter();
            IsLoading = true;
            ErrorMessage = null;
            _log.LogInformation("Loading repository dashboard");
            await Task.Yield();

            var savedFiltersTask = !_presetsLoaded
                ? LoadSavedFiltersAsync()
                : Task.CompletedTask;
            var ensureRepositoriesTask = _mediator.Send(new EnsureRepositoriesCommand());

            await Task.WhenAll(savedFiltersTask, ensureRepositoriesTask);

            if (!_presetsLoaded)
                _presetsLoaded = true;

            var repos = await _mediator.Send(new GetAllRepositoriesQuery());
            _allRepositories.Clear();

            var cards = await Task.Run(() => repos
                .Select(MapRepositoryCard)
                .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());

            _allRepositories.AddRange(cards);

            RebuildFormatTagFilters();
            if (!HasActiveFilters)
            {
                ReplaceVisibleRepositories(cards);
                IsEmpty = Repositories.Count == 0;
            }

            IsLoading = false;

            if (HasActiveFilters)
                await ApplyFilterAsync(debounce: false);

            _log.LogInformation(
                "Repository dashboard loaded. TotalRepositories {Total}. VisibleRepositories {Visible}",
                _allRepositories.Count,
                Repositories.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load repositories");
            ErrorMessage = Loc.T("dashboard.error_load_failed");
            Repositories.Clear();
            IsEmpty = true;
            NotifyDashboardChromeStateChanged();
        }
        finally
        {
            if (IsLoading)
                IsLoading = false;
        }
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilterIfNeeded();
    partial void OnSelectedAvailabilityFilterChanged(string value) => ApplyFilterIfNeeded();
    partial void OnSelectedSyncStateFilterChanged(string value) => ApplyFilterIfNeeded();
    partial void OnSelectedFormatTagFilterChanged(string value) => ApplyFilterIfNeeded();
    partial void OnMinSizeMbChanged(string value) => ApplyFilterIfNeeded();
    partial void OnMaxSizeMbChanged(string value) => ApplyFilterIfNeeded();
    partial void OnOnlyQueueIssuesChanged(bool value) => ApplyFilterIfNeeded();

    [RelayCommand]
    private async Task OpenRepositoryAsync(RepositoryCardViewModel? repo)
    {
        if (repo is null)
            return;

        await RunTransientActionAsync(
            "dashboard.open_repository_title",
            "dashboard.open_repository_detail",
            async () =>
            {
                _log.LogInformation("Dashboard open repository requested. RepositoryId {RepositoryId}", repo.Id);
                if (OpenRepositoryRequested is not null)
                    await OpenRepositoryRequested.Invoke(repo.Id);
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open repository from dashboard. RepositoryId {RepositoryId}", repo.Id);
                ErrorMessage = Loc.T("dashboard.error_load_failed");
            });
    }

    [RelayCommand]
    private async Task OpenRepositorySettingsAsync(RepositoryCardViewModel? repo)
    {
        if (repo is null)
            return;

        await RunTransientActionAsync(
            "dashboard.open_repository_settings_title",
            "dashboard.open_repository_settings_detail",
            async () =>
            {
                _log.LogInformation("Dashboard open repository settings requested. RepositoryId {RepositoryId}", repo.Id);
                if (OpenRepositorySettingsRequested is not null)
                    await OpenRepositorySettingsRequested.Invoke(repo.Id);
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open repository settings from dashboard. RepositoryId {RepositoryId}", repo.Id);
                ErrorMessage = Loc.T("dashboard.error_load_failed");
            });
    }

    [RelayCommand]
    private async Task AddRepositoryAsync()
    {
        await RunTransientActionAsync(
            "dashboard.create_repository_opening_title",
            "dashboard.create_repository_opening_detail",
            async () =>
            {
                var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                    "security.action_add_repository",
                    "security.action_add_repository_body");

                if (!guardResult.IsAllowed)
                {
                    if (!guardResult.IsCancelled)
                        ErrorMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                    return;
                }

                var wizard = _windows.Create<CreateRepositoryWindow>();
                var owner = _windows.GetActiveWindow();
                _log.LogInformation("Opening create repository window from dashboard");

                if (owner is not null)
                    await _windows.ShowDialogAsync(wizard, owner);
                else
                    _windows.Show(wizard);

                await LoadAsync();
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open repository creation wizard");
                ErrorMessage = Loc.T("dashboard.error_open_create_repository");
            });
    }

    [RelayCommand]
    private void ToggleAdvancedFilters() => IsAdvancedFiltersVisible = !IsAdvancedFiltersVisible;

    [RelayCommand]
    private void ClearAdvancedFilters()
    {
        _suppressFilterApply = true;
        try
        {
            SearchQuery = string.Empty;
            SelectedAvailabilityFilter = "all";
            SelectedSyncStateFilter = "all";
            SelectedFormatTagFilter = "all";
            MinSizeMb = string.Empty;
            MaxSizeMb = string.Empty;
            OnlyQueueIssues = false;
        }
        finally
        {
            _suppressFilterApply = false;
        }

        ApplyFilterIfNeeded(debounce: false);
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
            SavedFilters.Insert(index, new RepositoryDashboardSavedFilterViewModel(preset));
        }
        else
        {
            SavedFilters.Add(new RepositoryDashboardSavedFilterViewModel(preset));
        }

        SortSavedFilters();
        await PersistSavedFiltersAsync();

        SelectedSavedFilter = SavedFilters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        HasSavedFilters = SavedFilters.Count > 0;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void ApplySavedFilter(RepositoryDashboardSavedFilterViewModel? filter)
    {
        var target = filter ?? SelectedSavedFilter;
        if (target is null)
            return;

        ApplyPreset(target.Preset);
    }

    [RelayCommand]
    private async Task DeleteSavedFilterAsync(RepositoryDashboardSavedFilterViewModel? filter)
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

    private void ApplyFilterIfNeeded()
        => ApplyFilterIfNeeded(true);

    private void ApplyFilterIfNeeded(bool debounce)
    {
        if (_suppressFilterApply)
            return;

        _ = ApplyFilterAsync(debounce);
    }

    private async Task ApplyFilterAsync(bool debounce = true)
    {
        var searchQuery = (SearchQuery ?? string.Empty).Trim();
        var directives = ParseSearchDirectives(searchQuery);

        var textQuery = directives.TextQuery;
        var availabilityFilter = NormalizeAvailabilityFilter(SelectedAvailabilityFilter);
        if (availabilityFilter == "all" && !string.IsNullOrWhiteSpace(directives.AvailabilityFilter))
            availabilityFilter = directives.AvailabilityFilter;

        var syncStateFilter = NormalizeSyncStateFilter(SelectedSyncStateFilter);
        if (syncStateFilter == "all" && !string.IsNullOrWhiteSpace(directives.SyncStateFilter))
            syncStateFilter = directives.SyncStateFilter;

        var formatTagFilter = NormalizeFormatFilter(SelectedFormatTagFilter);
        if (formatTagFilter == "all" && !string.IsNullOrWhiteSpace(directives.FormatTagFilter))
            formatTagFilter = directives.FormatTagFilter;

        var minSizeMb = ParseNullableDouble(MinSizeMb) ?? directives.MinSizeMb;
        var maxSizeMb = ParseNullableDouble(MaxSizeMb) ?? directives.MaxSizeMb;
        var onlyQueueIssues = OnlyQueueIssues;
        var source = _allRepositories.ToArray();
        var (requestId, ct) = BeginFilterRequest();

        try
        {
            if (debounce)
                await Task.Delay(FilterDebounceMs, ct);

            var filtered = await Task.Run(() =>
            {
                _log.LogDebug(
                    "Applying dashboard filters. Query {Query}. Availability {Availability}. SyncState {SyncState}. Format {Format}. QueueIssuesOnly {QueueIssuesOnly}",
                    searchQuery,
                    availabilityFilter,
                    syncStateFilter,
                    formatTagFilter,
                    onlyQueueIssues);

                if (minSizeMb is > 0 && maxSizeMb is > 0 && minSizeMb > maxSizeMb)
                    (minSizeMb, maxSizeMb) = (maxSizeMb, minSizeMb);

                IEnumerable<RepositoryCardViewModel> filteredSource = source;

                if (!string.IsNullOrWhiteSpace(textQuery))
                {
                    filteredSource = filteredSource.Where(r =>
                        r.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase) ||
                        r.DirectoryPath.Contains(textQuery, StringComparison.OrdinalIgnoreCase) ||
                        r.LinkedFormats.Any(f => f.Contains(textQuery, StringComparison.OrdinalIgnoreCase)));
                }

                if (availabilityFilter != "all")
                {
                    filteredSource = availabilityFilter switch
                    {
                        "available" => filteredSource.Where(r => r.IsDirectoryAvailable),
                        "unavailable" => filteredSource.Where(r => !r.IsDirectoryAvailable),
                        _ => filteredSource
                    };
                }

                if (syncStateFilter != "all")
                {
                    filteredSource = filteredSource.Where(r =>
                        string.Equals(r.CloudSyncStateKey, syncStateFilter, StringComparison.OrdinalIgnoreCase));
                }

                if (formatTagFilter != "all")
                {
                    filteredSource = filteredSource.Where(r => r.LinkedFormats.Any(f =>
                        string.Equals(f.Trim().ToLowerInvariant(), formatTagFilter, StringComparison.OrdinalIgnoreCase)));
                }

                if (minSizeMb is > 0)
                    filteredSource = filteredSource.Where(r => (r.TotalSizeBytes / (1024d * 1024d)) >= minSizeMb.Value);

                if (maxSizeMb is > 0)
                    filteredSource = filteredSource.Where(r => (r.TotalSizeBytes / (1024d * 1024d)) <= maxSizeMb.Value);

                if (onlyQueueIssues)
                {
                    filteredSource = filteredSource.Where(r =>
                        r.QueueConflictCount > 0 ||
                        r.QueueDeadLetterCount > 0 ||
                        r.QueueFailedCount > 0);
                }

                return filteredSource
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }, ct);

            if (!IsLatestFilterRequest(requestId) || ct.IsCancellationRequested)
                return;

            ReplaceVisibleRepositories(filtered);
            IsEmpty = Repositories.Count == 0;
            NotifyDashboardChromeStateChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to apply dashboard filters");
        }
    }

    private async Task LoadSavedFiltersAsync(CancellationToken ct = default)
    {
        var presets = await _filterStore.LoadAsync(ct);

        SavedFilters.Clear();
        foreach (var preset in presets)
            SavedFilters.Add(new RepositoryDashboardSavedFilterViewModel(preset));

        SortSavedFilters();
        HasSavedFilters = SavedFilters.Count > 0;
    }

    private async Task PersistSavedFiltersAsync(CancellationToken ct = default)
    {
        var presets = SavedFilters
            .Select(v => v.Preset)
            .ToList();

        await _filterStore.SaveAsync(presets, ct);
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

    private bool IsLatestFilterRequest(long requestId)
        => requestId == Interlocked.Read(ref _filterRequestId);

    private void CancelActiveFilter()
    {
        var cts = Interlocked.Exchange(ref _filterCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    private void ReplaceVisibleRepositories(IReadOnlyCollection<RepositoryCardViewModel> repositories)
    {
        if (AreVisibleRepositoriesEquivalent(repositories))
        {
            NotifyDashboardChromeStateChanged();
            return;
        }

        Repositories.Clear();
        foreach (var repository in repositories)
            Repositories.Add(repository);

        NotifyDashboardChromeStateChanged();
    }

    private bool AreVisibleRepositoriesEquivalent(IReadOnlyCollection<RepositoryCardViewModel> repositories)
    {
        if (Repositories.Count != repositories.Count)
            return false;

        using var currentEnumerator = Repositories.GetEnumerator();
        using var nextEnumerator = repositories.GetEnumerator();

        while (currentEnumerator.MoveNext() && nextEnumerator.MoveNext())
        {
            if (!ReferenceEquals(currentEnumerator.Current, nextEnumerator.Current))
                return false;
        }

        return true;
    }

    private void ApplyPreset(RepositoryDashboardFilterPreset preset)
    {
        _suppressFilterApply = true;
        try
        {
            SearchQuery = preset.SearchQuery;
            SelectedAvailabilityFilter = NormalizeAvailabilityFilter(preset.AvailabilityFilter);
            SelectedSyncStateFilter = NormalizeSyncStateFilter(preset.SyncStateFilter);
            SelectedFormatTagFilter = EnsureFormatFilterOption(NormalizeFormatFilter(preset.FormatTagFilter));
            MinSizeMb = preset.MinSizeMb;
            MaxSizeMb = preset.MaxSizeMb;
            OnlyQueueIssues = preset.OnlyQueueIssues;
            SavedFilterName = preset.Name;
        }
        finally
        {
            _suppressFilterApply = false;
        }

        ApplyFilterIfNeeded(debounce: false);
    }

    private RepositoryDashboardFilterPreset BuildCurrentPreset(string name)
    {
        return new RepositoryDashboardFilterPreset
        {
            Name = name,
            SearchQuery = SearchQuery,
            AvailabilityFilter = NormalizeAvailabilityFilter(SelectedAvailabilityFilter),
            SyncStateFilter = NormalizeSyncStateFilter(SelectedSyncStateFilter),
            FormatTagFilter = NormalizeFormatFilter(SelectedFormatTagFilter),
            MinSizeMb = MinSizeMb,
            MaxSizeMb = MaxSizeMb,
            OnlyQueueIssues = OnlyQueueIssues,
            SavedAtUtc = DateTime.UtcNow
        };
    }

    private void SortSavedFilters()
    {
        var sorted = SavedFilters
            .OrderByDescending(f => f.SavedAtUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SavedFilters.Clear();
        foreach (var item in sorted)
            SavedFilters.Add(item);
    }

    private void RebuildFormatTagFilters()
    {
        _suppressFilterApply = true;
        try
        {
            var selected = NormalizeFormatFilter(SelectedFormatTagFilter);
            var tags = _allRepositories
                .SelectMany(r => r.LinkedFormats)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ReplaceCollectionIfChanged(
                FormatTagFilters,
                ["all", .. tags]);

            SelectedFormatTagFilter = EnsureFormatFilterOption(selected);
        }
        finally
        {
            _suppressFilterApply = false;
        }
    }

    private string EnsureFormatFilterOption(string value)
    {
        if (!FormatTagFilters.Contains(value, StringComparer.OrdinalIgnoreCase))
            FormatTagFilters.Add(value);

        return value;
    }

    private static void ReplaceCollectionIfChanged<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> items)
    {
        if (CollectionEquals(collection, items))
            return;

        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }

    private static bool CollectionEquals<T>(IReadOnlyList<T> current, IReadOnlyList<T> next)
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

    private static RepositoryCardViewModel MapRepositoryCard(RepositoryDto repository)
    {
        var isAvailable = Directory.Exists(repository.DirectoryPath);
        var syncStateKey = NormalizeCloudSyncStateKey(repository.CloudSync?.LastStatus);
        var queuePending = repository.CloudSync?.PendingQueueCount ?? 0;
        var queueRunning = repository.CloudSync?.RunningQueueCount ?? 0;
        var queueRetry = repository.CloudSync?.RetryQueueCount ?? 0;
        var queueConflict = repository.CloudSync?.ConflictQueueCount ?? 0;
        var queueDeadLetter = repository.CloudSync?.DeadLetterQueueCount ?? 0;
        var queueFailed = repository.CloudSync?.FailedQueueCount ?? 0;
        var queueCompleted = repository.CloudSync?.CompletedQueueCount ?? 0;
        var statusBadge = BuildRepositoryStatusBadge(
            isAvailable,
            repository.CloudSync?.LastStatus,
            queuePending,
            queueRunning,
            queueRetry,
            queueConflict,
            queueDeadLetter,
            queueFailed);

        var card = new RepositoryCardViewModel
        {
            Id = repository.Id,
            Name = repository.Name,
            Description = repository.Description,
            DirectoryPath = repository.DirectoryPath,
            LastActivityUtc = repository.LastScannedAt,
            StatusText = statusBadge.Text,
            StatusColor = statusBadge.Color,
            LastActivity = FormatLastActivity(repository.LastScannedAt),
            FileCount = repository.FileCount,
            VersionCount = repository.VersionCount,
            TotalSizeBytes = repository.TotalSizeBytes,
            SizeDisplay = FormatSize(repository.TotalSizeBytes),
            CloudSyncStateKey = syncStateKey,
            CloudSyncStatus = FormatCloudSyncStatus(repository.CloudSync?.LastStatus),
            CloudQueueSummary = BuildQueueSummary(queuePending, queueRunning, queueRetry, queueConflict, queueDeadLetter),
            IsDirectoryAvailable = isAvailable,
            QueuePendingCount = queuePending,
            QueueRunningCount = queueRunning,
            QueueRetryCount = queueRetry,
            QueueConflictCount = queueConflict,
            QueueDeadLetterCount = queueDeadLetter,
            QueueFailedCount = queueFailed,
            QueueCompletedCount = queueCompleted
        };

        foreach (var format in repository.LinkedFormats)
            card.LinkedFormats.Add(format);

        card.RefreshFormatsDisplay();
        return card;
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

            if (lower.StartsWith("tag:", StringComparison.Ordinal))
            {
                var value = NormalizeFormatFilter(normalized[4..]);
                if (value != "all")
                    directives = directives with { FormatTagFilter = value };
                continue;
            }

            if (lower.StartsWith("status:", StringComparison.Ordinal))
            {
                var value = NormalizeSyncStateFilter(normalized[7..]);
                if (value != "all")
                    directives = directives with { SyncStateFilter = value };
                continue;
            }

            if (lower.StartsWith("availability:", StringComparison.Ordinal))
            {
                var value = NormalizeAvailabilityFilter(normalized[13..]);
                if (value != "all")
                    directives = directives with { AvailabilityFilter = value };
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

        if (sizeMb <= 0)
            return false;

        return true;
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

    private static string NormalizeAvailabilityFilter(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "available" or "unavailable" ? normalized : "all";
    }

    private static string NormalizeSyncStateFilter(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_');
        return normalized switch
        {
            "synced" => "synced",
            "queued" => "queued",
            "syncing" => "syncing",
            "retrying" => "retrying",
            "conflict" => "conflict",
            "auth_required" => "auth_required",
            "dead_letter" => "dead_letter",
            "failed" => "failed",
            _ => "all"
        };
    }

    private static string NormalizeFormatFilter(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "all")
            return "all";

        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }

    private static string NormalizeCloudSyncStateKey(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "idle";

        var normalized = status.Trim().ToLowerInvariant();
        if (normalized.StartsWith("synced", StringComparison.Ordinal))
            return "synced";

        if (normalized.StartsWith("syncing_upload", StringComparison.Ordinal))
            return "syncing";

        return normalized switch
        {
            "queued" => "queued",
            "syncing" => "syncing",
            "offline_retry" => "retrying",
            "retrying" => "retrying",
            "conflict" => "conflict",
            "auth_required" => "auth_required",
            "dead_letter" => "dead_letter",
            "failed" => "failed",
            _ => normalized.Replace(' ', '_')
        };
    }

    private static string FormatLastActivity(DateTime? utc)
    {
        if (utc is null)
            return Loc.T("dashboard.no_scan");

        var delta = DateTime.UtcNow - utc.Value;
        if (delta.TotalSeconds < 60) return Loc.T("dashboard.just_now");
        if (delta.TotalMinutes < 60) return Loc.F("dashboard.minutes_ago", (int)delta.TotalMinutes);
        if (delta.TotalHours < 24) return Loc.F("dashboard.hours_ago", (int)delta.TotalHours);
        return Loc.F("dashboard.days_ago", (int)delta.TotalDays);
    }

    private static string FormatCloudSyncStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return UserExperienceManager.Instance.IsBasicMode
                ? Loc.T("dashboard.sync.simple.local_only")
                : Loc.T("dashboard.sync.idle");

        var normalized = status.Trim().ToLowerInvariant();
        if (UserExperienceManager.Instance.IsBasicMode)
        {
            return normalized switch
            {
                "queued" or "syncing" or "offline_retry" or "retrying" => Loc.T("dashboard.sync.simple.in_progress"),
                "auth_required" or "conflict" or "dead_letter" or "failed" => Loc.T("dashboard.sync.simple.attention"),
                _ when normalized.StartsWith("synced", StringComparison.Ordinal) => Loc.T("dashboard.sync.simple.ready"),
                _ => Loc.T("dashboard.sync.simple.local_only")
            };
        }

        return normalized switch
        {
            "queued" => Loc.T("dashboard.sync.queued"),
            "syncing" => Loc.T("dashboard.sync.syncing"),
            _ when normalized.StartsWith("syncing_upload", StringComparison.Ordinal) => FormatSyncingUploadStatus(status),
            "offline_retry" => Loc.T("dashboard.sync.offline_retry"),
            "retrying" => Loc.T("dashboard.sync.retrying"),
            "auth_required" => Loc.T("dashboard.sync.auth_required"),
            "conflict" => Loc.T("dashboard.sync.conflict"),
            "dead_letter" => Loc.T("dashboard.sync.dead_letter"),
            "failed" => Loc.T("dashboard.sync.failed"),
            "skipped" => Loc.T("dashboard.sync.skipped"),
            _ when normalized.StartsWith("synced", StringComparison.Ordinal) => Loc.T("dashboard.sync.synced"),
            _ => status.Replace('_', ' ')
        };
    }

    private static string FormatSyncingUploadStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Loc.T("dashboard.sync.syncing");

        var parts = status.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var progress = parts[^1].Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (progress.Length == 2 &&
                int.TryParse(progress[0], out var current) &&
                int.TryParse(progress[1], out var total))
            {
                return Loc.F("dashboard.sync.syncing_upload", current, total);
            }
        }

        return Loc.T("dashboard.sync.syncing");
    }

    private static string BuildQueueSummary(int pending, int running, int retry, int conflict, int deadLetter)
    {
        if (UserExperienceManager.Instance.IsBasicMode)
        {
            var total = pending + running + retry + conflict + deadLetter;
            return total <= 0
                ? Loc.T("dashboard.queue_summary_simple_idle")
                : Loc.F("dashboard.queue_summary_simple_active", total);
        }

        return Loc.F("dashboard.queue_summary", pending, running, retry, conflict, deadLetter);
    }

    private static (string Text, string Color) BuildRepositoryStatusBadge(
        bool isDirectoryAvailable,
        string? cloudStatus,
        int pending,
        int running,
        int retry,
        int conflict,
        int deadLetter,
        int failed)
    {
        if (!isDirectoryAvailable)
            return (Loc.T("common.unavailable"), "#F44336");

        var normalized = NormalizeCloudSyncStateKey(cloudStatus);
        if (conflict > 0 || deadLetter > 0 || failed > 0 || normalized is "conflict" or "auth_required" or "dead_letter" or "failed")
            return (Loc.T("dashboard.sync.simple.attention"), "#F59E0B");

        if (pending > 0 || running > 0 || retry > 0 || normalized is "queued" or "syncing" or "retrying")
            return (Loc.T("dashboard.sync.simple.in_progress"), "#6EA8FF");

        if (normalized == "synced")
            return (Loc.T("dashboard.sync.simple.ready"), "#4CAF50");

        return (Loc.T("dashboard.sync.simple.local_only"), "#4CAF50");
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void OnExperienceModeChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void RefreshLocalizationState()
    {
        RefreshFilterOptionBindings();

        foreach (var card in _allRepositories)
        {
            var statusBadge = BuildRepositoryStatusBadge(
                card.IsDirectoryAvailable,
                card.CloudSyncStateKey,
                card.QueuePendingCount,
                card.QueueRunningCount,
                card.QueueRetryCount,
                card.QueueConflictCount,
                card.QueueDeadLetterCount,
                card.QueueFailedCount);
            card.StatusText = statusBadge.Text;
            card.StatusColor = statusBadge.Color;
            card.LastActivity = FormatLastActivity(card.LastActivityUtc);
            card.CloudSyncStatus = FormatCloudSyncStatus(card.CloudSyncStateKey);
            card.CloudQueueSummary = BuildQueueSummary(
                card.QueuePendingCount,
                card.QueueRunningCount,
                card.QueueRetryCount,
                card.QueueConflictCount,
                card.QueueDeadLetterCount);
            card.RefreshLocalization();
        }

        RefreshRepositoryBindings();
        NotifyDashboardChromeStateChanged();
    }

    private void RefreshFilterOptionBindings()
    {
        RebuildStaticFilterOptions(AvailabilityFilters, SelectedAvailabilityFilter, ["all", "available", "unavailable"], value => SelectedAvailabilityFilter = value);
        RebuildStaticFilterOptions(SyncStateFilters, SelectedSyncStateFilter, ["all", "synced", "queued", "syncing", "retrying", "conflict", "auth_required", "dead_letter", "failed"], value => SelectedSyncStateFilter = value);
    }

    private static void RebuildStaticFilterOptions(
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

    private void RefreshRepositoryBindings()
    {
        var selectedSavedFilterName = SelectedSavedFilter?.Name;
        var items = Repositories.ToList();
        Repositories.Clear();
        foreach (var item in items)
            Repositories.Add(item);

        SelectedSavedFilter = string.IsNullOrWhiteSpace(selectedSavedFilterName)
            ? SelectedSavedFilter
            : SavedFilters.FirstOrDefault(x => string.Equals(x.Name, selectedSavedFilterName, StringComparison.OrdinalIgnoreCase));

        IsEmpty = Repositories.Count == 0;
        NotifyDashboardChromeStateChanged();
    }

    private int GetActiveFilterCount()
    {
        var count = 0;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
            count++;
        if (!string.Equals(SelectedAvailabilityFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.Equals(SelectedSyncStateFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.Equals(SelectedFormatTagFilter, "all", StringComparison.OrdinalIgnoreCase))
            count++;
        if (!string.IsNullOrWhiteSpace(MinSizeMb))
            count++;
        if (!string.IsNullOrWhiteSpace(MaxSizeMb))
            count++;
        if (OnlyQueueIssues)
            count++;

        return count;
    }

    private void NotifyDashboardChromeStateChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(FilterButtonLabel));
        OnPropertyChanged(nameof(RepositoryResultsSummary));
        OnPropertyChanged(nameof(ShowLoadingOverlay));
        OnPropertyChanged(nameof(LoadingOverlayTitle));
        OnPropertyChanged(nameof(LoadingOverlayDetail));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLoadingOverlay));
        OnPropertyChanged(nameof(LoadingOverlayTitle));
        OnPropertyChanged(nameof(LoadingOverlayDetail));
    }

    partial void OnIsTransientActionBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLoadingOverlay));
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

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

}
