using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Persistence;
using Veyra.Desktop.Services.Scheduling;
using Veyra.Desktop.Styling;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Native;
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
        ThemeManager.Instance.ApplyCurrentTheme();

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
            DatabaseStartupBootstrapper.Initialize(db);

            var nativeHealth = NativeRuntimeHealth.Probe();
            if (nativeHealth.IsHealthy)
            {
                Log.Information(
                    "Native runtime smoke-check passed. Library {Path}",
                    nativeHealth.LoadedPath ?? "(unknown)");
            }
            else
            {
                Log.Warning(
                    "Native runtime smoke-check failed. Library {Path}. Error {Error}. Missing entrypoints {Missing}",
                    nativeHealth.LoadedPath ?? "(not loaded)",
                    nativeHealth.ErrorMessage ?? "(none)",
                    string.Join(", ", nativeHealth.MissingEntrypoints));
            }

            var userProfiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            var tokenPolicy = scope.ServiceProvider.GetRequiredService<IAccessTokenPolicyService>();
            var setup = scope.ServiceProvider.GetRequiredService<ISetupRepository>();

            var activeUsername = userProfiles.GetActiveUsernameAsync().GetAwaiter().GetResult();
            var syncOrchestrator = scope.ServiceProvider.GetService<IRepositoryCloudSyncOrchestrator>();
            if (!string.IsNullOrWhiteSpace(activeUsername))
            {
                var dirs = setup.GetWatchedDirectoriesAsync().GetAwaiter().GetResult();
                var exts = setup.GetTrackedExtensionsAsync().GetAwaiter().GetResult();

                var profile = userProfiles.GetActiveProfileAsync().GetAwaiter().GetResult();
                var tokenState = tokenPolicy.Evaluate(profile?.AccessToken);
                var canUseCloudSync = tokenState.CanUseForSync;

                if (!canUseCloudSync)
                {
                    Log.Information(
                        "Skipping cloud startup sync for user {Username}. TokenState {TokenState}. Reason {Reason}",
                        activeUsername,
                        tokenState.State,
                        tokenState.Description);
                }

                if (canUseCloudSync && (dirs.Count == 0 || exts.Count == 0))
                {
                    var sync = scope.ServiceProvider.GetService<IRepositoryCloudSyncOrchestrator>();
                    if (sync is not null)
                    {
                        try
                        {
                            sync.RestoreRepositoriesFromCloudAsync().GetAwaiter().GetResult();
                            dirs = setup.GetWatchedDirectoriesAsync().GetAwaiter().GetResult();
                            exts = setup.GetTrackedExtensionsAsync().GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Cloud restore at startup failed for user {Username}", activeUsername);
                        }
                    }
                }

                if (canUseCloudSync)
                {
                    try
                    {
                        syncOrchestrator?.ProcessPendingQueueAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Cloud pending queue resume failed at startup for user {Username}", activeUsername);
                    }
                }

                shouldOpenMain = dirs.Count > 0 && exts.Count > 0;
            }

            var recovery = scope.ServiceProvider.GetService<IRepositoryRecoveryService>();
            if (recovery is not null)
            {
                try
                {
                    var recoveryResults = recovery.RunStartupHealthCheckAsync().GetAwaiter().GetResult();
                    foreach (var result in recoveryResults.Where(r => !r.Success || r.AffectedRows > 0))
                    {
                        Log.Information(
                            "Startup health-check result. RepositoryId {RepositoryId}. Success {Success}. Action {Action}. Affected {Affected}. Summary {Summary}",
                            result.RepositoryId,
                            result.Success,
                            result.Action,
                            result.AffectedRows,
                            result.Summary);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Startup health-check failed.");
                }
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
