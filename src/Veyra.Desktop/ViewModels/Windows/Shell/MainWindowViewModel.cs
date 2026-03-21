using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Onboarding;
using Veyra.Desktop.Styling;
using Veyra.Desktop.ViewModels.Pages.Dashboard;
using Veyra.Desktop.ViewModels.Pages.Explorer;
using Veyra.Desktop.ViewModels.Pages.RepositorySettings;
using Veyra.Desktop.ViewModels.Pages.Search;
using Veyra.Desktop.ViewModels.Pages.Settings;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ThemeManager _theme = ThemeManager.Instance;
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
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

    public bool CanGuidedTourGoBack => _guidedTourIndex > 0;
    public string GuidedTourNextLabel => _guidedTourIndex >= _guidedTourSteps.Count - 1
        ? Loc.T("tour.finish")
        : Loc.T("tour.next");

    public MainWindowViewModel(
        RepositoryDashboardViewModel dashboard,
        RepositoryExplorerViewModel explorer,
        RepositorySettingsViewModel settings,
        GlobalSearchViewModel search,
        AppSettingsViewModel appSettings,
        OnboardingStateService onboardingState,
        ILogger<MainWindowViewModel> log)
    {
        Dashboard = dashboard;
        Explorer = explorer;
        Settings = settings;
        Search = search;
        AppSettings = appSettings;
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

        AppSettings.BackRequested += ShowDashboard;
        AppSettings.OpenRepositorySettingsRequested += OpenRepositorySettingsFromAppSettingsAsync;
        AppSettings.ExperienceModeRefreshRequested += OnExperienceModeRefreshRequestedAsync;
        AppSettings.LanguageRefreshRequested += OnLanguageRefreshRequestedAsync;
        _theme.ThemeChanged += OnThemeChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _onboardingState.FirstRunTourRequested += OnFirstRunTourRequested;

        CurrentPage = Dashboard;
        RefreshThemeState();
        RefreshLanguageState();
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
        await RunShellBusyActionAsync(
            "search.loading_title",
            "search.loading_detail",
            async () =>
            {
                CurrentPage = Search;
                await WaitForUiFrameAsync();
                await Search.LoadAsync(forceRefresh: !Search.HasLoadedData);
            });
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

        await RunShellBusyActionAsync(
            "main.language_switch_title",
            "main.language_switch_detail",
            async () =>
            {
                _localization.SetLanguage(languages[nextIndex].Code);
                RefreshLanguageState();
                _log.LogInformation("Language toggled. CurrentLanguage {Language}", _localization.CurrentLanguageCode);
                await RefreshShellStateAsync();
            });
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
        _isLoaded = true;
        await TryStartPendingGuidedTourAsync();
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
    }

    private async Task OnExperienceModeRefreshRequestedAsync(string modeCode)
    {
        _log.LogInformation("Refreshing app after experience mode change. Mode {Mode}", modeCode);
        await RunShellBusyActionAsync(
            "app_settings.loading_title",
            "app_settings.loading_detail",
            RefreshShellStateAsync);
    }

    private async Task OnLanguageRefreshRequestedAsync(string languageCode)
    {
        _log.LogInformation("Refreshing app after language change. Language {Language}", languageCode);
        await RunShellBusyActionAsync(
            "main.language_switch_title",
            "main.language_switch_detail",
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
        RefreshGuidedTourLocalization();
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
