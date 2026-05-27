using System;
using System.Threading.Tasks;
using Avalonia;
using Serilog;

namespace Veyra.Desktop;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
                Log.Fatal(ex, "AppDomain unhandled exception");
            else
                Log.Fatal("AppDomain unhandled exception: {Exception}", eventArgs.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "Unobserved task exception");
            eventArgs.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
