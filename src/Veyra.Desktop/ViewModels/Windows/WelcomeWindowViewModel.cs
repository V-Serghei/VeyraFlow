using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Windows;

public partial class WelcomeWindowViewModel: ObservableObject
{
    private readonly INavigationService _nav;
    public WelcomeWindowViewModel(INavigationService nav) => _nav = nav;

    [RelayCommand]
    private void Start() => _nav.GoToMain();

    [RelayCommand]
    private async void LearnMore() => await _nav.ShowInfoAsync();

    public string Title => "Добро пожаловать в Версионный Проводник";
    public string Subtitle =>
        "Проводник с версионным контролем. Отслеживайте изменения, создавайте снимки и не теряйте данные.";
}
