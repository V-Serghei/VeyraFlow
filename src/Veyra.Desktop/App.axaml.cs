using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Persistence;
using Veyra.Desktop.Services.Scheduling;
using Veyra.Desktop.Styling;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Native;
using AvaloniaApplication = Avalonia.Application;
using DependencyInjection = Veyra.Desktop.CompositionRoot.DependencyInjection;

namespace Veyra.Desktop;

public partial class App : AvaloniaApplication
{
    public static IServiceProvider? _serviceProvider { get; private set; } = null!;
    private static ISnapshotScheduler? _snapshotScheduler;
    private static Task? _startupBackgroundTask;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Information("Application framework initialization started");
        ThemeManager.Instance.ApplyCurrentTheme();

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow", "veyra.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connectionString = $"Data Source={dbPath}";

        _serviceProvider = DependencyInjection.BuildServiceProvider(connectionString);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime classicDesktop)
        {
            classicDesktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            classicDesktop.Exit += OnDesktopExit;

            var nav = _serviceProvider.GetRequiredService<INavigationService>();
            nav.ShowWelcome();

            Log.Information(
                "Initial shell displayed immediately using welcome window while deferred startup resolves user state.");
        }

        _startupBackgroundTask = Task.Run(RunDeferredStartupAsync);

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            if (_startupBackgroundTask is { IsCompleted: false })
            {
                Log.Information("Deferred startup task is still running during shutdown; skipping wait to keep shutdown responsive.");
            }
            else if (_startupBackgroundTask is { IsFaulted: true } startupTask)
            {
                startupTask.Exception?.Handle(ex =>
                {
                    Log.Debug(ex, "Deferred startup task fault surfaced during shutdown.");
                    return true;
                });
            }

            if (_snapshotScheduler is not null)
                _ = StopSnapshotSchedulerSilentlyAsync(_snapshotScheduler);
        }
        catch
        {
        }

        Log.Information("Application shutting down");
        Log.CloseAndFlush();
    }

    private static async Task StopSnapshotSchedulerSilentlyAsync(ISnapshotScheduler snapshotScheduler)
    {
        try
        {
            await snapshotScheduler.StopAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task RunDeferredStartupAsync()
    {
        if (_serviceProvider is null)
            return;

        Log.Information("Deferred startup pipeline started");

        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<VeyraDbContext>();
            var userProfiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            var tokenPolicy = scope.ServiceProvider.GetRequiredService<IAccessTokenPolicyService>();
            var setup = scope.ServiceProvider.GetRequiredService<ISetupRepository>();
            var syncOrchestrator = scope.ServiceProvider.GetService<IRepositoryCloudSyncOrchestrator>();
            var recovery = scope.ServiceProvider.GetService<IRepositoryRecoveryService>();
            string? activeUsername;
            var watchedDirectoryCount = 0;
            var trackedExtensionCount = 0;
            var localRepositoryCount = 0;
            var isMainShellActive = false;

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

            activeUsername = await db.Set<UserProfile>()
                .AsNoTracking()
                .Where(u => u.IsActive)
                .OrderByDescending(u => u.LastLoginAt)
                .Select(u => u.Username)
                .FirstOrDefaultAsync();
            watchedDirectoryCount = await db.Set<WatchedDirectory>()
                .AsNoTracking()
                .CountAsync(x => !x.IsDeleted && x.IsEnabled);
            trackedExtensionCount = await db.Set<D_WatchedFormat>()
                .AsNoTracking()
                .CountAsync(x => !x.IsDeleted && x.IsEnabled);
            localRepositoryCount = await db.Set<Repository>()
                .AsNoTracking()
                .CountAsync(x => !x.IsDeleted);

            var hasLocalBootstrap = localRepositoryCount > 0 || (watchedDirectoryCount > 0 && trackedExtensionCount > 0);

            if (hasLocalBootstrap)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        var nav = _serviceProvider.GetRequiredService<INavigationService>();
                        nav.GoToMain();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Deferred startup failed to switch shell to main window after local bootstrap.");
                    }
                });

                isMainShellActive = true;
            }

            Log.Information(
                "Deferred startup resolved local bootstrap state. ActiveUser {Username}. LocalRepositories {LocalRepositories}. WatchedDirectories {WatchedDirectories}. TrackedExtensions {TrackedExtensions}. Target {Target}",
                activeUsername ?? "(none)",
                localRepositoryCount,
                watchedDirectoryCount,
                trackedExtensionCount,
                isMainShellActive ? "main" : "welcome");

            if (!string.IsNullOrWhiteSpace(activeUsername))
            {
                var profile = await userProfiles.GetActiveProfileAsync();
                var tokenState = tokenPolicy.Evaluate(profile?.AccessToken);
                var canUseCloudSync = tokenState.CanUseForSync;

                if (!canUseCloudSync)
                {
                    Log.Information(
                        "Skipping deferred cloud startup sync for user {Username}. TokenState {TokenState}. Reason {Reason}",
                        activeUsername,
                        tokenState.State,
                        tokenState.Description);
                }
                else
                {
                    var dirs = await setup.GetWatchedDirectoriesAsync();
                    var exts = await setup.GetTrackedExtensionsAsync();

                    if (dirs.Count == 0 || exts.Count == 0)
                    {
                        try
                        {
                            var restored = await (syncOrchestrator?.RestoreRepositoriesFromCloudAsync() ?? Task.FromResult(0));
                            Log.Information(
                                "Deferred cloud restore finished. User {Username}. Restored {Restored}",
                                activeUsername,
                                restored);

                            dirs = await setup.GetWatchedDirectoriesAsync();
                            exts = await setup.GetTrackedExtensionsAsync();

                            if (!isMainShellActive && dirs.Count > 0 && exts.Count > 0)
                            {
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    try
                                    {
                                        var nav = _serviceProvider.GetRequiredService<INavigationService>();
                                        nav.GoToMain();
                                        Log.Information(
                                            "Deferred startup switched shell to main window after cloud restore. WatchedDirectories {WatchedDirectories}. TrackedExtensions {TrackedExtensions}",
                                            dirs.Count,
                                            exts.Count);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Warning(ex, "Deferred startup failed to switch shell to main window");
                                    }
                                });

                                isMainShellActive = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Deferred cloud restore failed at startup for user {Username}", activeUsername);
                        }
                    }

                    try
                    {
                        if (syncOrchestrator is not null)
                            await syncOrchestrator.ProcessPendingQueueAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Deferred cloud pending queue resume failed at startup for user {Username}", activeUsername);
                    }
                }
            }

            if (recovery is not null)
            {
                try
                {
                    var recoveryResults = await recovery.RunStartupHealthCheckAsync();
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

            _snapshotScheduler = _serviceProvider.GetService<ISnapshotScheduler>();
            _snapshotScheduler?.Start();
            Log.Information("Deferred startup pipeline completed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deferred startup pipeline failed");
        }
    }
}
