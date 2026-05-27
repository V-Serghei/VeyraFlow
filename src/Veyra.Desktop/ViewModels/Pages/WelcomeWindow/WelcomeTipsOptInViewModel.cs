using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Veyra.Desktop.ViewModels.Pages.WelcomeWindow;

public partial class WelcomeTipsOptInViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _enableTips  = true;

    public event System.Action<bool>? ContinueRequested;

    [RelayCommand]
    private void Continue() => ContinueRequested?.Invoke(EnableTips);
}
