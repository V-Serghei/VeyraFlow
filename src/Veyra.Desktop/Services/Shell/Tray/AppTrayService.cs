using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Application.Queries;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Execution;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;
using AvaloniaApplication = Avalonia.Application;

namespace Veyra.Desktop.Services.Shell.Tray;

public sealed class AppTrayService(
    INavigationService navigation,
    IServiceScopeExecutor scopeExecutor,
    ILogger<AppTrayService> log)
    : IAppTrayService
{
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private TrayIcon? _trayIcon;
    private NativeMenu? _trayMenu;
    private string _lastStatusText = Loc.T("tray.status.ready");
    private bool _languageSubscribed;

    public bool IsInitialized => _trayIcon is not null;
    public bool IsExitRequested { get; private set; }

    public void Initialize(TrayIcon trayIcon)
    {
        ArgumentNullException.ThrowIfNull(trayIcon);

        if (_trayIcon is not null)
            return;

        _trayIcon = trayIcon;
        _trayMenu = trayIcon.Menu ?? new NativeMenu();
        _trayMenu.NeedsUpdate += OnTrayMenuNeedsUpdate;
        _trayIcon.Menu = _trayMenu;
        _trayIcon.Clicked += OnTrayIconClicked;
        _trayIcon.IsVisible = true;
        _trayIcon.ToolTipText = BuildTrayToolTip();

        if (!_languageSubscribed)
        {
            _localization.LanguageChanged += OnLanguageChanged;
            _languageSubscribed = true;
        }

        _ = RefreshMenuAsync();
        log.LogInformation("Application tray icon initialized");
    }

    public void PrepareForShutdown()
    {
        IsExitRequested = true;

        if (_trayIcon is not null)
            _trayIcon.IsVisible = false;
    }

    public void HandleMainWindowClosing(Window window, WindowClosingEventArgs e)
    {
        if (!ShouldHideToTray(e))
            return;

        e.Cancel = true;
        window.Hide();
        SetLastStatus(Loc.T("tray.status.background"));
        log.LogInformation("Main window was hidden to tray instead of closing");
    }

    private bool ShouldHideToTray(WindowClosingEventArgs e)
    {
        if (IsExitRequested || _trayIcon is null || !_trayIcon.IsVisible)
            return false;

        return e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;
    }

    private async void OnTrayMenuNeedsUpdate(object? sender, EventArgs e)
    {
        await RefreshMenuAsync();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        _ = RestoreMainWindowAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _lastStatusText = Loc.T("tray.status.ready");
        _ = RefreshMenuAsync();
    }

    private async Task RefreshMenuAsync()
    {
        IReadOnlyList<RepositoryDto> repositories = Array.Empty<RepositoryDto>();

        try
        {
            repositories = await scopeExecutor.ExecuteAsync<IMediator, IReadOnlyList<RepositoryDto>>(
                (mediator, ct) => mediator.Send(new GetAllRepositoriesQuery(), ct));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to refresh tray repository list");
            SetLastStatus(Loc.T("tray.status.menu_refresh_failed"));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_trayMenu is null)
                return;

            _trayMenu.Items.Clear();

            _trayMenu.Items.Add(new NativeMenuItem
            {
                Header = _lastStatusText,
                IsEnabled = false
            });
            _trayMenu.Items.Add(new NativeMenuItemSeparator());
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.open_app"), RestoreMainWindowAsync));
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.open_dashboard"), ShowDashboardAsync));
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.open_search"), ShowSearchAsync));
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.open_settings"), ShowSettingsAsync));
            _trayMenu.Items.Add(CreateSnapshotMenu(repositories));
            _trayMenu.Items.Add(new NativeMenuItemSeparator());
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.exit"), ExitApplicationAsync));

            if (_trayIcon is not null)
                _trayIcon.ToolTipText = BuildTrayToolTip();
        });
    }

    private NativeMenuItem CreateSnapshotMenu(IReadOnlyList<RepositoryDto> repositories)
    {
        var menu = new NativeMenu();
        foreach (var repository in repositories
                     .Where(static repo => !repo.IsDeleted)
                     .OrderBy(static repo => repo.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var repositoryId = repository.Id;
            var repositoryName = repository.Name;
            menu.Items.Add(CreateMenuItem(repositoryName, () => CreateSnapshotForRepositoryAsync(repositoryId, repositoryName)));
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem
            {
                Header = Loc.T("tray.menu.no_repositories"),
                IsEnabled = false
            });
        }

        return new NativeMenuItem
        {
            Header = Loc.T("tray.menu.create_snapshot"),
            Menu = menu
        };
    }

    private NativeMenuItem CreateMenuItem(string header, Func<Task> onClick)
    {
        var item = new NativeMenuItem
        {
            Header = header
        };

        item.Click += async (_, _) => await onClick();
        return item;
    }

    private string BuildTrayToolTip()
        => Loc.T("tray.tooltip");

    private void SetLastStatus(string status)
    {
        _lastStatusText = status;

        if (_trayIcon is not null)
            _trayIcon.ToolTipText = BuildTrayToolTip();
    }

    private async Task RestoreMainWindowAsync()
    {
        var window = await EnsureMainWindowAsync();
        if (window is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!window.IsVisible)
                window.Show();

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Activate();
        });

        SetLastStatus(Loc.T("tray.status.window_restored"));
    }

    private async Task ShowDashboardAsync()
    {
        var window = await EnsureMainWindowAsync();
        if (window?.DataContext is not MainWindowViewModel vm)
        {
            await RestoreMainWindowAsync();
            return;
        }

        await RestoreMainWindowAsync();
        await vm.ShowDashboardPageAsync();
        SetLastStatus(Loc.T("tray.status.dashboard_opened"));
    }

    private async Task ShowSearchAsync()
    {
        var window = await EnsureMainWindowAsync();
        if (window?.DataContext is not MainWindowViewModel vm)
        {
            await RestoreMainWindowAsync();
            return;
        }

        await RestoreMainWindowAsync();
        await vm.ShowSearchPageAsync();
        SetLastStatus(Loc.T("tray.status.search_opened"));
    }

    private async Task ShowSettingsAsync()
    {
        var window = await EnsureMainWindowAsync();
        if (window?.DataContext is not MainWindowViewModel vm)
        {
            await RestoreMainWindowAsync();
            return;
        }

        await RestoreMainWindowAsync();
        await vm.ShowAppSettingsPageAsync();
        SetLastStatus(Loc.T("tray.status.settings_opened"));
    }

    private async Task CreateSnapshotForRepositoryAsync(int repositoryId, string repositoryName)
    {
        if (!await _snapshotGate.WaitAsync(0))
        {
            SetLastStatus(Loc.T("tray.status.busy"));
            return;
        }

        try
        {
            SetLastStatus(Loc.F("tray.status.snapshot_running", repositoryName));

            var result = await scopeExecutor.ExecuteAsync<IMediator, Veyra.Application.Common.Results.OperationResult<RepositoryScanResultDto>>(
                (mediator, ct) => mediator.Send(
                    new ScanRepositoryCommand(
                        repositoryId,
                        Progress: null,
                        Options: new RepositoryScanOptionsDto(
                            SaveFileVersions: true,
                            TriggerOverride: "manual_snapshot")),
                    ct));

            if (!result.Success)
            {
                var message = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "common.error_generic");
                SetLastStatus(Loc.F("tray.status.snapshot_failed_with_reason", repositoryName, message));
                return;
            }

            if (result.Value?.SnapshotCreated == true)
            {
                SetLastStatus(Loc.F("tray.status.snapshot_created", repositoryName));
                return;
            }

            if (result.Value?.NoChangesDetected == true)
            {
                SetLastStatus(Loc.F("tray.status.snapshot_no_changes", repositoryName));
                return;
            }

            SetLastStatus(Loc.F("tray.status.snapshot_completed_without_version", repositoryName));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Tray snapshot failed for repository {RepositoryId}", repositoryId);
            SetLastStatus(Loc.F("tray.status.snapshot_failed", repositoryName));
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private async Task<MainWindow?> EnsureMainWindowAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (AvaloniaApplication.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return null;

            if (desktop.MainWindow is MainWindow mainWindow)
                return mainWindow;

            var existingMain = desktop.Windows.OfType<MainWindow>().FirstOrDefault();
            if (existingMain is not null)
            {
                desktop.MainWindow = existingMain;
                return existingMain;
            }

            navigation.GoToMain();
            return desktop.MainWindow as MainWindow;
        });
    }

    private async Task ExitApplicationAsync()
    {
        PrepareForShutdown();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (AvaloniaApplication.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });
    }
}
