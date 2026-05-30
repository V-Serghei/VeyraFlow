using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Cloud;
using Veyra.Application.DTOs.Repository.Core;
using Veyra.Application.DTOs.Repository.Scanning;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Connectivity;
using Veyra.Desktop.Services.Connectivity.Models;
using Veyra.Desktop.Services.Execution;
using Veyra.Desktop.Services.Monitoring;
using Veyra.Desktop.Services.Monitoring.Models;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Security;
using Veyra.Desktop.ViewModels.Pages.Settings;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;
using Veyra.Desktop.Views.Windows;
using Veyra.Desktop.Services.Repositories;
using Veyra.Desktop.Services.Sync.Runtime;
using AvaloniaApplication = Avalonia.Application;

namespace Veyra.Desktop.Services.Shell.Tray;

public sealed class AppTrayService(
    INavigationService navigation,
    IWindowService windows,
    IServiceScopeExecutor scopeExecutor,
    IConnectivityStatusService connectivity,
    ICloudSyncRuntimeControlService cloudSyncRuntime,
    IProcessResourceStatusStore processResourceStatusStore,
    IRepositoryLiveSyncStatusStore liveSyncStatusStore,
    ILogger<AppTrayService> log)
    : IAppTrayService
{
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private TrayIcon? _trayIcon;
    private NativeMenu? _trayMenu;
    private string _lastStatusText = Loc.T("tray.status.ready");
    private DateTime _lastStatusUpdatedAtUtc = DateTime.UtcNow;
    private bool _languageSubscribed;
    private bool _hasCloudAccess;
    private IReadOnlyList<RepositoryDto> _lastRepositories = Array.Empty<RepositoryDto>();
    private TrayPanelWindow? _trayPanelWindow;
    private TrayPanelWindowViewModel? _trayPanelViewModel;
    private int? _selectedTrayRepositoryId;
    private readonly List<(string Text, DateTime TimestampUtc)> _recentStatuses = [];
    private string? _currentActivityText;
    private string? _currentActivityKind;
    private int? _currentActivityRepositoryId;
    private string _currentActivityAccentColor = "#6EA8FF";
    private string _currentActivityBackgroundColor = "#1A6EA8FF";
    private ProcessResourceSnapshotDto? _processResourceSnapshot = processResourceStatusStore.Snapshot;

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
        RememberStatus(_lastStatusText, _lastStatusUpdatedAtUtc);

        if (!_languageSubscribed)
        {
            _localization.LanguageChanged += OnLanguageChanged;
            _languageSubscribed = true;
        }

        connectivity.StatusChanged += OnConnectivityStatusChanged;
        cloudSyncRuntime.StateChanged += OnCloudSyncRuntimeStateChanged;
        processResourceStatusStore.StatusChanged += OnProcessResourceStatusChanged;
        liveSyncStatusStore.StatusChanged += OnLiveSyncStatusChanged;

        _ = RefreshMenuAsync();
        log.LogInformation("Application tray icon initialized");
    }

    public void PrepareForShutdown()
    {
        IsExitRequested = true;

        if (Dispatcher.UIThread.CheckAccess())
            CloseTrayPanel();
        else
            Dispatcher.UIThread.Post(CloseTrayPanel);

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
        _ = ToggleTrayPanelAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _lastStatusText = Loc.T("tray.status.ready");
        _ = RefreshMenuAsync();
    }

    private void OnConnectivityStatusChanged(object? sender, EventArgs e)
    {
        _ = RefreshMenuAsync();
    }

    private void OnCloudSyncRuntimeStateChanged(CloudSyncRuntimeSnapshot snapshot)
    {
        _ = RefreshMenuAsync();
    }

    private void OnProcessResourceStatusChanged(object? sender, ProcessResourceStatusChangedEventArgs e)
    {
        _processResourceSnapshot = e.Snapshot;

        if (Dispatcher.UIThread.CheckAccess())
            UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories));
        else
            Dispatcher.UIThread.Post(() => UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories)));
    }

    private void OnLiveSyncStatusChanged(object? sender, RepositoryLiveSyncStatusChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories));
        else
            Dispatcher.UIThread.Post(() => UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories)));
    }

    private async Task RefreshMenuAsync()
    {
        var repositories = _lastRepositories;
        var hasCloudAccess = _hasCloudAccess;

        try
        {
            var repositoriesTask = scopeExecutor.ExecuteAsync<IMediator, IReadOnlyList<RepositoryDto>>(
                (mediator, ct) => mediator.Send(new GetAllRepositoriesQuery(), ct));
            var hasCloudAccessTask = scopeExecutor.ExecuteAsync<IUserProfileRepository, bool>(
                async (profiles, ct) => await profiles.GetActiveProfileAsync() is not null);

            await Task.WhenAll(repositoriesTask, hasCloudAccessTask);
            repositories = repositoriesTask.Result;
            hasCloudAccess = hasCloudAccessTask.Result;
            _lastRepositories = repositories;
            _hasCloudAccess = hasCloudAccess;
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

            var snapshotRepositories = GetSnapshotRepositories(repositories);
            _trayMenu.Items.Clear();

            _trayMenu.Items.Add(new NativeMenuItem
            {
                Header = _lastStatusText,
                IsEnabled = false
            });
            _trayMenu.Items.Add(new NativeMenuItem
            {
                Header = BuildConnectivityStatusText(),
                IsEnabled = false
            });
            _trayMenu.Items.Add(new NativeMenuItemSeparator());
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.open_app"), RestoreMainWindowAsync));
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.open_dashboard"), ShowDashboardAsync));
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.open_search"), ShowSearchAsync));
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.open_settings"), ShowSettingsAsync));
            _trayMenu.Items.Add(CreateSnapshotMenu(snapshotRepositories));
            _trayMenu.Items.Add(CreateCloudMenu());
            _trayMenu.Items.Add(new NativeMenuItemSeparator());
            _trayMenu.Items.Add(CreateMenuItem(Loc.T("tray.menu.exit"), ExitApplicationAsync));

            UpdateTrayPanelState(snapshotRepositories);

            if (_trayIcon is not null)
                _trayIcon.ToolTipText = BuildTrayToolTip();
        });
    }

    private NativeMenuItem CreateSnapshotMenu(IReadOnlyList<RepositoryDto> repositories)
    {
        var menu = new NativeMenu();
        foreach (var repository in repositories)
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

    private NativeMenuItem CreateCloudMenu()
    {
        var menu = new NativeMenu();
        var cloudActionsEnabled = CanRunCloudActions(out var disabledReason);

        menu.Items.Add(CreateMenuItem(
            cloudSyncRuntime.IsPaused
                ? Loc.T("tray.menu.cloud_resume")
                : Loc.T("tray.menu.cloud_pause"),
            ToggleCloudSyncPauseAsync,
            _hasCloudAccess));
        menu.Items.Add(new NativeMenuItemSeparator());

        menu.Items.Add(CreateMenuItem(
            Loc.T("tray.menu.cloud_push_all"),
            PushAllRepositoriesToCloudAsync,
            cloudActionsEnabled));
        menu.Items.Add(CreateMenuItem(
            Loc.T("tray.menu.cloud_process_queue"),
            ProcessCloudQueueAsync,
            cloudActionsEnabled));

        if (!cloudActionsEnabled)
        {
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(new NativeMenuItem
            {
                Header = disabledReason ?? Loc.T("tray.menu.cloud_disabled_reason_local"),
                IsEnabled = false
            });
        }

        return new NativeMenuItem
        {
            Header = Loc.T("tray.menu.cloud_actions"),
            Menu = menu
        };
    }

    private NativeMenuItem CreateMenuItem(string header, Func<Task> onClick, bool isEnabled = true)
    {
        var item = new NativeMenuItem
        {
            Header = header,
            IsEnabled = isEnabled
        };

        if (isEnabled)
            item.Click += async (_, _) => await onClick();

        return item;
    }

    private async Task ToggleTrayPanelAsync()
    {
        var wasOpen = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_trayPanelWindow is not { IsVisible: true })
                return false;

            CloseTrayPanel();
            return true;
        });

        if (wasOpen)
            return;

        await ShowTrayPanelAsync();
    }

    private async Task ShowTrayPanelAsync()
    {
        await RefreshMenuAsync();

        var owner = windows.GetActiveWindow() ?? await EnsureMainWindowAsync();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_trayPanelWindow is null)
            {
                var panel = windows.Create<TrayPanelWindow>();
                if (panel.DataContext is not TrayPanelWindowViewModel vm)
                    return;

                vm.ConfigureActions(
                    RestoreMainWindowAsync,
                    ShowDashboardAsync,
                    ShowSearchAsync,
                    ShowSettingsAsync,
                    OpenSyncHealthCenterFromTrayAsync,
                    OpenRepositorySettingsFromTrayAsync,
                    RetryIssueFromTrayAsync,
                    CancelIssueFromTrayAsync,
                    CreateSnapshotFromTraySelectionAsync,
                    ToggleCloudSyncPauseAsync,
                    ProcessCloudQueueAsync,
                    PushAllRepositoriesToCloudAsync,
                    ExitApplicationAsync);
                vm.SelectedRepositoryChanged += OnTrayPanelSelectedRepositoryChanged;

                panel.Closed += OnTrayPanelClosed;
                _trayPanelWindow = panel;
                _trayPanelViewModel = vm;
            }

            UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories));

            if (_trayPanelWindow is null)
                return;

            if (!_trayPanelWindow.IsVisible)
                windows.Show(_trayPanelWindow);

            _trayPanelWindow.PositionNearTray(owner);
            _trayPanelWindow.Activate();
        });
    }

    private void UpdateTrayPanelState(IReadOnlyList<RepositoryDto> repositories)
    {
        if (_trayPanelViewModel is null)
            return;

        if (_selectedTrayRepositoryId is not null && repositories.All(repository => repository.Id != _selectedTrayRepositoryId))
            _selectedTrayRepositoryId = null;

        var (accentColor, backgroundColor) = ResolveTrayPanelConnectivityColors();
        var canRunCloudActions = CanRunCloudActions(out var disabledReason);
        var cloudHint = canRunCloudActions ? null : disabledReason;
        var repositoryOptions = repositories
            .Select(repository => new TrayPanelRepositoryOptionViewModel(repository.Id, repository.Name))
            .ToArray();

        var currentActivity = ResolveCurrentActivity(repositories);

        _trayPanelViewModel.ApplyState(
            _lastStatusText,
            BuildConnectivityStatusText(),
            accentColor,
            backgroundColor,
            cloudSyncRuntime.IsPaused
                ? Loc.T("tray.menu.cloud_resume")
                : Loc.T("tray.menu.cloud_pause"),
            _hasCloudAccess,
            canRunCloudActions,
            cloudHint,
            repositoryOptions,
            _selectedTrayRepositoryId,
            FormatRelativeTime(_lastStatusUpdatedAtUtc),
            currentActivity.Text,
            currentActivity.AccentColor,
            currentActivity.BackgroundColor,
            FormatProcessLoadSummary(_processResourceSnapshot),
            FormatProcessLoadDetail(_processResourceSnapshot),
            ProcessResourceStatusPresenter.FormatPeakSummary(processResourceStatusStore.History),
            ProcessResourceStatusPresenter.FormatRecentHistory(processResourceStatusStore.History),
            DescribeProcessLoadVisuals(_processResourceSnapshot).AccentColor,
            DescribeProcessLoadVisuals(_processResourceSnapshot).BackgroundColor,
            BuildSelectedRepositoryState(repositories),
            BuildRecentActions(),
            BuildIssueItems(repositories));
    }

    private static IReadOnlyList<RepositoryDto> GetSnapshotRepositories(IReadOnlyList<RepositoryDto> repositories)
    {
        return repositories
            .Where(static repository => !repository.IsDeleted)
            .OrderBy(static repository => repository.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private (string AccentColor, string BackgroundColor) ResolveTrayPanelConnectivityColors()
    {
        if (!_hasCloudAccess)
            return ("#7CC7FF", "#1A7CC7FF");

        if (cloudSyncRuntime.IsPaused)
            return ("#FBBF24", "#1AFBBF24");

        return connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => ("#F59E0B", "#1AF59E0B"),
            ConnectivityState.CloudUnavailable => ("#F97316", "#1AF97316"),
            _ => ("#4ADE80", "#164ADE80")
        };
    }

    private string? FormatProcessLoadSummary(ProcessResourceSnapshotDto? snapshot)
    {
        if (snapshot is null)
            return null;

        return snapshot.CpuPercent.HasValue
            ? Loc.F(
                "tray.panel_process_summary",
                snapshot.CpuPercent.Value.ToString("0.#", CultureInfo.CurrentCulture),
                FormatSize(snapshot.WorkingSetBytes))
            : Loc.F("tray.panel_process_summary_cpu_pending", FormatSize(snapshot.WorkingSetBytes));
    }

    private string? FormatProcessLoadDetail(ProcessResourceSnapshotDto? snapshot)
    {
        if (snapshot is null)
            return null;

        return Loc.F(
            "tray.panel_process_detail",
            FormatSize(snapshot.PrivateMemoryBytes),
            FormatSize(snapshot.ManagedHeapBytes),
            snapshot.ThreadCount,
            snapshot.HandleCount,
            snapshot.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private static (string AccentColor, string BackgroundColor) DescribeProcessLoadVisuals(ProcessResourceSnapshotDto? snapshot)
    {
        if (snapshot is null)
            return ("#6EA8FF", "#1A6EA8FF");

        var cpu = snapshot.CpuPercent ?? 0;
        if (cpu >= 75 || snapshot.WorkingSetBytes >= 1024L * 1024 * 1024)
            return ("#F97316", "#1AF97316");

        if (cpu >= 40 || snapshot.WorkingSetBytes >= 700L * 1024 * 1024)
            return ("#F59E0B", "#1AF59E0B");

        return ("#4ADE80", "#164ADE80");
    }

    private async Task CreateSnapshotFromTraySelectionAsync(int? repositoryId)
    {
        var repository = GetSnapshotRepositories(_lastRepositories)
            .FirstOrDefault(item => item.Id == repositoryId)
            ?? GetSnapshotRepositories(_lastRepositories).FirstOrDefault();

        if (repository is null)
        {
            SetLastStatus(Loc.T("tray.menu.no_repositories"));
            await RefreshMenuAsync();
            return;
        }

        _selectedTrayRepositoryId = repository.Id;
        await CreateSnapshotForRepositoryAsync(repository.Id, repository.Name);
        await RefreshMenuAsync();
    }

    private void OnTrayPanelSelectedRepositoryChanged(int? repositoryId)
    {
        if (_selectedTrayRepositoryId == repositoryId)
            return;

        _selectedTrayRepositoryId = repositoryId;
        UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories));
    }

    private async Task OpenRepositorySettingsFromTrayAsync(int repositoryId)
    {
        var window = await EnsureMainWindowAsync();
        if (window?.DataContext is not MainWindowViewModel vm)
        {
            await RestoreMainWindowAsync();
            return;
        }

        await RestoreMainWindowAsync();
        await vm.ShowRepositorySettingsPageAsync(repositoryId);
        SetLastStatus(Loc.T("tray.status.settings_opened"));
    }

    private async Task OpenSyncHealthCenterFromTrayAsync()
    {
        var window = await EnsureMainWindowAsync();
        if (window?.DataContext is not MainWindowViewModel vm)
        {
            await RestoreMainWindowAsync();
            return;
        }

        await RestoreMainWindowAsync();
        await vm.ShowCloudSyncHealthCenterAsync();
        SetLastStatus(Loc.T("tray.status.sync_center_opened"));
    }

    private async Task RetryIssueFromTrayAsync(int repositoryId)
    {
        var issue = await ResolveSyncIssueForTrayAsync(repositoryId);
        if (issue is null)
        {
            SetLastStatus(Loc.T("tray.status.issue_no_longer_active"));
            return;
        }

        await RestoreMainWindowAsync();
        var window = await EnsureMainWindowAsync();
        if (window?.DataContext is not MainWindowViewModel vm)
            return;

        await vm.AppSettings.RetryRepositorySyncIssueDirectAsync(issue);
        SetLastStatus(string.IsNullOrWhiteSpace(vm.AppSettings.SyncMessage)
            ? Loc.F("tray.status.issue_retry_requested", issue.Name)
            : vm.AppSettings.SyncMessage);
    }

    private async Task CancelIssueFromTrayAsync(int repositoryId)
    {
        var issue = await ResolveSyncIssueForTrayAsync(repositoryId);
        if (issue is null)
        {
            SetLastStatus(Loc.T("tray.status.issue_no_longer_active"));
            return;
        }

        await RestoreMainWindowAsync();
        var window = await EnsureMainWindowAsync();
        if (window?.DataContext is not MainWindowViewModel vm)
            return;

        await vm.AppSettings.CancelRepositorySyncIssueDirectAsync(issue);
        SetLastStatus(string.IsNullOrWhiteSpace(vm.AppSettings.SyncMessage)
            ? Loc.F("tray.status.issue_cancel_requested", issue.Name)
            : vm.AppSettings.SyncMessage);
    }

    private void OnTrayPanelClosed(object? sender, EventArgs e)
    {
        if (sender is TrayPanelWindow panel)
            panel.Closed -= OnTrayPanelClosed;

        if (_trayPanelViewModel is not null)
            _trayPanelViewModel.SelectedRepositoryChanged -= OnTrayPanelSelectedRepositoryChanged;

        _trayPanelWindow = null;
        _trayPanelViewModel = null;
    }

    private void CloseTrayPanel()
    {
        if (_trayPanelWindow is null)
            return;

        _trayPanelWindow.Close();
    }

    private string BuildTrayToolTip()
    {
        var baseText = Loc.T("tray.tooltip");
        var connectivityText = BuildConnectivityStatusText();
        return string.Equals(baseText, connectivityText, StringComparison.Ordinal)
            ? baseText
            : $"{baseText}\n{connectivityText}";
    }

    private string BuildConnectivityStatusText()
    {
        if (!_hasCloudAccess)
            return Loc.T("tray.connectivity.local_mode");

        if (cloudSyncRuntime.IsPaused)
            return Loc.T("tray.connectivity.cloud_paused");

        return connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("tray.connectivity.internet_unavailable"),
            ConnectivityState.CloudUnavailable => Loc.T("tray.connectivity.cloud_unavailable"),
            _ => Loc.T("tray.connectivity.cloud_available")
        };
    }

    private bool CanRunCloudActions(out string? disabledReason)
    {
        if (!_hasCloudAccess)
        {
            disabledReason = Loc.T("tray.menu.cloud_disabled_reason_local");
            return false;
        }

        if (cloudSyncRuntime.IsPaused)
        {
            disabledReason = Loc.T("tray.menu.cloud_disabled_reason_paused");
            return false;
        }

        disabledReason = connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("tray.menu.cloud_disabled_reason_offline"),
            ConnectivityState.CloudUnavailable => Loc.T("tray.menu.cloud_disabled_reason_unavailable"),
            _ => null
        };

        return disabledReason is null;
    }

    private string? ResolveCloudConnectivityMessage()
    {
        if (!_hasCloudAccess)
            return Loc.T("tray.status.cloud_sign_in_required");

        if (cloudSyncRuntime.IsPaused)
            return Loc.T("ui_error.cloud_actions_paused");

        return connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("ui_error.internet_required"),
            ConnectivityState.CloudUnavailable => Loc.T("ui_error.cloud_temporarily_unavailable"),
            _ => null
        };
    }

    private async Task ToggleCloudSyncPauseAsync()
    {
        if (!_hasCloudAccess)
        {
            SetLastStatus(Loc.T("tray.status.cloud_sign_in_required"));
            return;
        }

        if (!await _operationGate.WaitAsync(0))
        {
            SetLastStatus(Loc.T("tray.status.busy"));
            return;
        }

        try
        {
            var paused = !cloudSyncRuntime.IsPaused;
            await cloudSyncRuntime.SetPausedAsync(paused);

            if (paused)
            {
                try
                {
                    await scopeExecutor.ExecuteAsync<IRepositoryCloudSyncOrchestrator>(
                        (sync, ct) => sync.ProcessPendingQueueAsync(ct));
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Cloud sync pause propagation encountered a non-fatal error");
                }
            }

            SetLastStatus(paused
                ? Loc.T("tray.status.cloud_paused")
                : Loc.T("tray.status.cloud_resumed"));
            await RefreshMenuAsync();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void SetLastStatus(string status)
    {
        _lastStatusText = status;
        _lastStatusUpdatedAtUtc = DateTime.UtcNow;
        RememberStatus(status, _lastStatusUpdatedAtUtc);

        if (_trayIcon is not null)
            _trayIcon.ToolTipText = BuildTrayToolTip();

        if (Dispatcher.UIThread.CheckAccess())
            UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories));
        else
            Dispatcher.UIThread.Post(() => UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories)));
    }

    private IReadOnlyList<TrayPanelRecentActionViewModel> BuildRecentActions()
    {
        return _recentStatuses
            .Select(item => new TrayPanelRecentActionViewModel(item.Text, FormatRelativeTime(item.TimestampUtc)))
            .ToArray();
    }

    private IReadOnlyList<TrayPanelIssueItemViewModel> BuildIssueItems(IReadOnlyList<RepositoryDto> repositories)
    {
        return repositories
            .Select(repository => new
            {
                Repository = repository,
                Issue = BuildIssueItem(repository)
            })
            .Where(item => item.Issue is not null)
            .OrderByDescending(item => GetIssuePriority(item.Repository.CloudSync))
            .ThenBy(item => item.Repository.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.Issue!)
            .Take(4)
            .ToArray();
    }

    private TrayPanelIssueItemViewModel? BuildIssueItem(RepositoryDto repository)
    {
        var cloudSync = repository.CloudSync;
        if (cloudSync is null || !_hasCloudAccess)
            return null;

        if ((cloudSync.FailedQueueCount + cloudSync.DeadLetterQueueCount) > 0 || !string.IsNullOrWhiteSpace(cloudSync.LastError))
        {
            return new TrayPanelIssueItemViewModel(
                repository.Id,
                repository.Name,
                Loc.F("tray.issue_failed", cloudSync.FailedQueueCount + cloudSync.DeadLetterQueueCount),
                "\uEA39",
                "#F97316",
                "failed",
                true,
                true);
        }

        if (cloudSync.ConflictQueueCount > 0)
        {
            return new TrayPanelIssueItemViewModel(
                repository.Id,
                repository.Name,
                Loc.F("tray.issue_conflicts", cloudSync.ConflictQueueCount),
                "\uEA39",
                "#F97316",
                "conflict",
                false,
                false);
        }

        if (cloudSync.RetryQueueCount > 0)
        {
            return new TrayPanelIssueItemViewModel(
                repository.Id,
                repository.Name,
                Loc.F("tray.issue_retries", cloudSync.RetryQueueCount),
                "\uE895",
                "#F59E0B",
                "retry",
                true,
                true);
        }

        return null;
    }

    private async Task<AppRepositorySyncIssueItemViewModel?> ResolveSyncIssueForTrayAsync(int repositoryId)
    {
        var window = await EnsureMainWindowAsync();
        if (window?.DataContext is not MainWindowViewModel vm)
            return null;

        await vm.AppSettings.RefreshSyncHealthAsync();
        return vm.AppSettings.RepositorySyncIssues.FirstOrDefault(issue => issue.RepositoryId == repositoryId);
    }

    private static int GetIssuePriority(RepositoryCloudSyncStatusDto? cloudSync)
    {
        if (cloudSync is null)
            return 0;

        if ((cloudSync.FailedQueueCount + cloudSync.DeadLetterQueueCount) > 0 || !string.IsNullOrWhiteSpace(cloudSync.LastError))
            return 3;

        if (cloudSync.ConflictQueueCount > 0)
            return 2;

        if (cloudSync.RetryQueueCount > 0)
            return 1;

        return 0;
    }

    private (string? Text, string AccentColor, string BackgroundColor) ResolveCurrentActivity(IReadOnlyList<RepositoryDto> repositories)
    {
        if (!string.IsNullOrWhiteSpace(_currentActivityText))
        {
            return (_currentActivityText, _currentActivityAccentColor, _currentActivityBackgroundColor);
        }

        var liveSyncActivity = repositories
            .Select(repository => new
            {
                Repository = repository,
                Status = liveSyncStatusStore.Get(repository.Id)
            })
            .Where(item => RepositoryLiveSyncStatusPresenter.IsBusy(item.Status))
            .OrderByDescending(item => RepositoryLiveSyncStatusPresenter.GetActivityPriority(item.Status))
            .ThenByDescending(item => item.Status?.QueueTotalCount ?? 0)
            .FirstOrDefault();

        if (liveSyncActivity?.Status is not null)
        {
            var visuals = RepositoryLiveSyncStatusPresenter.DescribeVisualState(liveSyncActivity.Status);
            return (
                RepositoryLiveSyncStatusPresenter.FormatTrayActivityText(liveSyncActivity.Repository.Name, liveSyncActivity.Status),
                visuals.AccentColor,
                visuals.BackgroundColor);
        }

        var activeUploadRepository = repositories.FirstOrDefault(repository =>
            (repository.CloudSync?.UploadProgressTotal ?? 0) > 0 ||
            (repository.CloudSync?.RunningQueueCount ?? 0) > 0);

        return activeUploadRepository is null
            ? (null, "#6EA8FF", "#1A6EA8FF")
            : (Loc.F("tray.activity_cloud_running", activeUploadRepository.Name), "#38BDF8", "#1638BDF8");
    }

    private void SetCurrentActivity(
        string? text,
        string accentColor = "#6EA8FF",
        string backgroundColor = "#1A6EA8FF",
        string? kind = null,
        int? repositoryId = null)
    {
        _currentActivityText = text;
        _currentActivityKind = text is null ? null : kind;
        _currentActivityRepositoryId = text is null ? null : repositoryId;
        _currentActivityAccentColor = accentColor;
        _currentActivityBackgroundColor = backgroundColor;

        if (Dispatcher.UIThread.CheckAccess())
            UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories));
        else
            Dispatcher.UIThread.Post(() => UpdateTrayPanelState(GetSnapshotRepositories(_lastRepositories)));
    }

    private void RememberStatus(string status, DateTime timestampUtc)
    {
        if (_recentStatuses.Count > 0 && string.Equals(_recentStatuses[0].Text, status, StringComparison.Ordinal))
        {
            _recentStatuses[0] = (status, timestampUtc);
            return;
        }

        _recentStatuses.Insert(0, (status, timestampUtc));

        if (_recentStatuses.Count > 3)
            _recentStatuses.RemoveRange(3, _recentStatuses.Count - 3);
    }

    private TrayPanelRepositoryState? BuildSelectedRepositoryState(IReadOnlyList<RepositoryDto> repositories)
    {
        var repository = repositories.FirstOrDefault(item => item.Id == _selectedTrayRepositoryId)
                         ?? repositories.FirstOrDefault();
        if (repository is null)
            return null;

        var cloudSync = repository.CloudSync;
        var metricsText = Loc.F(
            "tray.panel_repository_metrics",
            repository.FileCount,
            repository.ChangedVersionCount,
            FormatSize(repository.TotalSizeBytes));

        var lastSnapshotText = repository.LastScannedAt is { } lastScannedAt
            ? Loc.F("tray.panel_repository_last_snapshot_value", FormatRelativeTime(lastScannedAt))
            : Loc.T("tray.panel_repository_last_snapshot_empty");
        var (snapshotGlyph, snapshotAccent) = ResolveSnapshotVisualState(repository);
        var liveSyncStatus = liveSyncStatusStore.Get(repository.Id);
        var (liveSyncAccent, _, liveSyncGlyph) = RepositoryLiveSyncStatusPresenter.DescribeVisualState(liveSyncStatus);
        var liveSyncText = string.Concat(
            RepositoryLiveSyncStatusPresenter.FormatStateText(liveSyncStatus),
            ". ",
            RepositoryLiveSyncStatusPresenter.FormatSummaryText(liveSyncStatus));
        var liveSyncDetail = string.Concat(
            RepositoryLiveSyncStatusPresenter.FormatModeText(liveSyncStatus),
            ". ",
            RepositoryLiveSyncStatusPresenter.FormatDetailText(liveSyncStatus));

        var lastCloudSyncText = BuildCloudSyncSummary(cloudSync);
        var (cloudGlyph, cloudAccent) = ResolveCloudSyncVisualState(cloudSync);
        var queueText = BuildQueueSummary(cloudSync);
        var (queueGlyph, queueAccent) = ResolveQueueVisualState(cloudSync);

        var progressMaximum = Math.Max(1, cloudSync?.UploadProgressTotal ?? 0);
        var progressValue = Math.Clamp(cloudSync?.UploadProgressCurrent ?? 0, 0, progressMaximum);
        var showProgress = (cloudSync?.UploadProgressTotal ?? 0) > 0;
        var progressAccent = showProgress ? "#38BDF8" : queueAccent;
        var progressText = showProgress
            ? Loc.F(
                "tray.panel_repository_progress_value",
                progressValue,
                progressMaximum,
                cloudSync?.UploadProgressStartedAtUtc is { } startedAt
                    ? FormatRelativeTime(startedAt)
                    : Loc.T("dashboard.just_now"))
            : string.Empty;

        return new TrayPanelRepositoryState(
            repository.Name,
            repository.DirectoryPath,
            metricsText,
            lastSnapshotText,
            snapshotGlyph,
            snapshotAccent,
            liveSyncText,
            liveSyncGlyph,
            liveSyncAccent,
            liveSyncDetail,
            lastCloudSyncText,
            cloudGlyph,
            cloudAccent,
            queueText,
            queueGlyph,
            queueAccent,
            progressValue,
            progressMaximum,
            progressText,
            showProgress,
            progressAccent);
    }

    private string BuildCloudSyncSummary(RepositoryCloudSyncStatusDto? cloudSync)
    {
        if (!_hasCloudAccess)
            return Loc.T("tray.panel_repository_cloud_local_only");

        if (cloudSync?.LastSyncedAtUtc is { } lastSyncedAt)
            return Loc.F("tray.panel_repository_cloud_value", FormatRelativeTime(lastSyncedAt));

        return connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("tray.panel_repository_cloud_offline"),
            ConnectivityState.CloudUnavailable => Loc.T("tray.panel_repository_cloud_unavailable"),
            _ => Loc.T("tray.panel_repository_cloud_never")
        };
    }

    private string BuildQueueSummary(RepositoryCloudSyncStatusDto? cloudSync)
    {
        if (!_hasCloudAccess)
            return Loc.T("tray.panel_repository_queue_local_only");

        if (cloudSync is null)
            return Loc.T("tray.panel_repository_queue_empty");

        var total = cloudSync.PendingQueueCount
                    + cloudSync.RunningQueueCount
                    + cloudSync.RetryQueueCount
                    + cloudSync.ConflictQueueCount
                    + cloudSync.DeadLetterQueueCount;

        if (total <= 0)
            return Loc.T("tray.panel_repository_queue_empty");

        return Loc.F(
            "tray.panel_repository_queue_value",
            cloudSync.PendingQueueCount,
            cloudSync.RunningQueueCount,
            cloudSync.RetryQueueCount,
            cloudSync.ConflictQueueCount);
    }

    private (string Glyph, string AccentColor) ResolveSnapshotVisualState(RepositoryDto repository)
    {
        if (string.Equals(_currentActivityKind, "snapshot", StringComparison.Ordinal) &&
            _currentActivityRepositoryId == repository.Id)
        {
            return ("\uE823", "#38BDF8");
        }

        if (repository.LastScannedAt is null)
            return ("\uEA39", "#94A3B8");

        return ("\uE73E", "#4ADE80");
    }

    private (string Glyph, string AccentColor) ResolveCloudSyncVisualState(RepositoryCloudSyncStatusDto? cloudSync)
    {
        if (!_hasCloudAccess)
            return ("\uE73E", "#7CC7FF");

        if ((cloudSync?.UploadProgressTotal ?? 0) > 0 || (cloudSync?.RunningQueueCount ?? 0) > 0)
            return ("\uE753", "#38BDF8");

        if (!string.IsNullOrWhiteSpace(cloudSync?.LastError) || (cloudSync?.DeadLetterQueueCount ?? 0) > 0)
            return ("\uEA39", "#F97316");

        if (connectivity.Snapshot.State is ConnectivityState.InternetUnavailable or ConnectivityState.CloudUnavailable)
            return ("\uEA39", "#F59E0B");

        if (cloudSync?.LastSyncedAtUtc is not null)
            return ("\uE73E", "#4ADE80");

        return ("\uE9CE", "#94A3B8");
    }

    private (string Glyph, string AccentColor) ResolveQueueVisualState(RepositoryCloudSyncStatusDto? cloudSync)
    {
        if (!_hasCloudAccess)
            return ("\uE73E", "#7CC7FF");

        if ((cloudSync?.RunningQueueCount ?? 0) > 0 || (cloudSync?.UploadProgressTotal ?? 0) > 0)
            return ("\uE895", "#38BDF8");

        if ((cloudSync?.ConflictQueueCount ?? 0) > 0 || (cloudSync?.DeadLetterQueueCount ?? 0) > 0)
            return ("\uEA39", "#F97316");

        if ((cloudSync?.RetryQueueCount ?? 0) > 0 || (cloudSync?.PendingQueueCount ?? 0) > 0)
            return ("\uE895", "#F59E0B");

        return ("\uE73E", "#4ADE80");
    }

    private string FormatRelativeTime(DateTime timestampUtc)
    {
        var localTime = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc.ToLocalTime() : timestampUtc;
        var delta = DateTime.Now - localTime;

        if (delta.TotalSeconds < 45)
            return Loc.T("dashboard.just_now");

        if (delta.TotalMinutes < 60)
            return Loc.F("dashboard.minutes_ago", Math.Max(1, (int)delta.TotalMinutes));

        if (delta.TotalHours < 24)
            return Loc.F("dashboard.hours_ago", Math.Max(1, (int)delta.TotalHours));

        return Loc.F("dashboard.days_ago", Math.Max(1, (int)delta.TotalDays));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;

        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024d;
            index++;
        }

        var format = value >= 100 || index == 0 ? "0" : "0.#";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {suffixes[index]}";
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
        if (!await _operationGate.WaitAsync(0))
        {
            SetLastStatus(Loc.T("tray.status.busy"));
            return;
        }

        try
        {
            SetLastStatus(Loc.F("tray.status.snapshot_running", repositoryName));
            SetCurrentActivity(
                Loc.F("tray.activity_snapshot_running", repositoryName),
                "#38BDF8",
                "#1638BDF8",
                "snapshot",
                repositoryId);

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
            SetCurrentActivity(null);
            _operationGate.Release();
        }
    }

    private async Task ProcessCloudQueueAsync()
    {
        if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
        {
            SetLastStatus(connectivityMessage);
            return;
        }

        if (!await _operationGate.WaitAsync(0))
        {
            SetLastStatus(Loc.T("tray.status.busy"));
            return;
        }

        try
        {
            if (!await ConfirmTrayCloudActionAsync(
                    "tray.confirm.cloud_queue_title",
                    "tray.confirm.cloud_queue_body",
                    "tray.confirm.cloud_queue_warning",
                    "tray.confirm.cloud_queue_button"))
            {
                SetLastStatus(Loc.T("tray.status.action_cancelled"));
                return;
            }

            var guardResult = await AuthorizeSensitiveCloudActionAsync("security.action_cloud_sync", "security.action_process_queue_body");
            if (!guardResult.IsAllowed)
            {
                SetLastStatus(guardResult.IsCancelled
                    ? Loc.T("tray.status.action_cancelled")
                    : guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed"));
                return;
            }

            SetLastStatus(Loc.T("tray.status.cloud_queue_running"));
            SetCurrentActivity(Loc.T("tray.activity_queue_running"), "#38BDF8", "#1638BDF8", "queue");

            await scopeExecutor.ExecuteAsync<IRepositoryCloudSyncOrchestrator>(
                (sync, ct) => sync.ProcessPendingQueueAsync(ct));

            SetLastStatus(Loc.T("tray.status.cloud_queue_processed"));
            await RefreshMenuAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Tray cloud queue processing failed");
            var message = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "ui_error.cloud_temporarily_unavailable");
            SetLastStatus(Loc.F("tray.status.cloud_failed_with_reason", message));
        }
        finally
        {
            SetCurrentActivity(null);
            _operationGate.Release();
        }
    }

    private async Task PushAllRepositoriesToCloudAsync()
    {
        if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
        {
            SetLastStatus(connectivityMessage);
            return;
        }

        if (!await _operationGate.WaitAsync(0))
        {
            SetLastStatus(Loc.T("tray.status.busy"));
            return;
        }

        try
        {
            if (!await ConfirmTrayCloudActionAsync(
                    "tray.confirm.cloud_push_title",
                    "tray.confirm.cloud_push_body",
                    "tray.confirm.cloud_push_warning",
                    "tray.confirm.cloud_push_button"))
            {
                SetLastStatus(Loc.T("tray.status.action_cancelled"));
                return;
            }

            var guardResult = await AuthorizeSensitiveCloudActionAsync("security.action_cloud_sync", "security.action_push_all_body");
            if (!guardResult.IsAllowed)
            {
                SetLastStatus(guardResult.IsCancelled
                    ? Loc.T("tray.status.action_cancelled")
                    : guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed"));
                return;
            }

            SetLastStatus(Loc.T("tray.status.cloud_push_running"));
            SetCurrentActivity(Loc.T("tray.activity_push_running"), "#38BDF8", "#1638BDF8", "push_all");

            var repositories = await scopeExecutor.ExecuteAsync<IMediator, IReadOnlyList<RepositoryDto>>(
                (mediator, ct) => mediator.Send(new GetAllRepositoriesQuery(), ct));

            var repositoryIds = repositories
                .Where(static repository => !repository.IsDeleted)
                .Select(static repository => repository.Id)
                .ToArray();

            if (repositoryIds.Length == 0)
            {
                SetLastStatus(Loc.T("tray.status.cloud_no_repositories"));
                return;
            }

            await scopeExecutor.ExecuteAsync<IRepositoryCloudSyncOrchestrator>(async (sync, ct) =>
            {
                foreach (var repositoryId in repositoryIds)
                    await sync.TryPushLatestSnapshotAsync(repositoryId, ct);

                await sync.ProcessPendingQueueAsync(ct);
            });

            SetLastStatus(Loc.F("tray.status.cloud_push_requested", repositoryIds.Length));
            await RefreshMenuAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Tray push-all to cloud failed");
            var message = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "ui_error.cloud_temporarily_unavailable");
            SetLastStatus(Loc.F("tray.status.cloud_failed_with_reason", message));
        }
        finally
        {
            SetCurrentActivity(null);
            _operationGate.Release();
        }
    }

    private async Task<bool> ConfirmTrayCloudActionAsync(
        string titleKey,
        string bodyKey,
        string warningKey,
        string confirmButtonKey)
    {
        var owner = await EnsureInteractiveOwnerAsync();
        if (owner is null)
            return false;

        var window = windows.Create<ConfirmActionWindow>();
        if (window.DataContext is ConfirmActionWindowViewModel vm)
        {
            vm.ConfigureLocalized(
                titleKey,
                bodyKey,
                null,
                warningKey,
                confirmButtonKey);
        }

        await windows.ShowDialogAsync(window, owner);
        return window.DataContext is ConfirmActionWindowViewModel resultVm && resultVm.IsConfirmed;
    }

    private async Task<SensitiveActionGuardResult> AuthorizeSensitiveCloudActionAsync(string titleKey, string bodyKey)
    {
        var owner = await EnsureInteractiveOwnerAsync();
        if (owner is null)
            return new SensitiveActionGuardResult(false, false, Loc.T("security.error_window_unavailable"));

        return await scopeExecutor.ExecuteAsync<ISensitiveActionGuard, SensitiveActionGuardResult>(
            (guard, ct) => guard.AuthorizeIfRequiredLocalizedAsync(titleKey, bodyKey, ct: ct));
    }

    private async Task<Window?> EnsureInteractiveOwnerAsync()
    {
        var window = await EnsureMainWindowAsync();
        if (window is null)
            return null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!window.IsVisible)
                window.Show();

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Activate();
        });

        return window;
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
