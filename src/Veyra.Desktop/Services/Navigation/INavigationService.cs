using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Navigation;

public interface INavigationService
{
    void ShowWelcome();
    void ShowLogin();
    void GoToMain();
    Task ShowInfoAsync();
    Task ShowProgramOverviewAsync();
}
