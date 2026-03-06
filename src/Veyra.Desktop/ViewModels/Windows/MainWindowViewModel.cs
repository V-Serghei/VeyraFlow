using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.ViewModels.Pages.Dashboard;
using Veyra.Desktop.ViewModels.Pages.Explorer;
using Veyra.Desktop.ViewModels.Pages.RepositorySettings;
using Veyra.Desktop.ViewModels.Pages.Settings;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public RepositoryDashboardViewModel Dashboard { get; }
    public RepositoryExplorerViewModel Explorer { get; }
    public RepositorySettingsViewModel Settings { get; }
    public AppSettingsViewModel AppSettings { get; }

    [ObservableProperty] private object? _currentPage;

    public MainWindowViewModel(
        RepositoryDashboardViewModel dashboard,
        RepositoryExplorerViewModel explorer,
        RepositorySettingsViewModel settings,
        AppSettingsViewModel appSettings)
    {
        Dashboard = dashboard;
        Explorer = explorer;
        Settings = settings;
        AppSettings = appSettings;

        Dashboard.OpenRepositoryRequested += OpenRepositoryAsync;
        Dashboard.OpenRepositorySettingsRequested += OpenRepositorySettingsAsync;

        Explorer.BackRequested += ShowDashboard;
        Explorer.OpenSettingsRequested += OpenRepositorySettingsAsync;

        Settings.BackRequested += ShowDashboard;
        Settings.RepositoryUpdated += OnRepositoryUpdatedAsync;
        Settings.RepositoryDeleted += OnRepositoryDeleted;

        AppSettings.BackRequested += ShowDashboard;

        CurrentPage = Dashboard;
    }

    [RelayCommand]
    private void OpenGlobalSettings()
    {
        CurrentPage = AppSettings;
    }

    public async void OnLoaded()
    {
        await Dashboard.LoadAsync();
        CurrentPage = Dashboard;
    }

    private async Task OpenRepositoryAsync(int repositoryId)
    {
        await Explorer.LoadAsync(repositoryId);
        CurrentPage = Explorer;
    }

    private async Task OpenRepositorySettingsAsync(int repositoryId)
    {
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
        _ = ShowDashboardAsync();
    }

    private void ShowDashboard()
    {
        _ = ShowDashboardAsync();
    }

    private async Task ShowDashboardAsync()
    {
        await Dashboard.LoadAsync();
        CurrentPage = Dashboard;
    }
}
