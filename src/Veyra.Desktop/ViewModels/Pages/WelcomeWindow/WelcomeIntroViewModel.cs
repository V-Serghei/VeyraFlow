using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Pages.WelcomeWindow;

public partial class WelcomeIntroViewModel(INavigationService navigationService) : ObservableObject
{
    private readonly INavigationService _navigationService = navigationService;

    public event System.Action? StartRequested;

    [RelayCommand]
    private void Start() => StartRequested?.Invoke();

    [RelayCommand]
    private async Task LearnMore() => await _navigationService.ShowInfoAsync();

    public string Title => "Welcome to the Versioned Explorer";
    public string Subtitle => "A file explorer with version control. Track changes, create snapshots, and never lose your data.";
}
