using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Navigation;

public interface INavigationService
{
    void ShowWelcome();
    void GoToMain();
    Task ShowInfoAsync();
}
