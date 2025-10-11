using System.Threading.Tasks;
using Veyra.Desktop.Views;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.Services.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly IWindowService _windows;
    public NavigationService(IWindowService windows) => _windows = windows;

    public void ShowWelcome()
    {
        var w = _windows.Create<WelcomeWindow>();
        _windows.Show(w);
    }

    public void GoToMain()
    {
        var active = _windows.GetActiveWindow();
        var main = _windows.Create<MainWindow>();
        _windows.SwitchMainWindow(main, active);
    }

    public async Task ShowInfoAsync()
    {
        var owner = _windows.GetActiveWindow();
        var info = _windows.Create<InfoWindow>();
        if (owner is null) _windows.Show(info);
        else await _windows.ShowDialogAsync(info, owner);
    }
}
