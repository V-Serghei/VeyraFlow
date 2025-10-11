using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.ViewModels.Pages.WelcomeWindow;

public partial class WelcomeIntroViewModel(INavigationService navigationService) : ObservableObject
{
    private readonly INavigationService _navigationService = navigationService;

    public event System.Action? StartRequested;

    [RelayCommand]
    private void Start() => StartRequested?.Invoke();

    [RelayCommand]
    private async Task LearnMore() => await _navigationService.ShowInfoAsync();

    public string Title => "Добро пожаловать в Версионный Проводник";
    public string Subtitle => "Проводник с версионным контролем. Отслеживайте изменения, создавайте снимки и не теряйте данные.";
}
