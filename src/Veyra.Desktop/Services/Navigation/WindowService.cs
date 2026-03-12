using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Veyra.Desktop.Services.Navigation;

public sealed class WindowService : IWindowService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<WindowService> _log;

    public WindowService(IServiceProvider sp, ILogger<WindowService> log)
    {
        _sp = sp;
        _log = log;
    }

    public T Create<T>() where T : Window
    {
        _log.LogDebug("Creating window {WindowType}", typeof(T).Name);
        return _sp.GetRequiredService<T>();
    }

    public void Show(Window window)
    {
        _log.LogInformation("Showing window {WindowType}", window.GetType().Name);
        window.Show();
    }

    public async Task ShowDialogAsync(Window window, Window owner)
    {
        _log.LogInformation(
            "Showing dialog {WindowType} with owner {OwnerType}",
            window.GetType().Name,
            owner.GetType().Name);
        await window.ShowDialog(owner);
    }

    public Window? GetActiveWindow()
        => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows?
           .FirstOrDefault(w => w.IsActive)
           ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public void SwitchMainWindow(Window newMain, Window? toClose = null)
    {
        _log.LogInformation(
            "Switching main window. New {NewWindowType}. Closing {OldWindowType}",
            newMain.GetType().Name,
            toClose?.GetType().Name ?? "(none)");

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