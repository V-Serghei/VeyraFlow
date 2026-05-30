using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.OperationJournal;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Connectivity;
using Veyra.Desktop.Services.Connectivity.Models;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Onboarding;
using Veyra.Desktop.Styling;
using Veyra.Desktop.ViewModels.Pages.Dashboard;
using Veyra.Desktop.ViewModels.Pages.Explorer;
using Veyra.Desktop.ViewModels.Pages.RepositorySettings;
using Veyra.Desktop.ViewModels.Pages.Search;
using Veyra.Desktop.ViewModels.Pages.Settings;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ThemeManager _theme = ThemeManager.Instance;
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    private readonly IConnectivityStatusService _connectivity;
    private readonly IOperationJournalService _journal;
    private readonly IWindowService _windows;
    private readonly ILogger<MainWindowViewModel> _log;
    private readonly OnboardingStateService _onboardingState;
    private bool _returnToAppSettingsFromRepositorySettings;
    private readonly List<GuidedTourStep> _guidedTourSteps = [];
    private int _guidedTourIndex = -1;
    private bool _isGuidedTourStarting;
    private bool _isLoaded;
    private object? _pageBeforeSearch;

    public RepositoryDashboardViewModel Dashboard { get; }
    public RepositoryExplorerViewModel Explorer { get; }
    public RepositorySettingsViewModel Settings { get; }
    public GlobalSearchViewModel Search { get; }
    public AppSettingsViewModel AppSettings { get; }

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _themeToggleGlyph = "\uE793";
    [ObservableProperty] private string _languageToggleLabel = "EN";
    [ObservableProperty] private bool _isGuidedTourVisible;
    [ObservableProperty] private string _guidedTourTitle = string.Empty;
    [ObservableProperty] private string _guidedTourDescription = string.Empty;
    [ObservableProperty] private string _guidedTourStepText = string.Empty;
    [ObservableProperty] private string _guidedTourTargetName = string.Empty;
    [ObservableProperty] private bool _isShellBusy;
    [ObservableProperty] private string _shellBusyTitle = string.Empty;
    [ObservableProperty] private string _shellBusyDetail = string.Empty;
    [ObservableProperty] private bool _showConnectivityBanner;
    [ObservableProperty] private string _connectivityBannerText = string.Empty;
    [ObservableProperty] private bool _showShellActivityStrip;
    [ObservableProperty] private string _shellActivityText = string.Empty;
    [ObservableProperty] private string _shellActivityDetailText = string.Empty;
    [ObservableProperty] private string _shellActivityAccentColor = "#6EA8FF";
    [ObservableProperty] private string _shellActivityBackgroundColor = "#1A6EA8FF";
    [ObservableProperty] private string _shellActivityBadgeText = string.Empty;
    [ObservableProperty] private bool _showShellActivityBadge;
    private bool _isShellActivityDismissed;
    private string _shellActivityStateKey = string.Empty;
    private readonly HashSet<string> _dismissedShellActivityKeys = [];

    public bool CanGuidedTourGoBack => _guidedTourIndex > 0;
    public bool ShowShellSyncCenterAction => HasCloudAccessInShell;
    public bool ShowShellBackButton => !ReferenceEquals(CurrentPage, Dashboard);
    public bool ShowShellBrandBadge => !ShowShellBackButton;
    public bool HasShellSubtitle => !string.IsNullOrWhiteSpace(ShellSubtitle);
    public string ShellTitle => CurrentPage switch
    {
        var page when ReferenceEquals(page, Dashboard) => Loc.T("main.product_name"),
        var page when ReferenceEquals(page, Explorer) => Loc.T("main.repository_explorer_title"),
        var page when ReferenceEquals(page, Settings) => Loc.T("repo_settings.title"),
        var page when ReferenceEquals(page, Search) => Loc.T("search.title"),
        var page when ReferenceEquals(page, AppSettings) => Loc.T("app_settings.title"),
        _ => Loc.T("main.product_name")
    };
    public string ShellSubtitle => CurrentPage switch
    {
        var page when ReferenceEquals(page, Dashboard) => Loc.T("main.product_subtitle"),
        var page when ReferenceEquals(page, Explorer) => Explorer.RepositoryName,
        var page when ReferenceEquals(page, Settings) => Settings.RepositoryName,
        var page when ReferenceEquals(page, Search) => Loc.T("search.subtitle"),
        _ => string.Empty
    };
    public string ShellSubtitleTooltip => CurrentPage switch
    {
        var page when ReferenceEquals(page, Explorer) => Explorer.RepositoryPath,
        var page when ReferenceEquals(page, Settings) => Settings.DirectoryPath,
        _ => ShellSubtitle
    };
    public string ConnectivityBannerAccentColor => _connectivity.Snapshot.State switch
    {
        ConnectivityState.InternetUnavailable => "#F59E0B",
        ConnectivityState.CloudUnavailable => "#F59E0B",
        _ => "#6EA8FF"
    };
    public string ConnectivityBannerBackgroundColor => _connectivity.Snapshot.State switch
    {
        ConnectivityState.InternetUnavailable => "#1AF59E0B",
        ConnectivityState.CloudUnavailable => "#1AF59E0B",
        _ => "#1A6EA8FF"
    };
    public string GuidedTourNextLabel => _guidedTourIndex >= _guidedTourSteps.Count - 1
        ? Loc.T("tour.finish")
        : Loc.T("tour.next");

    public MainWindowViewModel(
        RepositoryDashboardViewModel dashboard,
        RepositoryExplorerViewModel explorer,
        RepositorySettingsViewModel settings,
        GlobalSearchViewModel search,
        AppSettingsViewModel appSettings,
        IConnectivityStatusService connectivity,
        IOperationJournalService journal,
        IWindowService windows,
        OnboardingStateService onboardingState,
        ILogger<MainWindowViewModel> log)
    {
        Dashboard = dashboard;
        Explorer = explorer;
        Settings = settings;
        Search = search;
        AppSettings = appSettings;
        _connectivity = connectivity;
        _journal = journal;
        _windows = windows;
        _onboardingState = onboardingState;
        _log = log;

        Dashboard.OpenRepositoryRequested += OpenRepositoryAsync;
        Dashboard.OpenRepositorySettingsRequested += OpenRepositorySettingsAsync;

        Explorer.BackRequested += ShowDashboard;
        Explorer.OpenSettingsRequested += OpenRepositorySettingsAsync;

        Settings.BackRequested += ShowDashboard;
        Settings.RepositoryUpdated += OnRepositoryUpdatedAsync;
        Settings.RepositoryDeleted += OnRepositoryDeleted;

        Search.BackRequested += ReturnFromSearch;
        Search.OpenRepositoryRequested += OpenRepositoryAsync;
        Search.OpenRepositorySettingsRequested += OpenRepositorySettingsAsync;
        Search.OpenEntryRequested += OpenRepositoryEntryFromSearchAsync;
        Search.OpenSnapshotRequested += OpenRepositorySnapshotFromSearchAsync;
        Search.SnapshotTagSelectedRequested += OpenGlobalSearchForTagAsync;

        AppSettings.BackRequested += ShowDashboard;
        AppSettings.OpenRepositorySettingsRequested += OpenRepositorySettingsFromAppSettingsAsync;
        AppSettings.ExperienceModeRefreshRequested += OnExperienceModeRefreshRequestedAsync;
        Dashboard.PropertyChanged += OnChildCloudAccessChanged;
        Explorer.PropertyChanged += OnChildShellStateChanged;
        Settings.PropertyChanged += OnChildShellStateChanged;
        AppSettings.PropertyChanged += OnChildCloudAccessChanged;
        _theme.ThemeChanged += OnThemeChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _connectivity.StatusChanged += OnConnectivityStatusChanged;
        _onboardingState.FirstRunTourRequested += OnFirstRunTourRequested;

        CurrentPage = Dashboard;
        RefreshThemeState();
        RefreshLanguageState();
        RefreshConnectivityState();
    }

    partial void OnCurrentPageChanged(object? value)
    {
        RefreshConnectivityState();
        NotifyShellHeaderStateChanged();
    }

    [RelayCommand]
    private void ShellBack()
    {
        if (ReferenceEquals(CurrentPage, Explorer)
            || ReferenceEquals(CurrentPage, Settings)
            || ReferenceEquals(CurrentPage, AppSettings))
        {
            ShowDashboard();
            return;
        }

        if (ReferenceEquals(CurrentPage, Search))
            ReturnFromSearch();
    }

    [RelayCommand]
    private async Task OpenGlobalSettingsAsync()
    {
        _log.LogInformation("Opening global settings page");
        await RunShellBusyActionAsync(
            "app_settings.loading_title",
            "app_settings.loading_detail",
            async () =>
            {
                CurrentPage = AppSettings;
                await WaitForUiFrameAsync();
                await AppSettings.LoadAsync();
            });
    }

    [RelayCommand]
    private async Task OpenGlobalSearchAsync()
    {
        _pageBeforeSearch = CurrentPage;
        _log.LogInformation("Opening global search page");
        CurrentPage = Search;
        await WaitForUiFrameAsync();
        await Search.LoadAsync(forceRefresh: !Search.HasLoadedData);
    }

    [RelayCommand]
    private async Task OpenHelpCenterAsync()
    {
        _log.LogInformation("Opening help center");
        var owner = _windows.GetActiveWindow();
        var help = _windows.Create<InfoWindow>();

        if (help.DataContext is InfoWindowViewModel viewModel)
            viewModel.DeepLinkRequested += OpenHelpDeepLinkAsync;

        if (owner is null)
            _windows.Show(help);
        else
            await _windows.ShowDialogAsync(help, owner);
    }

    [RelayCommand]
    private async Task OpenCloudInformationAsync()
    {
        _log.LogInformation("Opening cloud information window");
        var owner = _windows.GetActiveWindow();
        var window = _windows.Create<CloudInformationWindow>();

        if (owner is null)
            _windows.Show(window);
        else
            await _windows.ShowDialogAsync(window, owner);
    }

    public async Task OpenHelpDeepLinkAsync(string link)
    {
        var normalized = link.Trim().ToLowerInvariant();
        if (normalized.StartsWith("settings.", StringComparison.Ordinal))
        {
            await OpenGlobalSettingsAsync();
            AppSettings.SelectTabByKey(normalized switch
            {
                "settings.cloud.encryption" => "sync",
                _ => "automation"
            });
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        _theme.ToggleDarkLight();
        RefreshThemeState();
        _log.LogInformation("Theme toggled. DarkTheme {IsDarkTheme}", _theme.IsDarkTheme);
    }

    [RelayCommand]
    private async Task ToggleLanguageAsync()
    {
        var languages = _localization.AvailableLanguages;
        if (languages.Count == 0)
            return;

        var currentIndex = languages
            .Select((item, index) => new { item, index })
            .FirstOrDefault(x => string.Equals(
                x.item.Code,
                _localization.CurrentLanguageCode,
                StringComparison.OrdinalIgnoreCase))
            ?.index ?? -1;

        var nextIndex = currentIndex < 0
            ? 0
            : (currentIndex + 1) % languages.Count;

        await WaitForUiFrameAsync();
        _localization.SetLanguage(languages[nextIndex].Code);
        RefreshLanguageState();
        _log.LogInformation("Language toggled. CurrentLanguage {Language}", _localization.CurrentLanguageCode);
    }

    public async void OnLoaded()
    {
        _log.LogInformation("Main window loaded. Loading dashboard");
        await RunShellBusyActionAsync(
            "dashboard.loading_title",
            "dashboard.loading_detail",
            async () =>
            {
                await Dashboard.LoadAsync();
                CurrentPage = Dashboard;
            });
        await RefreshShellActivityAsync();
        _isLoaded = true;
        _ = RefreshConnectivityAsync();
        await TryStartPendingGuidedTourAsync();
    }

    public Task ShowDashboardPageAsync()
        => ShowDashboardAsync();

    public Task ShowSearchPageAsync()
        => OpenGlobalSearchAsync();

    public Task ShowAppSettingsPageAsync()
        => OpenGlobalSettingsAsync();

    public Task ShowCloudSyncHealthCenterAsync()
        => OpenShellSyncHealthCenterAsync();

    public Task ShowRepositorySettingsPageAsync(int repositoryId)
        => OpenRepositorySettingsAsync(repositoryId);

    [RelayCommand]
    private async Task ToggleGlobalTagPickerAsync()
    {
        await Search.LoadAsync(forceRefresh: true);
        Search.ToggleTagPickerCommand.Execute(null);
    }

    private async Task OpenGlobalSearchForTagAsync(string tag)
    {
        _pageBeforeSearch = CurrentPage;
        CurrentPage = Search;
        await WaitForUiFrameAsync();
        await Search.LoadAsync(forceRefresh: !Search.HasLoadedData);
    }

    private async Task OpenRepositoryAsync(int repositoryId)
    {
        _log.LogInformation("Opening repository explorer. RepositoryId {RepositoryId}", repositoryId);
        await RunShellBusyActionAsync(
            "dashboard.open_repository_title",
            "dashboard.open_repository_detail",
            async () =>
            {
                await Explorer.LoadAsync(repositoryId);
                CurrentPage = Explorer;
            });
        await RefreshShellActivityAsync();
    }

    private async Task OpenRepositorySettingsAsync(int repositoryId)
    {
        _returnToAppSettingsFromRepositorySettings = false;
        _log.LogInformation("Opening repository settings. RepositoryId {RepositoryId}", repositoryId);
        await RunShellBusyActionAsync(
            "repo_settings.loading_title",
            "repo_settings.loading_detail",
            async () =>
            {
                await Settings.LoadAsync(repositoryId);
                CurrentPage = Settings;
            });
        await RefreshShellActivityAsync();
    }

    private async Task OpenRepositorySettingsFromAppSettingsAsync(int repositoryId)
    {
        _returnToAppSettingsFromRepositorySettings = true;
        _log.LogInformation("Opening repository settings from app settings. RepositoryId {RepositoryId}", repositoryId);
        await RunShellBusyActionAsync(
            "repo_settings.loading_title",
            "repo_settings.loading_detail",
            async () =>
            {
                await Settings.LoadAsync(repositoryId);
                CurrentPage = Settings;
            });
        await RefreshShellActivityAsync();
    }

    private async Task OpenRepositoryEntryFromSearchAsync(int repositoryId, string relativePath, bool isDirectory)
    {
        _log.LogInformation(
            "Opening repository entry from search. RepositoryId {RepositoryId}. RelativePath {RelativePath}. IsDirectory {IsDirectory}",
            repositoryId,
            relativePath,
            isDirectory);

        await RunShellBusyActionAsync(
            "dashboard.open_repository_title",
            "dashboard.open_repository_detail",
            async () =>
            {
                await Explorer.LoadAsync(repositoryId);
                await Explorer.FocusEntryAsync(relativePath, isDirectory);
                CurrentPage = Explorer;
            });
    }

    private async Task OpenRepositorySnapshotFromSearchAsync(int repositoryId, long snapshotId)
    {
        _log.LogInformation(
            "Opening repository snapshot from search. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}",
            repositoryId,
            snapshotId);

        await RunShellBusyActionAsync(
            "dashboard.open_repository_title",
            "dashboard.open_repository_detail",
            async () =>
            {
                await Explorer.LoadAsync(repositoryId);
                await Explorer.FocusSnapshotAsync(snapshotId);
                CurrentPage = Explorer;
            });
    }

    private async Task OnRepositoryUpdatedAsync(int repositoryId)
    {
        await RunShellBusyActionAsync(
            "dashboard.open_repository_title",
            "dashboard.open_repository_detail",
            async () =>
            {
                await Dashboard.LoadAsync();
                await Explorer.LoadAsync(repositoryId);
                CurrentPage = Explorer;
            });
        await RefreshShellActivityAsync();
    }

    private void OnRepositoryDeleted()
    {
        _log.LogInformation("Repository deleted. Returning to dashboard");
        _ = ShowDashboardAsync();
    }

    private void ShowDashboard()
    {
        if (_returnToAppSettingsFromRepositorySettings && ReferenceEquals(CurrentPage, Settings))
        {
            _returnToAppSettingsFromRepositorySettings = false;
            _log.LogInformation("Returning from repository settings to app settings");
            CurrentPage = AppSettings;
            return;
        }

        _ = ShowDashboardAsync();
    }

    private void ReturnFromSearch()
    {
        if (_pageBeforeSearch is not null && !ReferenceEquals(_pageBeforeSearch, Search))
        {
            CurrentPage = _pageBeforeSearch;
            return;
        }

        _ = ShowDashboardAsync();
    }

    private async Task ShowDashboardAsync()
    {
        _log.LogInformation("Showing dashboard page");
        await RunShellBusyActionAsync(
            "dashboard.loading_title",
            "dashboard.loading_detail",
            async () =>
            {
                await Dashboard.LoadAsync();
                CurrentPage = Dashboard;
            });
        await RefreshShellActivityAsync();
    }

    private async Task OnExperienceModeRefreshRequestedAsync(string modeCode)
    {
        _log.LogInformation("Refreshing app after experience mode change. Mode {Mode}", modeCode);
        await RunShellBusyActionAsync(
            "app_settings.loading_title",
            "app_settings.loading_detail",
            RefreshShellStateAsync);
    }

    [RelayCommand]
    private async Task GuidedTourNextAsync()
    {
        if (_guidedTourIndex >= _guidedTourSteps.Count - 1)
        {
            await CompleteGuidedTourAsync();
            return;
        }

        await MoveToGuidedTourStepAsync(_guidedTourIndex + 1);
    }

    [RelayCommand(CanExecute = nameof(CanGuidedTourGoBack))]
    private async Task GuidedTourBackAsync()
    {
        if (!CanGuidedTourGoBack)
            return;

        await MoveToGuidedTourStepAsync(_guidedTourIndex - 1);
    }

    [RelayCommand]
    private async Task GuidedTourSkipAsync()
    {
        _log.LogInformation("Guided tour skipped at step {StepIndex}", _guidedTourIndex + 1);
        await CompleteGuidedTourAsync();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RefreshThemeState();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLanguageState();
        RefreshConnectivityState();
        RefreshGuidedTourLocalization();
    }

    private void OnConnectivityStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshConnectivityState();
            _ = RefreshShellActivityAsync();
        });
    }

    private void RefreshThemeState()
    {
        var dark = _theme.IsDarkTheme;
        ThemeToggleGlyph = dark ? "\uE706" : "\uE793";
    }

    private void RefreshLanguageState()
    {
        var code = _localization.CurrentLanguageCode;
        LanguageToggleLabel = string.IsNullOrWhiteSpace(code)
            ? "EN"
            : code.ToUpperInvariant();
        NotifyShellHeaderStateChanged();
    }

    private void RefreshConnectivityState()
    {
        if (!ShouldShowShellConnectivityBanner)
        {
            ShowConnectivityBanner = false;
            ConnectivityBannerText = string.Empty;
            OnPropertyChanged(nameof(ConnectivityBannerAccentColor));
            OnPropertyChanged(nameof(ConnectivityBannerBackgroundColor));
            return;
        }

        switch (_connectivity.Snapshot.State)
        {
            case ConnectivityState.InternetUnavailable:
                ShowConnectivityBanner = true;
                ConnectivityBannerText = Loc.T("connectivity.banner.internet_required");
                break;
            case ConnectivityState.CloudUnavailable:
                ShowConnectivityBanner = true;
                ConnectivityBannerText = Loc.T("connectivity.banner.cloud_unavailable");
                break;
            default:
                ShowConnectivityBanner = false;
                ConnectivityBannerText = string.Empty;
                break;
        }

        OnPropertyChanged(nameof(ConnectivityBannerAccentColor));
        OnPropertyChanged(nameof(ConnectivityBannerBackgroundColor));
    }

    private async Task RefreshShellActivityAsync()
    {
        try
        {
            var recent = await _journal.GetRecentAsync(3);
            ApplyShellActivityState(recent);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to refresh shell activity strip");
            ApplyShellActivityState(Array.Empty<OperationJournalEntryDto>());
        }
    }

    private void ApplyShellActivityState(IReadOnlyList<OperationJournalEntryDto> recentEntries)
    {
        var repositories = Dashboard.Repositories.ToList();
        var running = repositories.Sum(repository => repository.QueueRunningCount);
        var pending = repositories.Sum(repository => repository.QueuePendingCount);
        var retries = repositories.Sum(repository => repository.QueueRetryCount);
        var conflicts = repositories.Sum(repository => repository.QueueConflictCount);
        var failures = repositories.Sum(repository => repository.QueueDeadLetterCount + repository.QueueFailedCount);
        var issueRepositories = repositories.Count(repository =>
            repository.QueueRetryCount > 0 ||
            repository.QueueConflictCount > 0 ||
            repository.QueueDeadLetterCount > 0 ||
            repository.QueueFailedCount > 0);

        if (running > 0)
        {
            ShowShellActivityStrip = ShouldShowShellActivity($"running:{running}:{pending}:{retries}:{conflicts}:{failures}");
            ShellActivityText = Loc.F("main.shell_activity_running", running, pending);
            ShellActivityDetailText = Loc.F("main.shell_activity_running_detail", retries, conflicts, failures);
            ShellActivityAccentColor = "#38BDF8";
            ShellActivityBackgroundColor = "#1638BDF8";
            ShellActivityBadgeText = Loc.T("main.shell_activity_badge_running");
            ShowShellActivityBadge = true;
            return;
        }

        if (issueRepositories > 0)
        {
            ShowShellActivityStrip = ShouldShowShellActivity($"issues:{issueRepositories}:{retries}:{conflicts}:{failures}");
            ShellActivityText = Loc.F("main.shell_activity_issues", issueRepositories);
            ShellActivityDetailText = Loc.F("main.shell_activity_issues_detail", retries, conflicts, failures);
            ShellActivityAccentColor = failures > 0 || conflicts > 0 ? "#F97316" : "#F59E0B";
            ShellActivityBackgroundColor = failures > 0 || conflicts > 0 ? "#1AF97316" : "#1AF59E0B";
            ShellActivityBadgeText = Loc.F("main.shell_activity_badge_issues", issueRepositories);
            ShowShellActivityBadge = true;
            return;
        }

        ShowShellActivityStrip = false;
        _shellActivityStateKey = string.Empty;
        _isShellActivityDismissed = false;
        ShellActivityText = string.Empty;
        ShellActivityDetailText = string.Empty;
        ShellActivityAccentColor = "#94A3B8";
        ShellActivityBackgroundColor = "#1494A3B8";
        ShellActivityBadgeText = string.Empty;
        ShowShellActivityBadge = false;
    }

    private bool ShouldShowShellActivity(string stateKey)
    {
        _shellActivityStateKey = stateKey;
        _isShellActivityDismissed = _dismissedShellActivityKeys.Contains(stateKey);

        return !_isShellActivityDismissed;
    }

    [RelayCommand]
    private void DismissShellActivityStrip()
    {
        if (!string.IsNullOrWhiteSpace(_shellActivityStateKey))
            _dismissedShellActivityKeys.Add(_shellActivityStateKey);

        _isShellActivityDismissed = true;
        ShowShellActivityStrip = false;
    }

    private string FormatRelativeTime(DateTime timestampUtc)
    {
        var localTime = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc.ToLocalTime() : timestampUtc;
        var delta = DateTime.Now - localTime;

        if (delta.TotalSeconds < 45)
            return Loc.T("dashboard.just_now");

        if (delta.TotalMinutes < 60)
            return Loc.F("dashboard.minutes_ago", Math.Max(1, (int)delta.TotalMinutes));

        if (delta.TotalHours < 24)
            return Loc.F("dashboard.hours_ago", Math.Max(1, (int)delta.TotalHours));

        return Loc.F("dashboard.days_ago", Math.Max(1, (int)delta.TotalDays));
    }

    private async Task RefreshConnectivityAsync()
    {
        try
        {
            await _connectivity.RefreshAsync();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Connectivity refresh failed during shell load");
        }
    }

    private void RefreshGuidedTourLocalization()
    {
        if (!IsGuidedTourVisible || _guidedTourIndex < 0 || _guidedTourIndex >= _guidedTourSteps.Count)
            return;

        var step = _guidedTourSteps[_guidedTourIndex];
        GuidedTourTitle = Loc.T(step.TitleKey);
        GuidedTourDescription = Loc.T(step.DescriptionKey);
        GuidedTourStepText = Loc.F("tour.step_counter", _guidedTourIndex + 1, _guidedTourSteps.Count);
        OnPropertyChanged(nameof(GuidedTourNextLabel));
    }

    private async Task RefreshShellStateAsync()
    {
        var currentPage = CurrentPage;
        var explorerRepositoryId = Explorer.RepositoryId;
        var settingsRepositoryId = Settings.RepositoryId;
        var selectedSettingsTab = AppSettings.SelectedTabKey;

        await Dashboard.LoadAsync();

        if (explorerRepositoryId > 0)
            await Explorer.LoadAsync(explorerRepositoryId);

        if (settingsRepositoryId > 0)
            await Settings.LoadAsync(settingsRepositoryId);

        if (ReferenceEquals(currentPage, Search))
            await Search.LoadAsync(forceRefresh: true);

        await AppSettings.LoadAsync();
        AppSettings.SelectTabByKey(selectedSettingsTab);
        await RefreshShellActivityAsync();

        if (ReferenceEquals(currentPage, Explorer) && explorerRepositoryId > 0)
        {
            CurrentPage = Explorer;
            return;
        }

        if (ReferenceEquals(currentPage, Settings) && settingsRepositoryId > 0)
        {
            CurrentPage = Settings;
            return;
        }

        if (ReferenceEquals(currentPage, AppSettings))
        {
            CurrentPage = AppSettings;
            return;
        }

        if (ReferenceEquals(currentPage, Search))
        {
            CurrentPage = Search;
            return;
        }

        CurrentPage = Dashboard;
    }

    [RelayCommand]
    private async Task OpenShellOperationJournalAsync()
    {
        await RunShellBusyActionAsync(
            "operation_journal.loading_title",
            "operation_journal.loading_detail",
            async () =>
            {
                var owner = _windows.GetActiveWindow();
                var window = _windows.Create<OperationJournalWindow>();
                if (window.DataContext is not OperationJournalWindowViewModel vm)
                    return;

                await vm.LoadAsync();

                if (owner is not null)
                    await _windows.ShowDialogAsync(window, owner);
                else
                    _windows.Show(window);
            },
            ex => _log.LogError(ex, "Failed to open operation journal window from shell"));
    }

    [RelayCommand]
    private async Task OpenShellOperationMonitorAsync()
    {
        await RunShellBusyActionAsync(
            "app_settings.operation_monitor_loading_title",
            "app_settings.operation_monitor_loading_detail",
            () =>
            {
                var window = _windows.Create<OperationMonitorWindow>();
                _windows.Show(window);
                return Task.CompletedTask;
            },
            ex => _log.LogError(ex, "Failed to open operation monitor window from shell"));
    }

    [RelayCommand]
    private async Task OpenShellSyncHealthCenterAsync()
    {
        await RunShellBusyActionAsync(
            "app_settings.sync_health_center_loading_title",
            "app_settings.sync_health_center_loading_detail",
            async () =>
            {
                await AppSettings.ShowCloudSyncHealthCenterAsync();
                await RefreshShellActivityAsync();
            },
            ex => _log.LogError(ex, "Failed to open sync health center from shell"));
    }

    private bool HasCloudAccessInShell
        => AppSettings.HasActiveProfile || Dashboard.HasCloudAccess || Settings.HasCloudAccess;

    private bool ShouldShowShellConnectivityBanner => false;

    private void OnChildShellStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepositoryExplorerViewModel.RepositoryName)
            or nameof(RepositoryExplorerViewModel.RepositoryPath)
            or nameof(RepositorySettingsViewModel.RepositoryName)
            or nameof(RepositorySettingsViewModel.DirectoryPath))
        {
            Dispatcher.UIThread.Post(NotifyShellHeaderStateChanged);
        }

        OnChildCloudAccessChanged(sender, e);
    }

    private void OnChildCloudAccessChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(RepositoryDashboardViewModel.HasCloudAccess)
            and not nameof(RepositorySettingsViewModel.HasCloudAccess)
            and not nameof(AppSettingsViewModel.HasActiveProfile))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            RefreshConnectivityState();
            OnPropertyChanged(nameof(ShowShellSyncCenterAction));
        });
    }

    private void NotifyShellHeaderStateChanged()
    {
        OnPropertyChanged(nameof(ShowShellBackButton));
        OnPropertyChanged(nameof(ShowShellBrandBadge));
        OnPropertyChanged(nameof(ShellTitle));
        OnPropertyChanged(nameof(ShellSubtitle));
        OnPropertyChanged(nameof(HasShellSubtitle));
        OnPropertyChanged(nameof(ShellSubtitleTooltip));
    }

    private async Task RunShellBusyActionAsync(string titleKey, string detailKey, Func<Task> action)
    {
        if (IsShellBusy)
        {
            await action();
            return;
        }

        try
        {
            IsShellBusy = true;
            ShellBusyTitle = Loc.T(titleKey);
            ShellBusyDetail = Loc.T(detailKey);

            await WaitForUiFrameAsync();
            await action();
        }
        finally
        {
            IsShellBusy = false;
            ShellBusyTitle = string.Empty;
            ShellBusyDetail = string.Empty;
        }
    }

    private async Task RunShellBusyActionAsync(
        string titleKey,
        string detailKey,
        Func<Task> action,
        Action<Exception> onError)
    {
        try
        {
            await RunShellBusyActionAsync(titleKey, detailKey, action);
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }

    private async Task TryStartPendingGuidedTourAsync()
    {
        if (_isGuidedTourStarting || !_onboardingState.ConsumeFirstRunTourRequest())
            return;

        _isGuidedTourStarting = true;

        try
        {
            _guidedTourSteps.Clear();
            _guidedTourSteps.AddRange(BuildGuidedTourSteps());
            if (_guidedTourSteps.Count == 0)
                return;

            _log.LogInformation("Starting first-run guided tour. Steps {StepCount}", _guidedTourSteps.Count);
            await MoveToGuidedTourStepAsync(0);
        }
        finally
        {
            _isGuidedTourStarting = false;
        }
    }

    private List<GuidedTourStep> BuildGuidedTourSteps()
        =>
        [
            new("DashboardSearchBox", "tour.step.dashboard_search.title", "tour.step.dashboard_search.description", EnsureDashboardForTourAsync),
            new("DashboardCreateRepositoryButton", "tour.step.dashboard_create.title", "tour.step.dashboard_create.description", EnsureDashboardForTourAsync),
            new("ExplorerTreePanel", "tour.step.explorer_tree.title", "tour.step.explorer_tree.description", EnsureExplorerForTourAsync),
            new("ExplorerItemsListBox", "tour.step.explorer_files.title", "tour.step.explorer_files.description", EnsureExplorerForTourAsync),
            new("ExplorerDetailsPanel", "tour.step.explorer_details.title", "tour.step.explorer_details.description", EnsureExplorerForTourAsync),
            new("ExplorerRestoreActionsPanel", "tour.step.explorer_restore.title", "tour.step.explorer_restore.description", EnsureExplorerRestoreForTourAsync),
            new("ExplorerSnapshotHistoryButton", "tour.step.explorer_history.title", "tour.step.explorer_history.description", EnsureExplorerForTourAsync),
            new("SnapshotHistoryCommitsPanel", "tour.step.snapshot_history_panel.title", "tour.step.snapshot_history_panel.description", EnsureExplorerSnapshotHistoryForTourAsync),
            new("ExplorerOpenSettingsButton", "tour.step.explorer_settings.title", "tour.step.explorer_settings.description", EnsureExplorerAfterHistoryForTourAsync),
            new("RepositorySettingsRepositoryCard", "tour.step.repository_info.title", "tour.step.repository_info.description", EnsureRepositorySettingsForTourAsync),
            new("RepositorySettingsFormatsCard", "tour.step.repository_formats.title", "tour.step.repository_formats.description", EnsureRepositorySettingsForTourAsync),
            new("RepositorySettingsCloudSyncStatusGrid", "tour.step.repository_sync.title", "tour.step.repository_sync.description", EnsureRepositorySettingsForTourAsync),
            new("RepositorySettingsRetentionPolicyCard", "tour.step.repository_retention.title", "tour.step.repository_retention.description", EnsureRepositorySettingsForTourAsync),
            new("MainGlobalSettingsButton", "tour.step.open_settings.title", "tour.step.open_settings.description", EnsureExplorerForTourAsync),
            new("AppSettingsExperienceComboBox", "tour.step.settings_mode.title", "tour.step.settings_mode.description", ShowAppSettingsForTourAsync),
            new("AppSettingsUserSessionCard", "tour.step.settings_user.title", "tour.step.settings_user.description", ShowAppSettingsUserForTourAsync),
            new("AppSettingsSyncActionsPanel", "tour.step.settings_sync.title", "tour.step.settings_sync.description", ShowAppSettingsSyncForTourAsync),
            new("AppSettingsOperationJournalHeader", "tour.step.settings_journal.title", "tour.step.settings_journal.description", ShowAppSettingsForTourAsync)
        ];

    private async Task MoveToGuidedTourStepAsync(int index)
    {
        if (index < 0)
            index = 0;

        if (index >= _guidedTourSteps.Count)
        {
            await CompleteGuidedTourAsync();
            return;
        }

        var step = _guidedTourSteps[index];
        if (step.EnterAsync is not null)
        {
            var canShowStep = await step.EnterAsync();
            if (!canShowStep)
            {
                if (index < _guidedTourSteps.Count - 1)
                    await MoveToGuidedTourStepAsync(index + 1);
                else
                    await CompleteGuidedTourAsync();

                return;
            }
        }

        _guidedTourIndex = index;
        GuidedTourTargetName = step.TargetName;
        GuidedTourTitle = Loc.T(step.TitleKey);
        GuidedTourDescription = Loc.T(step.DescriptionKey);
        GuidedTourStepText = Loc.F("tour.step_counter", index + 1, _guidedTourSteps.Count);
        IsGuidedTourVisible = true;
        GuidedTourBackCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGuidedTourGoBack));
        OnPropertyChanged(nameof(GuidedTourNextLabel));
    }

    private async Task CompleteGuidedTourAsync()
    {
        _guidedTourIndex = -1;
        IsGuidedTourVisible = false;
        GuidedTourTargetName = string.Empty;
        GuidedTourTitle = string.Empty;
        GuidedTourDescription = string.Empty;
        GuidedTourStepText = string.Empty;
        GuidedTourBackCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGuidedTourGoBack));
        OnPropertyChanged(nameof(GuidedTourNextLabel));
        await ShowDashboardAsync();
    }

    private async Task<bool> EnsureDashboardForTourAsync()
    {
        await Dashboard.LoadAsync();
        CurrentPage = Dashboard;
        return true;
    }

    private async Task<bool> EnsureExplorerForTourAsync()
    {
        var repository = Dashboard.Repositories.FirstOrDefault();
        if (repository is null)
        {
            await Dashboard.LoadAsync();
            repository = Dashboard.Repositories.FirstOrDefault();
        }

        if (repository is null)
            return false;

        await Explorer.LoadAsync(repository.Id);
        CurrentPage = Explorer;
        return true;
    }

    private async Task<bool> EnsureExplorerSnapshotHistoryForTourAsync()
    {
        var canShowExplorer = await EnsureExplorerForTourAsync();
        if (!canShowExplorer)
            return false;

        if (!Explorer.IsSnapshotHistoryMenuOpen)
            await Explorer.ShowSnapshotHistoryCommand.ExecuteAsync(null);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!Explorer.IsSnapshotHistoryLoading)
                break;

            await Task.Delay(75);
        }

        return Explorer.IsSnapshotHistoryMenuOpen;
    }

    private async Task<bool> EnsureExplorerAfterHistoryForTourAsync()
    {
        var canShowExplorer = await EnsureExplorerForTourAsync();
        if (!canShowExplorer)
            return false;

        if (Explorer.IsSnapshotHistoryMenuOpen)
            Explorer.CloseSnapshotHistoryMenuCommand.Execute(null);

        return true;
    }

    private async Task<bool> ShowAppSettingsForTourAsync()
    {
        CurrentPage = AppSettings;
        await WaitForUiFrameAsync();
        await AppSettings.LoadAsync();
        AppSettings.SelectTabByKey("general");
        return true;
    }

    private async Task<bool> ShowAppSettingsUserForTourAsync()
    {
        CurrentPage = AppSettings;
        await WaitForUiFrameAsync();
        await AppSettings.LoadAsync();
        AppSettings.SelectTabByKey("user");
        return true;
    }

    private async Task<bool> ShowAppSettingsSyncForTourAsync()
    {
        CurrentPage = AppSettings;
        await WaitForUiFrameAsync();
        await AppSettings.LoadAsync();
        AppSettings.SelectTabByKey("sync");
        return true;
    }

    private static async Task WaitForUiFrameAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private async Task<bool> EnsureRepositorySettingsForTourAsync()
    {
        var repository = Dashboard.Repositories.FirstOrDefault();
        if (repository is null)
        {
            await Dashboard.LoadAsync();
            repository = Dashboard.Repositories.FirstOrDefault();
        }

        if (repository is null)
            return false;

        await Settings.LoadAsync(repository.Id);
        CurrentPage = Settings;
        return true;
    }

    private async Task<bool> EnsureExplorerRestoreForTourAsync()
    {
        var canShowExplorer = await EnsureExplorerForTourAsync();
        if (!canShowExplorer)
            return false;

        foreach (var item in Explorer.Items.Where(i => !i.IsDirectory).Take(10))
        {
            Explorer.SelectedItem = item;

            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (!Explorer.IsVersionLoading)
                    break;

                await Task.Delay(75);
            }

            if (Explorer.FileVersions.Count > 0)
                return true;
        }

        return false;
    }

    private void OnFirstRunTourRequested(object? sender, EventArgs e)
    {
        if (!_isLoaded)
            return;

        _ = TryStartPendingGuidedTourAsync();
    }
}
