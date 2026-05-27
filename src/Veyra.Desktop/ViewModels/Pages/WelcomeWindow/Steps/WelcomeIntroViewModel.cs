using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Pages.WelcomeWindow;

public partial class WelcomeIntroViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;

    public WelcomeIntroViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService;

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
        };
    }

    public event System.Action? StartRequested;

    [RelayCommand]
    private void Start() => StartRequested?.Invoke();

    [RelayCommand]
    private async Task LearnMore() => await _navigationService.ShowInfoAsync();

    public string Title => Loc.T("welcome.intro_title");
    public string Subtitle => Loc.T("welcome.intro_subtitle");
}
