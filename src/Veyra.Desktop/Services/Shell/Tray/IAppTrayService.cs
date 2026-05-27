using Avalonia.Controls;

namespace Veyra.Desktop.Services.Shell.Tray;

public interface IAppTrayService
{
    bool IsInitialized { get; }
    bool IsExitRequested { get; }

    void Initialize(TrayIcon trayIcon);
    void PrepareForShutdown();
    void HandleMainWindowClosing(Window window, WindowClosingEventArgs e);
}
