using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Veyra.Desktop.Services.Navigation;

public sealed class WindowService : IWindowService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WindowService> _log;
    private readonly ConcurrentDictionary<Window, AsyncServiceScope> _windowScopes = new();

    public WindowService(IServiceScopeFactory scopeFactory, ILogger<WindowService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public T Create<T>() where T : Window
    {
        _log.LogDebug("Creating window {WindowType}", typeof(T).Name);
        var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            var window = scope.ServiceProvider.GetRequiredService<T>();
            AppWindowIconProvider.Apply(window);
            if (!_windowScopes.TryAdd(window, scope))
            {
                scope.Dispose();
                throw new InvalidOperationException($"Failed to register lifetime scope for window {typeof(T).Name}.");
            }

            window.Closed += OnWindowClosed;
            return window;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
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
            AppWindowIconProvider.Apply(newMain);
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

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        window.Closed -= OnWindowClosed;
        if (!_windowScopes.TryRemove(window, out var scope))
            return;

        _ = DisposeWindowScopeAsync(window, scope);
    }

    private async Task DisposeWindowScopeAsync(Window window, AsyncServiceScope scope)
    {
        try
        {
            await scope.DisposeAsync();
            _log.LogDebug("Disposed window scope for {WindowType}", window.GetType().Name);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to dispose scope for window {WindowType}", window.GetType().Name);
        }
    }
}
