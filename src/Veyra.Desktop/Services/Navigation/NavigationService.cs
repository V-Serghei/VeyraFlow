using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Veyra.Desktop.Views;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.Services.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly IWindowService _windows;
    private readonly ILogger<NavigationService> _log;

    public NavigationService(IWindowService windows, ILogger<NavigationService> log)
    {
        _windows = windows;
        _log = log;
    }

    public void ShowWelcome()
    {
        _log.LogInformation("Showing welcome window");
        var w = _windows.Create<WelcomeWindow>();
        _windows.Show(w);
    }

    public void GoToMain()
    {
        _log.LogInformation("Switching navigation shell to main window");
        var active = _windows.GetActiveWindow();
        var main = _windows.Create<MainWindow>();
        _windows.SwitchMainWindow(main, active);
    }

    public async Task ShowInfoAsync()
    {
        _log.LogInformation("Opening info window");
        var owner = _windows.GetActiveWindow();
        var info = _windows.Create<InfoWindow>();
        if (owner is null)
            _windows.Show(info);
        else
            await _windows.ShowDialogAsync(info, owner);
    }
}