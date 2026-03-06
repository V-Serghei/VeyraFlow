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

    public string Title => "Версионный проводник для локальных проектов";
    public string Subtitle => "Выбирай папки, задавай форматы, получай историю изменений и безопасное восстановление версий без ручной работы в консоли.";
}
