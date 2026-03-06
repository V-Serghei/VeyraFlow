using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.IO;
using Avalonia.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Setup;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Scheduling;
using Veyra.Infrastructure.Data.Persistence;
using AvaloniaApplication = Avalonia.Application;
using DependencyInjection = Veyra.Desktop.CompositionRoot.DependencyInjection;

namespace Veyra.Desktop;

public partial class App : AvaloniaApplication
{
    public static IServiceProvider? _serviceProvider { get; private set; } = null!;
    private static ISnapshotScheduler? _snapshotScheduler;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow", "veyra.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connectionString = $"Data Source={dbPath}";

        _serviceProvider = DependencyInjection.BuildServiceProvider(connectionString);

        var shouldOpenMain = false;

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VeyraDbContext>();
            db.Database.Migrate();
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

            var userProfiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            var setup = scope.ServiceProvider.GetRequiredService<ISetupRepository>();

            var activeUsername = userProfiles.GetActiveUsernameAsync().GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(activeUsername))
            {
                var dirs = setup.GetWatchedDirectoriesAsync().GetAwaiter().GetResult();
                var exts = setup.GetTrackedExtensionsAsync().GetAwaiter().GetResult();
                shouldOpenMain = dirs.Count > 0 && exts.Count > 0;
            }
        }

        _snapshotScheduler = _serviceProvider.GetService<ISnapshotScheduler>();
        _snapshotScheduler?.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime classicDesktop)
        {
            classicDesktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            classicDesktop.Exit += OnDesktopExit;

            var nav = _serviceProvider.GetRequiredService<INavigationService>();

            if (shouldOpenMain)
                nav.GoToMain();
            else
                nav.ShowWelcome();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            _snapshotScheduler?.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        Log.CloseAndFlush();
    }
}
