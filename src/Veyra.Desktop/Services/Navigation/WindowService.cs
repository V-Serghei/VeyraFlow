using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;

namespace Veyra.Desktop.Services.Navigation;

public class WindowService(IServiceProvider sp) : IWindowService
{
    public T Create<T>() where T : Window => sp.GetRequiredService<T>();
    public void Show(Window window) => window.Show();
    public async Task ShowDialogAsync(Window window, Window owner) => await window.ShowDialog(owner);

    public Window? GetActiveWindow()
        => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows?
           .FirstOrDefault(w => w.IsActive)
           ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public void SwitchMainWindow(Window newMain, Window? toClose = null)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = newMain;
            newMain.Show();
            toClose?.Close();
        }
        else
        {
            newMain.Show();
            toClose?.Close();
        }
    }
}
