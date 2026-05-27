using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Monitoring;
using Veyra.Desktop.Services.Monitoring.Models;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Repositories;
using Veyra.Desktop.Services.Security;
using Veyra.Desktop.Services.Connectivity;
using Veyra.Desktop.Services.Connectivity.Models;
using Veyra.Desktop.Services.State;
using Veyra.Desktop.Styling;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

public sealed partial class RepositoryDashboardViewModel : ObservableObject
{
    private const int FilterDebounceMs = 120;

    private readonly IMediator _mediator;
    private readonly ILogger<RepositoryDashboardViewModel> _log;
    private readonly IWindowService _windows;
    private readonly ISensitiveActionGuard _sensitiveActionGuard;
    private readonly IUserProfileRepository _userProfiles;
    private readonly IConnectivityStatusService _connectivity;
    private readonly IMonitoringControlService _monitoringControl;
    private readonly IProcessResourceStatusStore _processResourceStatusStore;
    private readonly IRepositoryDashboardFilterStore _filterStore;
    private readonly IRepositoryLiveSyncStatusStore _liveSyncStatusStore;
    private readonly IRepositoryRelocationDetector _relocationDetector;
    private readonly LocalizationManager _localization;
    private readonly UserExperienceManager _experience;
    private readonly List<RepositoryCardViewModel> _allRepositories = [];
    private readonly Dictionary<int, RepositoryCardViewModel> _repositoryCardCache = [];
    private ProcessResourceSnapshotDto? _processResourceSnapshot;

    private bool _presetsLoaded;
    private bool _suppressFilterApply;
    private CancellationTokenSource? _filterCts;
    private long _filterRequestId;
    private const string LocalModeAccent = "#6EA8FF";
    private const string WarningAccent = "#F59E0B";
    private const string SuccessAccent = "#4CAF50";
    private const string LocalModeSurface = "#1A6EA8FF";
    private const string WarningSurface = "#1AF59E0B";
    private const string SuccessSurface = "#144CAF50";

    public event Func<int, Task>? OpenRepositoryRequested;
    public event Func<int, Task>? OpenRepositorySettingsRequested;

