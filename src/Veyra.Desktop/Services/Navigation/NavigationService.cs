using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Veyra.Desktop.ViewModels.Windows;
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

    public void ShowLogin()
    {
        _log.LogInformation("Showing welcome window at login page");
        var w = _windows.Create<WelcomeWindow>();
        if (w.DataContext is WelcomeWindowViewModel vm)
            vm.NavigateToLoginDirectly();
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
        if (info.DataContext is InfoWindowViewModel infoModel
            && owner?.DataContext is MainWindowViewModel mainModel)
        {
            infoModel.DeepLinkRequested += mainModel.OpenHelpDeepLinkAsync;
        }

        if (owner is null)
            _windows.Show(info);
        else
            await _windows.ShowDialogAsync(info, owner);
    }

    public async Task ShowProgramOverviewAsync()
    {
        _log.LogInformation("Opening program overview window");
        var owner = _windows.GetActiveWindow();
        var overview = _windows.Create<ProgramOverviewWindow>();

        if (owner is null)
            _windows.Show(overview);
        else
            await _windows.ShowDialogAsync(overview, owner);
    }
}
