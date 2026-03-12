using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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

        var shouldOpenMain = false;
        string? activeUsername = null;
        int watchedDirectoryCount = 0;
        int trackedExtensionCount = 0;

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
            var setup = scope.ServiceProvider.GetRequiredService<ISetupRepository>();

            activeUsername = userProfiles.GetActiveUsernameAsync().GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(activeUsername))
            {
                var dirs = setup.GetWatchedDirectoriesAsync().GetAwaiter().GetResult();
                var exts = setup.GetTrackedExtensionsAsync().GetAwaiter().GetResult();
                watchedDirectoryCount = dirs.Count;
                trackedExtensionCount = exts.Count;
                shouldOpenMain = dirs.Count > 0 && exts.Count > 0;
            }
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime classicDesktop)
        {
            classicDesktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            classicDesktop.Exit += OnDesktopExit;

            var nav = _serviceProvider.GetRequiredService<INavigationService>();

            if (shouldOpenMain)
                nav.GoToMain();
            else
                nav.ShowWelcome();

            Log.Information(
                "Initial shell displayed. ActiveUser {Username}. WatchedDirectories {WatchedDirectories}. TrackedExtensions {TrackedExtensions}. Target {Target}",
                activeUsername ?? "(none)",
                watchedDirectoryCount,
                trackedExtensionCount,
                shouldOpenMain ? "main" : "welcome");
        }

        _startupBackgroundTask = Task.Run(() => RunDeferredStartupAsync(activeUsername, shouldOpenMain));

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            if (_startupBackgroundTask is { IsCompleted: false })
            {
                if (!_startupBackgroundTask.Wait(TimeSpan.FromSeconds(2)))
                    Log.Warning("Deferred startup task is still running during shutdown; continuing shutdown.");
            }
            else
            {
                _startupBackgroundTask?.GetAwaiter().GetResult();
            }

            _snapshotScheduler?.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        Log.Information("Application shutting down");
        Log.CloseAndFlush();
    }

    private static async Task RunDeferredStartupAsync(string? activeUsername, bool initialShouldOpenMain)
    {
        if (_serviceProvider is null)
            return;

        Log.Information("Deferred startup pipeline started");

        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var userProfiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            var tokenPolicy = scope.ServiceProvider.GetRequiredService<IAccessTokenPolicyService>();
            var setup = scope.ServiceProvider.GetRequiredService<ISetupRepository>();
            var syncOrchestrator = scope.ServiceProvider.GetService<IRepositoryCloudSyncOrchestrator>();
            var recovery = scope.ServiceProvider.GetService<IRepositoryRecoveryService>();

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

                            if (!initialShouldOpenMain && dirs.Count > 0 && exts.Count > 0)
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