    public ObservableCollection<RepositoryCardViewModel> Repositories { get; } = new();
    public ObservableCollection<DashboardFolderTreeNodeViewModel> FolderTreeRoots { get; } = [];
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
    [ObservableProperty] private bool _isSearchVisible;
    [ObservableProperty] private bool _isCompactCards;
    [ObservableProperty] private bool _isListView;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderTreeModeButtonText))]
    private bool _isRepositoryTreeOnly;
    [ObservableProperty] private int _repositoryGridColumns = 3;
    [ObservableProperty] private string _savedFilterName = string.Empty;
    [ObservableProperty] private DashboardFolderTreeNodeViewModel? _selectedFolderNode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderFilterActive))]
    [NotifyPropertyChangedFor(nameof(EmptyTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    private string _selectedFolderPath = string.Empty;
    [ObservableProperty] private RepositoryDashboardSavedFilterViewModel? _selectedSavedFilter;
    [ObservableProperty] private bool _hasSavedFilters;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectivityPanel))]
    [NotifyPropertyChangedFor(nameof(ConnectivityPanelText))]
    [NotifyPropertyChangedFor(nameof(ConnectivityPanelBadgeText))]
    [NotifyPropertyChangedFor(nameof(ConnectivityPanelTitle))]
    [NotifyPropertyChangedFor(nameof(ConnectivityPanelDetail))]
    [NotifyPropertyChangedFor(nameof(ConnectivityPanelAccentColor))]
    [NotifyPropertyChangedFor(nameof(ConnectivityPanelBackgroundColor))]
    [NotifyPropertyChangedFor(nameof(ConnectivityPanelIcon))]
    private bool _hasCloudAccess;

    public bool HasActiveFilters => GetActiveFilterCount() > 0;
    public bool ShowConnectivityPanel => HasCloudAccess;
    public string ConnectivityPanelText => _connectivity.Snapshot.State switch
    {
        ConnectivityState.InternetUnavailable => Loc.T("dashboard.mode.internet_unavailable_compact"),
        ConnectivityState.CloudUnavailable => Loc.T("dashboard.mode.cloud_unavailable_compact"),
        _ => string.Empty
    };
    public string ConnectivityPanelBadgeText => _connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("dashboard.cloud_mode.internet_unavailable"),
            ConnectivityState.CloudUnavailable => Loc.T("dashboard.cloud_mode.cloud_unavailable"),
            _ => Loc.T("dashboard.cloud_mode.ready")
        };
    public string ConnectivityPanelTitle => _connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("dashboard.mode.internet_unavailable_title"),
            ConnectivityState.CloudUnavailable => Loc.T("dashboard.mode.cloud_unavailable_title"),
            _ => string.Empty
        };
    public string ConnectivityPanelDetail => _connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("dashboard.mode.internet_unavailable_detail"),
            ConnectivityState.CloudUnavailable => Loc.T("dashboard.mode.cloud_unavailable_detail"),
            _ => Loc.T("dashboard.mode.cloud_connected_detail")
        };
    public string ConnectivityPanelAccentColor => DescribeConnectivityVisuals(HasCloudAccess, _connectivity.Snapshot.State).AccentColor;
    public string ConnectivityPanelBackgroundColor => DescribeConnectivityVisuals(HasCloudAccess, _connectivity.Snapshot.State).BackgroundColor;
    public string ConnectivityPanelIcon => HasCloudAccess &&
        _connectivity.Snapshot.State is not (ConnectivityState.InternetUnavailable or ConnectivityState.CloudUnavailable)
        ? ""   // checkmark — cloud connected
        : "";  // warning — connectivity issue
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
    public bool IsCardView => !IsListView;
    public bool IsRegularCardView => !IsListView && !IsCompactCards;
    public bool IsCompactCardView => !IsListView && IsCompactCards;
    public string RepositoryViewModeLabel => IsListView
        ? Loc.T("dashboard.view_mode_list")
        : IsCompactCards
            ? Loc.T("dashboard.view_mode_compact_cards")
            : Loc.T("dashboard.view_mode_roomy_cards");
    public string RepositoryViewModeIcon => IsListView ? "\uE8FD" : "\uE8A9";
    public string FolderTreeModeButtonText => IsRepositoryTreeOnly
        ? Loc.T("dashboard.folder_tree_all_folders")
        : Loc.T("dashboard.folder_tree_repositories");
    public bool IsFolderFilterActive => !string.IsNullOrWhiteSpace(SelectedFolderPath);
    public string EmptyTitle => IsFolderFilterActive
        ? Loc.T("dashboard.no_repositories_in_folder")
        : Loc.T("dashboard.no_repositories");
    public string EmptyHint => IsFolderFilterActive
        ? Loc.F("dashboard.no_repositories_in_folder_hint", SelectedFolderPath)
        : Loc.T("dashboard.no_repositories_hint");
    public bool HasProcessLoadSnapshot => _processResourceSnapshot is not null;
    public bool HasProcessLoadHistory => _processResourceStatusStore.History.Count > 0;
    public string ProcessLoadSummaryText
    {
        get
        {
            if (_processResourceSnapshot is not { } snapshot)
                return string.Empty;

            return snapshot.CpuPercent.HasValue
                ? Loc.F(
                    "dashboard.process_load_summary",
                    snapshot.CpuPercent.Value.ToString("0.#", CultureInfo.CurrentCulture),
                    FormatSize(snapshot.WorkingSetBytes))
                : Loc.F("dashboard.process_load_summary_cpu_pending", FormatSize(snapshot.WorkingSetBytes));
        }
    }

    public string ProcessLoadDetailText
    {
        get
        {
            if (_processResourceSnapshot is not { } snapshot)
                return string.Empty;

            return Loc.F(
                "dashboard.process_load_detail",
                FormatSize(snapshot.PrivateMemoryBytes),
                FormatSize(snapshot.ManagedHeapBytes),
                snapshot.ThreadCount,
                snapshot.HandleCount,
                snapshot.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        }
    }

    public string ProcessLoadPeakText
        => ProcessResourceStatusPresenter.FormatPeakSummary(_processResourceStatusStore.History);

    public string ProcessLoadHistoryText
        => ProcessResourceStatusPresenter.FormatRecentHistory(_processResourceStatusStore.History);

    public string ProcessLoadAccentColor => DescribeProcessLoadVisuals(_processResourceSnapshot).AccentColor;
    public string ProcessLoadBackgroundColor => DescribeProcessLoadVisuals(_processResourceSnapshot).BackgroundColor;
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
        IUserProfileRepository userProfiles,
        IConnectivityStatusService connectivity,
        IMonitoringControlService monitoringControl,
        IProcessResourceStatusStore processResourceStatusStore,
        IRepositoryDashboardFilterStore filterStore,
        IRepositoryLiveSyncStatusStore liveSyncStatusStore,
        IRepositoryRelocationDetector relocationDetector)
    {
        _mediator = mediator;
        _log = log;
        _windows = windows;
        _sensitiveActionGuard = sensitiveActionGuard;
        _userProfiles = userProfiles;
        _connectivity = connectivity;
        _monitoringControl = monitoringControl;
        _processResourceStatusStore = processResourceStatusStore;
        _filterStore = filterStore;
        _liveSyncStatusStore = liveSyncStatusStore;
        _relocationDetector = relocationDetector;
        _localization = LocalizationManager.Instance;
        _experience = UserExperienceManager.Instance;
        _processResourceSnapshot = _monitoringControl.IsEnabled
            ? _processResourceStatusStore.Snapshot
            : null;
        _localization.LanguageChanged += OnLanguageChanged;
        _experience.ModeChanged += OnExperienceModeChanged;
        _connectivity.StatusChanged += OnConnectivityStatusChanged;
        _liveSyncStatusStore.StatusChanged += OnLiveSyncStatusChanged;
        _processResourceStatusStore.StatusChanged += OnProcessResourceStatusChanged;
        _monitoringControl.StateChanged += OnMonitoringStateChanged;
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

            HasCloudAccess = (await _userProfiles.GetActiveProfileAsync()) is not null;
            var repos = await _mediator.Send(new GetAllRepositoriesQuery());
            _allRepositories.Clear();

            var cards = await Task.Run(() =>
            {
                var mapped = repos
                    .Select(repo => MapRepositoryCardCached(repo, HasCloudAccess, _connectivity.Snapshot.State))
                    .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var activeRepositoryIds = mapped
                    .Select(card => card.Id)
                    .ToHashSet();
                var staleRepositoryIds = _repositoryCardCache.Keys
                    .Where(id => !activeRepositoryIds.Contains(id))
                    .ToList();

                foreach (var staleRepositoryId in staleRepositoryIds)
                    _repositoryCardCache.Remove(staleRepositoryId);

                return mapped;
            });

            _allRepositories.AddRange(cards);
            RebuildFolderTreeRoots();

            RebuildFormatTagFilters();
            var shouldApplyFilters = HasActiveFilters || IsFolderFilterActive;
            if (!shouldApplyFilters)
            {
                ReplaceVisibleRepositories(cards);
                IsEmpty = Repositories.Count == 0;
            }

            IsLoading = false;

            if (shouldApplyFilters)
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
    partial void OnIsCompactCardsChanged(bool value) => NotifyLayoutModeChanged();
    partial void OnIsListViewChanged(bool value) => NotifyLayoutModeChanged();
    partial void OnSelectedFolderNodeChanged(DashboardFolderTreeNodeViewModel? value)
    {
        if (value?.IsPlaceholder == true)
            return;

        MarkSelectedFolderNode(value);
        if (value is not null)
            EnsureFolderChildrenLoaded(value);

        SelectedFolderPath = value?.FullPath ?? string.Empty;
        ApplyFilterIfNeeded(debounce: false);
    }

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
    private async Task AutoRelinkRepositoryAsync(RepositoryCardViewModel? repo)
    {
        if (repo is null)
            return;

        await RunTransientActionAsync(
            "repo_relink.search_title",
            "repo_relink.search_detail",
            async () =>
            {
                ErrorMessage = null;

                var repository = await _mediator.Send(new GetRepositoryDetailQuery(repo.Id));
                if (repository is null)
                {
                    ErrorMessage = Loc.T("ui_error.repository_not_found");
                    return;
                }

                var latestEntries = await _mediator.Send(new GetRepositoryLatestEntriesQuery(repo.Id));
                var suggestion = await _relocationDetector.SuggestAsync(repository.DirectoryPath, latestEntries);

                if (!suggestion.CanAutoRelink || string.IsNullOrWhiteSpace(suggestion.SuggestedPath))
                {
                    ErrorMessage = suggestion.IsAmbiguous
                        ? Loc.T("repo_relink.error_auto_ambiguous")
                        : Loc.T("repo_relink.error_auto_not_found");
                    return;
                }

                var result = await _mediator.Send(RepositoryRelinkCommandFactory.Create(repository, suggestion.SuggestedPath));
                if (!result.Success)
                {
                    ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(
                        result.Error,
                        "repo_relink.error_apply_failed");
                    return;
                }

                await LoadAsync();
            },
            ex =>
            {
                _log.LogError(ex, "Failed to auto-relink repository from dashboard. RepositoryId {RepositoryId}", repo.Id);
                ErrorMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "repo_relink.error_apply_failed");
            });
    }

    [RelayCommand]
    private async Task AddRepositoryAsync()
    {
        await OpenCreateRepositoryWizardAsync(null);
    }

    [RelayCommand]
    private async Task CreateRepositoryFromFolderAsync(DashboardFolderTreeNodeViewModel? folder)
    {
        if (folder is null || !folder.CanCreateRepository)
            return;

        await OpenCreateRepositoryWizardAsync(folder.FullPath);
    }

    [RelayCommand]
    private async Task OpenRepositorySettingsFromFolderAsync(DashboardFolderTreeNodeViewModel? folder)
    {
        if (folder?.Repository is not { } repository)
            return;

        await OpenRepositorySettingsAsync(repository);
    }

    [RelayCommand]
    private async Task DeleteRepositoryFromFolderAsync(DashboardFolderTreeNodeViewModel? folder)
    {
        if (folder?.Repository is not { } repository)
            return;

        try
        {
            if (!await ConfirmRepositoryDeletionAsync(repository))
                return;

            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_delete_repository",
                "security.action_delete_repository_body",
                [repository.Name]);

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    ErrorMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            await RunTransientActionAsync(
                "dashboard.loading_title",
                "dashboard.loading_detail",
                async () =>
                {
                    await _mediator.Send(new DeleteRepositoryCommand(repository.Id));
                    await LoadAsync();
                },
                ex =>
                {
                    _log.LogError(ex, "Failed to delete repository from dashboard tree. RepositoryId {RepositoryId}", repository.Id);
                    ErrorMessage = Loc.T("repo_settings.error_delete_failed");
                });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete repository from dashboard tree. RepositoryId {RepositoryId}", repository.Id);
            ErrorMessage = Loc.T("repo_settings.error_delete_failed");
        }
    }

    [RelayCommand]
    private void ToggleRepositoryTreeMode()
    {
        IsRepositoryTreeOnly = !IsRepositoryTreeOnly;
        ClearFolderSelection();
        RebuildFolderTreeRoots();
    }

    public void ClearFolderSelection()
    {
        SelectedFolderNode = null;
        MarkSelectedFolderNode(null);
        SelectedFolderPath = string.Empty;
        ApplyFilterIfNeeded(debounce: false);
    }

    private async Task OpenCreateRepositoryWizardAsync(string? initialDirectoryPath)
    {
        try
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
            if (!string.IsNullOrWhiteSpace(initialDirectoryPath)
                && wizard.DataContext is Veyra.Desktop.ViewModels.Windows.CreateRepositoryWindowViewModel createRepositoryVm)
            {
                createRepositoryVm.ConfigureInitialDirectory(initialDirectoryPath, lockDirectory: true);
            }

            var owner = _windows.GetActiveWindow();
            _log.LogInformation("Opening create repository window from dashboard");

            if (owner is not null)
                await _windows.ShowDialogAsync(wizard, owner);
            else
                _windows.Show(wizard);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open repository creation wizard");
            ErrorMessage = Loc.T("dashboard.error_open_create_repository");
        }
    }

    private async Task<bool> ConfirmRepositoryDeletionAsync(RepositoryCardViewModel repository)
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return false;

        var window = _windows.Create<ConfirmActionWindow>();
        if (window.DataContext is ConfirmActionWindowViewModel vm)
        {
            vm.ConfigureLocalized(
                "repo_settings.delete_confirm_title",
                "repo_settings.delete_confirm_body",
                [repository.Name],
                "repo_settings.delete_confirm_warning",
                "repo_settings.delete_confirm_button");
        }

        await _windows.ShowDialogAsync(window, owner);
        return window.DataContext is ConfirmActionWindowViewModel resultVm && resultVm.IsConfirmed;
    }

    [RelayCommand]
    private void ToggleAdvancedFilters() => IsAdvancedFiltersVisible = !IsAdvancedFiltersVisible;

    [RelayCommand]
    private void ToggleSearch() => IsSearchVisible = !IsSearchVisible;

    [RelayCommand]
    private void ToggleCardDensity() => IsCompactCards = !IsCompactCards;

    [RelayCommand]
    private void ToggleViewMode() => IsListView = !IsListView;

    [RelayCommand]
    private void SelectRoomyCardsView()
    {
        IsListView = false;
        IsCompactCards = false;
    }

    [RelayCommand]
    private void SelectCompactCardsView()
    {
        IsListView = false;
        IsCompactCards = true;
    }

    [RelayCommand]
    private void SelectListView()
    {
        IsListView = true;
    }

    public void UpdateRepositoryGridColumns(double availableWidth)
    {
        if (availableWidth <= 0)
            return;

        var minimumCardWidth = IsCompactCards ? 280 : 360;
        var columns = Math.Clamp((int)Math.Floor(availableWidth / minimumCardWidth), 1, 3);
        if (RepositoryGridColumns != columns)
            RepositoryGridColumns = columns;
    }

    public void ExpandFolderNode(DashboardFolderTreeNodeViewModel? node)
    {
        if (node is null || node.IsPlaceholder)
            return;

        node.IsExpanded = true;
        EnsureFolderChildrenLoaded(node);
    }

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
        var selectedFolderPath = SelectedFolderPath;
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

                if (IsPassThroughDashboardFilter(
                        textQuery,
                        availabilityFilter,
                        syncStateFilter,
                        formatTagFilter,
                        minSizeMb,
                        maxSizeMb,
                        onlyQueueIssues,
                        selectedFolderPath))
                {
                    return new List<RepositoryCardViewModel>(source);
                }

                var filteredSource = new List<RepositoryCardViewModel>(source.Length);

                foreach (var repository in source)
                {
                    if (!MatchesRepositoryCardText(repository, textQuery))
                        continue;

                    if (!MatchesAvailability(repository, availabilityFilter))
                        continue;

                    if (!MatchesSyncState(repository, syncStateFilter))
                        continue;

                    if (!MatchesFormatTag(repository, formatTagFilter))
                        continue;

                    if (!MatchesRepositorySize(repository, minSizeMb, maxSizeMb))
                        continue;

                    if (!MatchesRepositoryFolder(repository, selectedFolderPath))
                        continue;

                    if (onlyQueueIssues
                        && repository.QueueConflictCount <= 0
                        && repository.QueueDeadLetterCount <= 0
                        && repository.QueueFailedCount <= 0)
                    {
                        continue;
                    }

                    filteredSource.Add(repository);
                }

                return filteredSource;
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
        var items = presets
            .Select(preset => new RepositoryDashboardSavedFilterViewModel(preset))
            .OrderByDescending(f => f.SavedAtUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SyncSavedFilters(items);
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

    private void ReplaceVisibleRepositories(IReadOnlyList<RepositoryCardViewModel> repositories)
    {
        SyncCollection(Repositories, repositories);
        NotifyDashboardChromeStateChanged();
    }

    private void RebuildFolderTreeRoots()
    {
        var trackedPaths = _allRepositories
            .Select(card => NormalizeDirectoryPath(card.DirectoryPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var roots = IsRepositoryTreeOnly
            ? BuildRepositoryOnlyTreeRoots(trackedPaths)
            : DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
                .Select(drive => CreateFolderNode(drive.Name, trackedPaths, isDrive: true))
                .Where(node => node is not null)
                .Cast<DashboardFolderTreeNodeViewModel>()
                .ToList();

        FolderTreeRoots.Clear();
        foreach (var root in roots)
        {
            if (!IsRepositoryTreeOnly)
                AddPlaceholderIfExpandable(root);

            FolderTreeRoots.Add(root);
        }

        MarkSelectedFolderNode(null);
    }

    private List<DashboardFolderTreeNodeViewModel> BuildRepositoryOnlyTreeRoots(IReadOnlyCollection<string> trackedPaths)
    {
        var nodes = trackedPaths
            .Select(path => CreateFolderNode(path, trackedPaths))
            .Where(node => node is not null)
            .Cast<DashboardFolderTreeNodeViewModel>()
            .ToDictionary(node => NormalizeDirectoryPath(node.FullPath), StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes.Values)
        {
            node.IsLoaded = true;
            node.IsExpanded = true;
        }

        var roots = new List<DashboardFolderTreeNodeViewModel>();
        foreach (var node in nodes.Values.OrderBy(node => node.FullPath.Length))
        {
            var parent = nodes.Values
                .Where(candidate => !ReferenceEquals(candidate, node)
                                    && IsSameOrChildPath(candidate.FullPath, node.FullPath))
                .OrderByDescending(candidate => candidate.FullPath.Length)
                .FirstOrDefault();

            if (parent is null)
                roots.Add(node);
            else
                parent.Children.Add(node);
        }

        SortRepositoryOnlyNodes(roots);
        return roots;
    }

    private static void SortRepositoryOnlyNodes(IList<DashboardFolderTreeNodeViewModel> nodes)
    {
        var sorted = nodes
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        nodes.Clear();
        foreach (var node in sorted)
        {
            SortRepositoryOnlyNodes(node.Children);
            nodes.Add(node);
        }
    }

    private DashboardFolderTreeNodeViewModel? CreateFolderNode(
        string path,
        IReadOnlyCollection<string> trackedPaths,
        bool isDrive = false)
    {
        var normalizedPath = NormalizeDirectoryPath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return null;

        var name = isDrive
            ? normalizedPath
            : Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(name))
            name = normalizedPath;

        var isTracked = trackedPaths.Any(trackedPath => PathsEqual(trackedPath, normalizedPath));
        var hasTrackedDescendant = trackedPaths.Any(trackedPath => IsSameOrChildPath(normalizedPath, trackedPath));
        var isInsideTrackedRepository = trackedPaths.Any(trackedPath =>
            !PathsEqual(trackedPath, normalizedPath)
            && IsSameOrChildPath(trackedPath, normalizedPath));
        var repository = _allRepositories.FirstOrDefault(card => PathsEqual(card.DirectoryPath, normalizedPath));
        return new DashboardFolderTreeNodeViewModel(
            name,
            normalizedPath,
            isDrive,
            isTracked,
            hasTrackedDescendant,
            isInsideTrackedRepository,
            repository);
    }

    private void EnsureFolderChildrenLoaded(DashboardFolderTreeNodeViewModel node)
    {
        if (node.IsLoaded || node.IsPlaceholder)
            return;

        var trackedPaths = _allRepositories
            .Select(card => NormalizeDirectoryPath(card.DirectoryPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var children = EnumerateSafeDirectories(node.FullPath)
            .Select(path => CreateFolderNode(path, trackedPaths))
            .Where(child => child is not null)
            .Cast<DashboardFolderTreeNodeViewModel>()
            .OrderByDescending(child => child.IsTracked)
            .ThenByDescending(child => child.IsInsideTrackedRepository)
            .ThenByDescending(child => child.HasTrackedDescendant)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Take(240)
            .ToList();

        node.Children.Clear();
        foreach (var child in children)
        {
            AddPlaceholderIfExpandable(child);
            node.Children.Add(child);
        }

        node.IsLoaded = true;
    }

    private void AddPlaceholderIfExpandable(DashboardFolderTreeNodeViewModel node)
    {
        if (node.IsPlaceholder || !Directory.Exists(node.FullPath))
            return;

        try
        {
            if (!Directory.EnumerateDirectories(node.FullPath).Any())
                return;
        }
        catch
        {
            return;
        }

        node.Children.Add(new DashboardFolderTreeNodeViewModel("...", string.Empty, false, false, false, false, isPlaceholder: true));
    }

    private static IEnumerable<string> EnumerateSafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void MarkSelectedFolderNode(DashboardFolderTreeNodeViewModel? selected)
    {
        foreach (var node in EnumerateFolderNodes(FolderTreeRoots))
            node.IsSelected = ReferenceEquals(node, selected);
    }

    private static IEnumerable<DashboardFolderTreeNodeViewModel> EnumerateFolderNodes(
        IEnumerable<DashboardFolderTreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in EnumerateFolderNodes(node.Children))
                yield return child;
        }
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

        SyncSavedFilters(sorted);
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

    private void SyncSavedFilters(IReadOnlyList<RepositoryDashboardSavedFilterViewModel> items)
    {
        SyncCollection(
            SavedFilters,
            items,
            static (left, right) =>
                string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
                && left.SavedAtUtc == right.SavedAtUtc);
    }

    private static void ReplaceCollectionIfChanged<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> items)
    {
        if (CollectionEquals(collection, items, EqualityComparer<T>.Default.Equals))
            return;

        SyncCollection(collection, items);
    }

    private static void SyncCollection<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> items)
        => SyncCollection(collection, items, EqualityComparer<T>.Default.Equals);

    private static void SyncCollection<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> items,
        Func<T, T, bool> comparer)
    {
        var prefixLength = 0;
        var currentCount = collection.Count;
        var nextCount = items.Count;

        while (prefixLength < currentCount &&
               prefixLength < nextCount &&
               comparer(collection[prefixLength], items[prefixLength]))
        {
            prefixLength++;
        }

        var suffixLength = 0;
        while (suffixLength < currentCount - prefixLength &&
               suffixLength < nextCount - prefixLength &&
               comparer(collection[currentCount - 1 - suffixLength], items[nextCount - 1 - suffixLength]))
        {
            suffixLength++;
        }

        for (var index = currentCount - suffixLength - 1; index >= prefixLength; index--)
            collection.RemoveAt(index);

        for (var index = prefixLength; index < nextCount - suffixLength; index++)
            collection.Insert(index, items[index]);
    }

    private static bool CollectionEquals<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> next,
        Func<T, T, bool> comparer)
    {
        if (current.Count != next.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!comparer(current[i], next[i]))
                return false;
        }

        return true;
    }

    private RepositoryCardViewModel MapRepositoryCardCached(
        RepositoryDto repository,
        bool hasCloudAccess,
        ConnectivityState connectivityState)
    {
        if (_repositoryCardCache.TryGetValue(repository.Id, out var cached))
        {
            UpdateRepositoryCard(cached, repository, hasCloudAccess, connectivityState);
            ApplyLiveSyncState(cached, _liveSyncStatusStore.Get(repository.Id));
            return cached;
        }

        var created = CreateRepositoryCard(repository, hasCloudAccess, connectivityState);
        ApplyLiveSyncState(created, _liveSyncStatusStore.Get(repository.Id));
        _repositoryCardCache[repository.Id] = created;
        return created;
    }

    private RepositoryCardViewModel CreateRepositoryCard(
        RepositoryDto repository,
        bool hasCloudAccess,
        ConnectivityState connectivityState)
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
            VersionCount = repository.ChangedVersionCount,
            TotalSizeBytes = repository.TotalSizeBytes,
            SizeDisplay = FormatSize(repository.TotalSizeBytes),
            ShowCloudSection = hasCloudAccess,
            ShowLiveSyncSection = _monitoringControl.IsEnabled,
            CloudSyncStateKey = syncStateKey,
            CloudSyncStatus = FormatCloudSyncStatus(repository.CloudSync?.LastStatus, hasCloudAccess, connectivityState),
            CloudModeText = FormatCloudMode(hasCloudAccess, connectivityState, syncStateKey),
            CloudModeColor = FormatCloudModeColor(hasCloudAccess, connectivityState),
            CloudModeBorderColor = FormatCloudModeBorderColor(hasCloudAccess, connectivityState),
            CloudModeBackgroundColor = FormatCloudModeBackgroundColor(hasCloudAccess, connectivityState),
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

    private void UpdateRepositoryCard(
        RepositoryCardViewModel card,
        RepositoryDto repository,
        bool hasCloudAccess,
        ConnectivityState connectivityState)
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

        card.Name = repository.Name;
        card.Description = repository.Description;
        card.DirectoryPath = repository.DirectoryPath;
        card.LastActivityUtc = repository.LastScannedAt;
        card.StatusText = statusBadge.Text;
        card.StatusColor = statusBadge.Color;
        card.LastActivity = FormatLastActivity(repository.LastScannedAt);
        card.FileCount = repository.FileCount;
        card.VersionCount = repository.ChangedVersionCount;
        card.TotalSizeBytes = repository.TotalSizeBytes;
        card.SizeDisplay = FormatSize(repository.TotalSizeBytes);
        card.ShowCloudSection = hasCloudAccess;
        card.ShowLiveSyncSection = _monitoringControl.IsEnabled;
        card.CloudSyncStateKey = syncStateKey;
        card.CloudSyncStatus = FormatCloudSyncStatus(repository.CloudSync?.LastStatus, hasCloudAccess, connectivityState);
        card.CloudModeText = FormatCloudMode(hasCloudAccess, connectivityState, syncStateKey);
        card.CloudModeColor = FormatCloudModeColor(hasCloudAccess, connectivityState);
        card.CloudModeBorderColor = FormatCloudModeBorderColor(hasCloudAccess, connectivityState);
        card.CloudModeBackgroundColor = FormatCloudModeBackgroundColor(hasCloudAccess, connectivityState);
        card.CloudQueueSummary = BuildQueueSummary(queuePending, queueRunning, queueRetry, queueConflict, queueDeadLetter);
        card.IsDirectoryAvailable = isAvailable;
        card.QueuePendingCount = queuePending;
        card.QueueRunningCount = queueRunning;
        card.QueueRetryCount = queueRetry;
        card.QueueConflictCount = queueConflict;
        card.QueueDeadLetterCount = queueDeadLetter;
        card.QueueFailedCount = queueFailed;
        card.QueueCompletedCount = queueCompleted;

        SyncCollection(card.LinkedFormats, repository.LinkedFormats.ToList());
        card.RefreshFormatsDisplay();
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

        if (normalized.StartsWith("syncing_", StringComparison.Ordinal))
            return "syncing";

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

    private static bool MatchesRepositoryCardText(RepositoryCardViewModel repository, string textQuery)
    {
        if (string.IsNullOrWhiteSpace(textQuery))
            return true;

        return repository.Name.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || repository.DirectoryPath.Contains(textQuery, StringComparison.OrdinalIgnoreCase)
               || repository.LinkedFormats.Any(f => f.Contains(textQuery, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesAvailability(RepositoryCardViewModel repository, string availabilityFilter)
    {
        return availabilityFilter switch
        {
            "available" => repository.IsDirectoryAvailable,
            "unavailable" => !repository.IsDirectoryAvailable,
            _ => true
        };
    }

    private static bool MatchesSyncState(RepositoryCardViewModel repository, string syncStateFilter)
        => syncStateFilter == "all"
           || string.Equals(repository.CloudSyncStateKey, syncStateFilter, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesFormatTag(RepositoryCardViewModel repository, string formatTagFilter)
    {
        if (formatTagFilter == "all")
            return true;

        return repository.LinkedFormats.Any(f =>
            string.Equals(f.Trim().ToLowerInvariant(), formatTagFilter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesRepositorySize(RepositoryCardViewModel repository, double? minSizeMb, double? maxSizeMb)
    {
        var sizeMb = repository.TotalSizeBytes / (1024d * 1024d);
        if (minSizeMb is > 0 && sizeMb < minSizeMb.Value)
            return false;
        if (maxSizeMb is > 0 && sizeMb > maxSizeMb.Value)
            return false;

        return true;
    }

    private static bool MatchesRepositoryFolder(RepositoryCardViewModel repository, string? selectedFolderPath)
    {
        if (string.IsNullOrWhiteSpace(selectedFolderPath))
            return true;

        return IsSameOrChildPath(selectedFolderPath, repository.DirectoryPath);
    }

    private static bool IsPassThroughDashboardFilter(
        string textQuery,
        string availabilityFilter,
        string syncStateFilter,
        string formatTagFilter,
        double? minSizeMb,
        double? maxSizeMb,
        bool onlyQueueIssues,
        string? selectedFolderPath)
    {
        return string.IsNullOrWhiteSpace(textQuery)
               && availabilityFilter == "all"
               && syncStateFilter == "all"
               && formatTagFilter == "all"
               && minSizeMb is not > 0
               && maxSizeMb is not > 0
               && !onlyQueueIssues
               && string.IsNullOrWhiteSpace(selectedFolderPath);
    }

    private static bool PathsEqual(string? left, string? right)
        => string.Equals(NormalizeDirectoryPath(left), NormalizeDirectoryPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChildPath(string? parentPath, string? childPath)
    {
        var parent = NormalizeDirectoryPath(parentPath);
        var child = NormalizeDirectoryPath(childPath);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child))
            return false;

        if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
            return true;

        var parentWithSeparator = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                return fullPath;

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
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
                _ when normalized.StartsWith("syncing_upload", StringComparison.Ordinal) => Loc.T("dashboard.sync.simple.in_progress"),
                "queued" or "syncing" or "syncing_prepare" or "syncing_snapshot" or "syncing_finalize" or "offline_retry" or "retrying"
                    => Loc.T("dashboard.sync.simple.in_progress"),
                "auth_required" or "conflict" or "dead_letter" or "failed" or "paused" or "cancelled" => Loc.T("dashboard.sync.simple.attention"),
                "linked" => Loc.T("dashboard.sync.simple.ready"),
                _ when normalized.StartsWith("synced", StringComparison.Ordinal) => Loc.T("dashboard.sync.simple.ready"),
                _ => Loc.T("dashboard.sync.simple.local_only")
            };
        }

        return normalized switch
        {
            "queued" => Loc.T("dashboard.sync.queued"),
            "syncing" or "syncing_prepare" => Loc.T("dashboard.sync.syncing_prepare"),
            "syncing_snapshot" => Loc.T("dashboard.sync.syncing_snapshot"),
            "syncing_finalize" => Loc.T("dashboard.sync.syncing_finalize"),
            _ when normalized.StartsWith("syncing_upload", StringComparison.Ordinal) => FormatSyncingUploadStatus(status),
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
        var total = pending + running + retry + conflict + deadLetter;
        if (total <= 0)
            return Loc.T("dashboard.queue_summary_simple_idle");

        if (conflict > 0 || deadLetter > 0)
            return Loc.T("dashboard.sync.simple.attention");

        return Loc.T("dashboard.queue_summary_simple_active");
    }

    private static string FormatCloudMode(bool hasCloudAccess, ConnectivityState connectivityState, string syncStateKey)
    {
        if (!hasCloudAccess)
            return Loc.T("dashboard.cloud_mode.local");

        return connectivityState switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("dashboard.cloud_mode.internet_unavailable"),
            ConnectivityState.CloudUnavailable => Loc.T("dashboard.cloud_mode.cloud_unavailable"),
            _ when syncStateKey == "idle" => Loc.T("dashboard.cloud_mode.ready"),
            _ => Loc.T("dashboard.cloud_mode.ready")
        };
    }

    private static string FormatCloudSyncStatus(string? cloudStatus, bool hasCloudAccess, ConnectivityState connectivityState)
    {
        if (hasCloudAccess)
        {
            if (connectivityState == ConnectivityState.InternetUnavailable)
                return Loc.T("dashboard.sync.internet_unavailable_status");

            if (connectivityState == ConnectivityState.CloudUnavailable)
                return Loc.T("dashboard.sync.cloud_unavailable_status");
        }

        return FormatCloudSyncStatus(cloudStatus);
    }

    private static string FormatCloudModeColor(bool hasCloudAccess, ConnectivityState connectivityState)
        => !hasCloudAccess
            ? LocalModeAccent
            : connectivityState switch
            {
                ConnectivityState.InternetUnavailable => WarningAccent,
                ConnectivityState.CloudUnavailable => WarningAccent,
                _ => SuccessAccent
            };

    private static string FormatCloudModeBorderColor(bool hasCloudAccess, ConnectivityState connectivityState)
        => FormatCloudModeColor(hasCloudAccess, connectivityState);

    private static string FormatCloudModeBackgroundColor(bool hasCloudAccess, ConnectivityState connectivityState)
        => !hasCloudAccess
            ? LocalModeSurface
            : connectivityState switch
            {
                ConnectivityState.InternetUnavailable => WarningSurface,
                ConnectivityState.CloudUnavailable => WarningSurface,
                _ => SuccessSurface
            };

    private static (string AccentColor, string BackgroundColor) DescribeConnectivityVisuals(bool hasCloudAccess, ConnectivityState state)
        => !hasCloudAccess
            ? (LocalModeAccent, LocalModeSurface)
            : state switch
            {
                ConnectivityState.InternetUnavailable => (WarningAccent, WarningSurface),
                ConnectivityState.CloudUnavailable => (WarningAccent, WarningSurface),
                _ => (SuccessAccent, SuccessSurface)
            };

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

    private static void ApplyLiveSyncState(
        RepositoryCardViewModel card,
        RepositoryLiveSyncStatusSnapshot? status)
    {
        var visuals = RepositoryLiveSyncStatusPresenter.DescribeVisualState(status);
        card.LiveSyncStateText = RepositoryLiveSyncStatusPresenter.FormatStateText(status);
        card.LiveSyncModeText = RepositoryLiveSyncStatusPresenter.FormatModeText(status);
        card.LiveSyncSummaryText = RepositoryLiveSyncStatusPresenter.FormatSummaryText(status);
        card.LiveSyncDetailText = RepositoryLiveSyncStatusPresenter.FormatDetailText(status);
        card.LiveSyncAccentColor = visuals.AccentColor;
        card.LiveSyncBorderColor = visuals.AccentColor;
        card.LiveSyncBackgroundColor = visuals.BackgroundColor;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void OnExperienceModeChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void OnConnectivityStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshLocalizationState);
    }

    private void OnLiveSyncStatusChanged(object? sender, RepositoryLiveSyncStatusChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_repositoryCardCache.TryGetValue(e.RepositoryId, out var card))
                ApplyLiveSyncState(card, e.Snapshot);
        });
    }

    private void OnMonitoringStateChanged(bool enabled)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _processResourceSnapshot = enabled ? _processResourceStatusStore.Snapshot : null;

            foreach (var card in _repositoryCardCache.Values)
                card.ShowLiveSyncSection = enabled;

            NotifyProcessLoadStateChanged();
        });
    }

    private void OnProcessResourceStatusChanged(object? sender, ProcessResourceStatusChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _processResourceSnapshot = _monitoringControl.IsEnabled ? e.Snapshot : null;
            NotifyProcessLoadStateChanged();
        });
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
            card.ShowCloudSection = HasCloudAccess;
            card.ShowLiveSyncSection = _monitoringControl.IsEnabled;
            card.CloudSyncStatus = FormatCloudSyncStatus(card.CloudSyncStateKey, HasCloudAccess, _connectivity.Snapshot.State);
            card.CloudModeText = FormatCloudMode(HasCloudAccess, _connectivity.Snapshot.State, card.CloudSyncStateKey);
            card.CloudModeColor = FormatCloudModeColor(HasCloudAccess, _connectivity.Snapshot.State);
            card.CloudModeBorderColor = FormatCloudModeBorderColor(HasCloudAccess, _connectivity.Snapshot.State);
            card.CloudModeBackgroundColor = FormatCloudModeBackgroundColor(HasCloudAccess, _connectivity.Snapshot.State);
            card.CloudQueueSummary = BuildQueueSummary(
                card.QueuePendingCount,
                card.QueueRunningCount,
                card.QueueRetryCount,
                card.QueueConflictCount,
                card.QueueDeadLetterCount);
            ApplyLiveSyncState(card, _liveSyncStatusStore.Get(card.Id));
            card.RefreshLocalization();
        }

        RefreshRepositoryBindings();
        NotifyDashboardChromeStateChanged();
        OnPropertyChanged(nameof(ShowConnectivityPanel));
        OnPropertyChanged(nameof(ConnectivityPanelText));
        OnPropertyChanged(nameof(ConnectivityPanelBadgeText));
        OnPropertyChanged(nameof(ConnectivityPanelTitle));
        OnPropertyChanged(nameof(ConnectivityPanelDetail));
        OnPropertyChanged(nameof(ConnectivityPanelAccentColor));
        OnPropertyChanged(nameof(ConnectivityPanelBackgroundColor));
        OnPropertyChanged(nameof(ConnectivityPanelIcon));
        NotifyProcessLoadStateChanged();
    }

    private void NotifyProcessLoadStateChanged()
    {
        OnPropertyChanged(nameof(HasProcessLoadSnapshot));
        OnPropertyChanged(nameof(HasProcessLoadHistory));
        OnPropertyChanged(nameof(ProcessLoadSummaryText));
        OnPropertyChanged(nameof(ProcessLoadDetailText));
        OnPropertyChanged(nameof(ProcessLoadPeakText));
        OnPropertyChanged(nameof(ProcessLoadHistoryText));
        OnPropertyChanged(nameof(ProcessLoadAccentColor));
        OnPropertyChanged(nameof(ProcessLoadBackgroundColor));
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

        ReplaceCollectionIfChanged(collection, values);

        restoreSelection(values.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? selected
            : values[0]);
    }

    private void RefreshRepositoryBindings()
    {
        var selectedSavedFilterName = SelectedSavedFilter?.Name;

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
        OnPropertyChanged(nameof(FolderTreeModeButtonText));
        OnPropertyChanged(nameof(RepositoryViewModeLabel));
        OnPropertyChanged(nameof(RepositoryResultsSummary));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(ShowLoadingOverlay));
        OnPropertyChanged(nameof(LoadingOverlayTitle));
        OnPropertyChanged(nameof(LoadingOverlayDetail));
    }

    private void NotifyLayoutModeChanged()
    {
        OnPropertyChanged(nameof(IsCardView));
        OnPropertyChanged(nameof(IsRegularCardView));
        OnPropertyChanged(nameof(IsCompactCardView));
        OnPropertyChanged(nameof(RepositoryViewModeLabel));
        OnPropertyChanged(nameof(RepositoryViewModeIcon));
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

    private static (string AccentColor, string BackgroundColor) DescribeProcessLoadVisuals(ProcessResourceSnapshotDto? snapshot)
    {
        if (snapshot is null)
            return ("#6EA8FF", "#1A6EA8FF");

        var cpu = snapshot.CpuPercent ?? 0;
        if (cpu >= 75 || snapshot.WorkingSetBytes >= 1024L * 1024 * 1024)
            return ("#F97316", "#1AF97316");

        if (cpu >= 40 || snapshot.WorkingSetBytes >= 700L * 1024 * 1024)
            return ("#F59E0B", "#1AF59E0B");

        return ("#4ADE80", "#164ADE80");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

}
