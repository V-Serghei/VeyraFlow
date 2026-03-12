using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Styling;
using Veyra.Desktop.ViewModels.Pages.Dashboard;
using Veyra.Desktop.ViewModels.Pages.Explorer;
using Veyra.Desktop.ViewModels.Pages.RepositorySettings;
using Veyra.Desktop.ViewModels.Pages.Settings;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ThemeManager _theme = ThemeManager.Instance;
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    private readonly ILogger<MainWindowViewModel> _log;
    private bool _returnToAppSettingsFromRepositorySettings;

    public RepositoryDashboardViewModel Dashboard { get; }
    public RepositoryExplorerViewModel Explorer { get; }
    public RepositorySettingsViewModel Settings { get; }
    public AppSettingsViewModel AppSettings { get; }

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _themeToggleGlyph = "\uE793";
    [ObservableProperty] private string _languageToggleLabel = "EN";

    public MainWindowViewModel(
        RepositoryDashboardViewModel dashboard,
        RepositoryExplorerViewModel explorer,
        RepositorySettingsViewModel settings,
        AppSettingsViewModel appSettings,
        ILogger<MainWindowViewModel> log)
    {
        Dashboard = dashboard;
        Explorer = explorer;
        Settings = settings;
        AppSettings = appSettings;
        _log = log;

        Dashboard.OpenRepositoryRequested += OpenRepositoryAsync;
        Dashboard.OpenRepositorySettingsRequested += OpenRepositorySettingsAsync;

        Explorer.BackRequested += ShowDashboard;
        Explorer.OpenSettingsRequested += OpenRepositorySettingsAsync;

        Settings.BackRequested += ShowDashboard;
        Settings.RepositoryUpdated += OnRepositoryUpdatedAsync;
        Settings.RepositoryDeleted += OnRepositoryDeleted;

        AppSettings.BackRequested += ShowDashboard;
        AppSettings.OpenRepositorySettingsRequested += OpenRepositorySettingsFromAppSettingsAsync;
        _theme.ThemeChanged += OnThemeChanged;
        _localization.LanguageChanged += OnLanguageChanged;

        CurrentPage = Dashboard;
        RefreshThemeState();
        RefreshLanguageState();
    }

    [RelayCommand]
    private async Task OpenGlobalSettingsAsync()
    {
        _log.LogInformation("Opening global settings page");
        await AppSettings.LoadAsync();
        CurrentPage = AppSettings;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        _theme.ToggleDarkLight();
        RefreshThemeState();
        _log.LogInformation("Theme toggled. DarkTheme {IsDarkTheme}", _theme.IsDarkTheme);
    }

    [RelayCommand]
    private void ToggleLanguage()
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

        _localization.SetLanguage(languages[nextIndex].Code);
        RefreshLanguageState();
        _log.LogInformation("Language toggled. CurrentLanguage {Language}", _localization.CurrentLanguageCode);
    }

    public async void OnLoaded()
    {
        _log.LogInformation("Main window loaded. Loading dashboard");
        await Dashboard.LoadAsync();
        CurrentPage = Dashboard;
    }

    private async Task OpenRepositoryAsync(int repositoryId)
    {
        _log.LogInformation("Opening repository explorer. RepositoryId {RepositoryId}", repositoryId);
        await Explorer.LoadAsync(repositoryId);
        CurrentPage = Explorer;
    }

    private async Task OpenRepositorySettingsAsync(int repositoryId)
    {
        _returnToAppSettingsFromRepositorySettings = false;
        _log.LogInformation("Opening repository settings. RepositoryId {RepositoryId}", repositoryId);
        await Settings.LoadAsync(repositoryId);
        CurrentPage = Settings;
    }

    private async Task OpenRepositorySettingsFromAppSettingsAsync(int repositoryId)
    {
        _returnToAppSettingsFromRepositorySettings = true;
        _log.LogInformation("Opening repository settings from app settings. RepositoryId {RepositoryId}", repositoryId);
        await Settings.LoadAsync(repositoryId);
        CurrentPage = Settings;
    }

    private async Task OnRepositoryUpdatedAsync(int repositoryId)
    {
        await Dashboard.LoadAsync();
        await Explorer.LoadAsync(repositoryId);
        CurrentPage = Explorer;
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

    private async Task ShowDashboardAsync()
    {
        _log.LogInformation("Showing dashboard page");
        await Dashboard.LoadAsync();
        CurrentPage = Dashboard;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RefreshThemeState();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLanguageState();
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
}
