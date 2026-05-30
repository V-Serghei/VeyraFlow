using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.DTOs;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs.Auth;
using Veyra.Desktop.Services.Connectivity;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Scheduling;
using Veyra.Desktop.Services.Shell.Tray;
using Veyra.Desktop.Styling;
using Veyra.Desktop.ViewModels.Windows;
using AvaloniaApplication = Avalonia.Application;
using DependencyInjection = Veyra.Desktop.CompositionRoot.DependencyInjection;

namespace Veyra.Desktop;

public partial class App : AvaloniaApplication
{
    public static IServiceProvider? _serviceProvider { get; private set; } = null!;
    private static ISnapshotScheduler? _snapshotScheduler;
    private static Task? _startupBackgroundTask;
    private static bool _databaseInitialized;
    private IAppTrayService? _trayService;

    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;

        DataTemplates.Add(new ViewLocator());
        Styles.Add(new FluentAvaloniaTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Veyra.Desktop/"))
        {
            Source = new Uri("avares://Veyra.Desktop/Styles/Controls.axaml")
        });
    }

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
        EnsureDatabaseReadyForShell();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime classicDesktop)
        {
            classicDesktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            classicDesktop.Exit += OnDesktopExit;

            EnsureTrayIconRegistered();
            _trayService = _serviceProvider.GetService<IAppTrayService>();
            var trayIcon = TrayIcon.GetIcons(this)?.FirstOrDefault();
            if (_trayService is not null && trayIcon is not null)
                _trayService.Initialize(trayIcon);

            var nav = _serviceProvider.GetRequiredService<INavigationService>();
            nav.ShowWelcome();

            Log.Information(
                "Initial shell displayed immediately using welcome window while deferred startup resolves user state.");
        }

        _startupBackgroundTask = Task.Run(RunDeferredStartupAsync);

        base.OnFrameworkInitializationCompleted();
    }

    private void EnsureTrayIconRegistered()
    {
        var existingIcons = TrayIcon.GetIcons(this);
        if (existingIcons?.Any() == true)
            return;

        try
        {
            var trayIcon = new TrayIcon
            {
                ToolTipText = "VeyraFlow",
                IsVisible = false,
                Menu = new NativeMenu()
            };

            try
            {
                var iconUri = new Uri("avares://Veyra.Desktop/Assets/veyraFlowLogo.ico");
                using var stream = AssetLoader.Open(iconUri);
                var icon = CreateWindowIcon(stream);
                if (icon is not null)
                    trayIcon.Icon = icon;
            }
            catch
            {
            }

            var trayIcons = new TrayIcons();
            trayIcons.Add(trayIcon);
            TrayIcon.SetIcons(this, trayIcons);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register tray icon resources programmatically.");
        }
    }

    private static WindowIcon? CreateWindowIcon(Stream stream)
    {
        var streamCtor = typeof(WindowIcon).GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, [typeof(Stream)], null);
        if (streamCtor is not null)
            return streamCtor.Invoke([stream]) as WindowIcon;

        var bytes = ReadAllBytes(stream);
        var stringCtor = typeof(WindowIcon).GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, [typeof(string)], null);
        if (stringCtor is null)
            return null;

        var tempIconPath = Path.Combine(Path.GetTempPath(), "VeyraFlow", "tray-icon.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(tempIconPath)!);
        File.WriteAllBytes(tempIconPath, bytes);
        return stringCtor.Invoke([tempIconPath]) as WindowIcon;
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream is MemoryStream memoryStream)
            return memoryStream.ToArray();

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            _trayService?.PrepareForShutdown();

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
            var databaseStartup = scope.ServiceProvider.GetRequiredService<IDatabaseStartupService>();
            var userProfiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            var tokenPolicy = scope.ServiceProvider.GetRequiredService<IAccessTokenPolicyService>();
            var setup = scope.ServiceProvider.GetRequiredService<ISetupRepository>();
            var repositories = scope.ServiceProvider.GetRequiredService<IRepositoryRepository>();
            var nativeRuntime = scope.ServiceProvider.GetRequiredService<INativeRuntimeHealthService>();
            var syncOrchestrator = scope.ServiceProvider.GetService<IRepositoryCloudSyncOrchestrator>();
            var recovery = scope.ServiceProvider.GetService<IRepositoryRecoveryService>();
            string? activeUsername;
            var watchedDirectoryCount = 0;
            var trackedExtensionCount = 0;
            var localRepositoryCount = 0;
            var isMainShellActive = false;

            if (!_databaseInitialized)
            {
                databaseStartup.Initialize();
                _databaseInitialized = true;
            }

            var nativeHealth = nativeRuntime.Probe();
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

            activeUsername = await userProfiles.GetActiveUsernameAsync();

            var connectivityService = _serviceProvider.GetRequiredService<IConnectivityStatusService>();
            connectivityService.SetCloudProbeEnabled(!string.IsNullOrWhiteSpace(activeUsername));
            watchedDirectoryCount = (await setup.GetWatchedDirectoriesAsync()).Count;
            trackedExtensionCount = (await setup.GetTrackedExtensionsAsync()).Count;
            localRepositoryCount = (await repositories.GetAllRepositoriesAsync()).Count;

            var hasLocalBootstrap = localRepositoryCount > 0 || (watchedDirectoryCount > 0 && trackedExtensionCount > 0);

            // Validate cloud token against server BEFORE deciding whether to show main window.
            // needsReAuth = true only when the user had a cloud account and the server explicitly
            // rejected the refresh (401). Network errors are treated as offline — no forced logout.
            var canUseCloudSync = false;
            var needsReAuth = false;
            AccessTokenPolicyEvaluationDto? tokenState = null;

            if (!string.IsNullOrWhiteSpace(activeUsername))
            {
                var profile = await userProfiles.GetActiveProfileAsync();
                tokenState = tokenPolicy.Evaluate(profile?.AccessToken);
                canUseCloudSync = tokenState.CanUseForSync;

                if (profile?.RefreshToken is { Length: > 0 } refreshToken)
                {
                    // Always attempt refresh when a refresh token exists — even if the access token
                    // is already locally expired. The access token may have been issued by a different
                    // server instance (e.g. switched from localhost to Railway), so local expiry
                    // checks are not sufficient.
                    var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
                    try
                    {
                        var refreshed = await authService.RefreshAsync(refreshToken);
                        if (refreshed is not null)
                        {
                            await userProfiles.SaveOrUpdateProfileAsync(
                                refreshed.Username,
                                refreshed.CloudUserId,
                                refreshed.AccessToken,
                                refreshed.Email,
                                refreshed.CloudSessionId,
                                refreshed.RefreshToken,
                                refreshed.AccessTokenExpiresAtUtc,
                                refreshed.RefreshTokenExpiresAtUtc);
                            canUseCloudSync = true;
                            Log.Information("Startup token refresh succeeded. User {Username}", activeUsername);
                        }
                        else
                        {
                            canUseCloudSync = false;
                        }
                    }
                    catch (CloudAuthRefreshRejectedException ex)
                    {
                        Log.Warning(ex, "Startup token refresh rejected by server. Signing out user {Username}", activeUsername);
                        canUseCloudSync = false;
                        needsReAuth = true;
                        await userProfiles.SignOutActiveAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Startup token refresh failed (network or server unavailable). Cloud sync skipped. User {Username}", activeUsername);
                        canUseCloudSync = false;
                    }
                }
                else if (!canUseCloudSync)
                {
                    // No refresh token and access token is also invalid — profile exists but all
                    // credentials are gone (e.g. after manual DB clear or server switch).
                    // Force re-auth so the user can log in again.
                    Log.Warning("No usable tokens found for user {Username}. Requiring re-authentication.", activeUsername);
                    needsReAuth = true;
                    await userProfiles.SignOutActiveAsync();
                }
            }

            // No active cloud profile but has local repos: check if they ever had a cloud account.
            // If yes (profile record exists but was signed out) → require re-auth.
            // If no history at all (pure local/guest user) → open main window silently.
            if (!needsReAuth && string.IsNullOrWhiteSpace(activeUsername) && hasLocalBootstrap)
            {
                var allProfiles = await userProfiles.GetProfilesAsync();
                if (allProfiles.Count > 0)
                {
                    Log.Information("Local repos found but cloud profile is signed out. Requiring re-authentication.");
                    needsReAuth = true;
                }
            }

            // Re-auth: navigate the existing WelcomeWindow directly to login (skip onboarding intro).
            // Pure guest-mode users (no cloud history, no active profile) are not affected.
            if (needsReAuth)
            {
                Log.Information("Startup re-authentication required. Navigating to login. User {Username}", activeUsername);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        var windows = _serviceProvider.GetRequiredService<IWindowService>();
                        if (windows.GetActiveWindow()?.DataContext is WelcomeWindowViewModel welcomeVm)
                            welcomeVm.NavigateToLoginDirectly();
                        else
                            _serviceProvider.GetRequiredService<INavigationService>().ShowLogin();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to navigate to login after startup token rejection");
                    }
                });
                // WelcomeWindow handles navigation to main after successful login — don't GoToMain here.
            }
            else if (hasLocalBootstrap)
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
                needsReAuth ? "login" : isMainShellActive ? "main" : "welcome");

            if (!string.IsNullOrWhiteSpace(activeUsername) && !needsReAuth)
            {
                if (!canUseCloudSync)
                {
                    Log.Information(
                        "Skipping deferred cloud startup sync for user {Username}. TokenState {TokenState}. Reason {Reason}",
                        activeUsername,
                        tokenState?.State,
                        tokenState?.Description);
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

    private static void EnsureDatabaseReadyForShell()
    {
        if (_serviceProvider is null || _databaseInitialized)
            return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var databaseStartup = scope.ServiceProvider.GetRequiredService<IDatabaseStartupService>();
            databaseStartup.Initialize();
            _databaseInitialized = true;
            Log.Information("Database schema is ready before shell initialization.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to prepare database schema before shell initialization. Deferred startup will retry.");
        }
    }
}
