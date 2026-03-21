using System.Threading.Tasks;
using Avalonia.Controls;

namespace Veyra.Desktop.Services.Navigation;

public interface IWindowService
{
    T Create<T>() where T : Window;
    void Show(Window window);
    Task ShowDialogAsync(Window window, Window owner);
    Window? GetActiveWindow();
    void SwitchMainWindow(Window newMain, Window? toClose = null);
}
