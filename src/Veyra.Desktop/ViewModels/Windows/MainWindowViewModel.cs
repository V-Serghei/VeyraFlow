using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.ViewModels.Pages.Dashboard;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public RepositoryDashboardViewModel Dashboard { get; }

    public MainWindowViewModel(RepositoryDashboardViewModel dashboard)
    {
        Dashboard = dashboard;
    }

    public async void OnLoaded()
    {
        await Dashboard.LoadAsync();
    }
}
