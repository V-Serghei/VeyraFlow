using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.IO;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Veyra.Desktop.Services.Navigation;
using Veyra.Infrastructure.Data.Persistence;
using AvaloniaApplication = Avalonia.Application;
using DependencyInjection = Veyra.Desktop.CompositionRoot.DependencyInjection;

namespace Veyra.Desktop;

public partial class App : AvaloniaApplication
{
    public static IServiceProvider? _serviceProvider { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        //DB path
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow", "veyra.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connectionString = $"Data Source={dbPath}";

        // Dependency Injection setup
        _serviceProvider = DependencyInjection.BuildServiceProvider(connectionString);


        using (IServiceScope scope = _serviceProvider.CreateScope())
        {
            VeyraDbContext db = scope.ServiceProvider.GetRequiredService<VeyraDbContext>();
            db.Database.EnsureCreated();
        }
        Veyra.Infrastructure.Data.DependencyInjection.EnableWalMode(connectionString);


        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Configure services
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime classicDesktop)
            {
                // Set shutdown mode to close the application when the last window is closed
                classicDesktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                // Show welcome window on startup
                var nav = _serviceProvider.GetRequiredService<INavigationService>();
                nav.ShowWelcome(); // synchronous call to show the welcome window
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Log.CloseAndFlush();
    }

}
