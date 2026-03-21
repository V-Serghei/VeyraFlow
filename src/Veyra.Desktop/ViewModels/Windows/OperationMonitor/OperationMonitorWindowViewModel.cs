using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Monitoring;
using Veyra.Desktop.Services.Monitoring.Models;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class OperationMonitorWindowViewModel : ObservableObject
{
    private readonly IOperationMonitorService _monitor;
    private readonly ILogger<OperationMonitorWindowViewModel> _log;
    private readonly LocalizationManager _localization;
    private DispatcherTimer? _autoRefreshTimer;
    private bool _isStarted;
    private OperationMonitorSnapshotDto? _lastSnapshot;

    public event Action? RequestClose;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isAutoRefreshEnabled = true;
    [ObservableProperty] private string _lastUpdatedText = string.Empty;
    [ObservableProperty] private string _cpuText = string.Empty;
    [ObservableProperty] private string _workingSetText = string.Empty;
    [ObservableProperty] private string _privateBytesText = string.Empty;
    [ObservableProperty] private string _managedHeapText = string.Empty;
    [ObservableProperty] private string _threadCountText = string.Empty;
    [ObservableProperty] private string _handleCountText = string.Empty;
    [ObservableProperty] private string _processAgeText = string.Empty;
    [ObservableProperty] private string _monitorMessage = string.Empty;

    public ObservableCollection<OperationMonitorActiveOperationItemViewModel> ActiveOperations { get; } = [];
    public ObservableCollection<OperationMonitorRecentOperationItemViewModel> RecentOperations { get; } = [];

    public bool HasActiveOperations => ActiveOperations.Count > 0;
    public bool HasRecentOperations => RecentOperations.Count > 0;

    public OperationMonitorWindowViewModel(
        IOperationMonitorService monitor,
        ILogger<OperationMonitorWindowViewModel> log)
    {
        _monitor = monitor;
        _log = log;
        _localization = LocalizationManager.Instance;
        _localization.LanguageChanged += OnLanguageChanged;
        ClearSnapshotState();
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        UpdateAutoRefreshTimer();
    }

    public async Task StartAsync()
    {
        if (_isStarted)
            return;

        _isStarted = true;
        await RefreshAsync(initial: true);
        UpdateAutoRefreshTimer();
    }

    public Task StopAsync()
    {
        _isStarted = false;
        StopAutoRefreshTimer();
        _localization.LanguageChanged -= OnLanguageChanged;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshAsync(initial: false);

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private async Task RefreshAsync(bool initial)
    {
        if (IsRefreshing || IsLoading)
            return;

        try
        {
            if (initial && _lastSnapshot is null)
                IsLoading = true;
            else
                IsRefreshing = true;

            MonitorMessage = string.Empty;

            var snapshot = await _monitor.CaptureAsync();
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to refresh operation monitor window");
            MonitorMessage = Loc.T("app_settings.operation_monitor_refresh_failed");
            if (_lastSnapshot is null)
                ClearSnapshotState();
        }
        finally
        {
            IsLoading = false;
            IsRefreshing = false;
        }
    }

    private void ApplySnapshot(OperationMonitorSnapshotDto snapshot)
    {
        _lastSnapshot = snapshot;
        LastUpdatedText = snapshot.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        CpuText = snapshot.Process.CpuPercent.HasValue
            ? $"{snapshot.Process.CpuPercent.Value:0.#}%"
            : Loc.T("app_settings.operation_monitor_cpu_unavailable");
        WorkingSetText = FormatBytes(snapshot.Process.WorkingSetBytes);
        PrivateBytesText = FormatBytes(snapshot.Process.PrivateMemoryBytes);
        ManagedHeapText = FormatBytes(snapshot.Process.ManagedHeapBytes);
        ThreadCountText = snapshot.Process.ThreadCount.ToString();
        HandleCountText = snapshot.Process.HandleCount.ToString();
        ProcessAgeText = snapshot.Process.StartedAtUtc.HasValue
            ? FormatDuration(DateTime.UtcNow - snapshot.Process.StartedAtUtc.Value)
            : Loc.T("common.not_available_short");

        ActiveOperations.Clear();
        foreach (var item in snapshot.ActiveRepositories.Select(MapActiveOperation))
            ActiveOperations.Add(item);

        RecentOperations.Clear();
        foreach (var item in snapshot.RecentOperations.Select(MapRecentOperation))
            RecentOperations.Add(item);

        OnPropertyChanged(nameof(HasActiveOperations));
        OnPropertyChanged(nameof(HasRecentOperations));
    }

    private void ClearSnapshotState()
    {
        LastUpdatedText = Loc.T("common.not_available_short");
        CpuText = Loc.T("app_settings.operation_monitor_cpu_unavailable");
        WorkingSetText = Loc.T("common.not_available_short");
        PrivateBytesText = Loc.T("common.not_available_short");
        ManagedHeapText = Loc.T("common.not_available_short");
        ThreadCountText = Loc.T("common.not_available_short");
        HandleCountText = Loc.T("common.not_available_short");
        ProcessAgeText = Loc.T("common.not_available_short");
        ActiveOperations.Clear();
        RecentOperations.Clear();
        OnPropertyChanged(nameof(HasActiveOperations));
        OnPropertyChanged(nameof(HasRecentOperations));
    }

    private OperationMonitorActiveOperationItemViewModel MapActiveOperation(OperationMonitorRepositorySnapshotDto item)
    {
        var totalQueued = item.PendingQueueCount + item.RunningQueueCount + item.RetryQueueCount + item.ConflictQueueCount + item.FailedQueueCount + item.DeadLetterQueueCount;
        var progressPercent = item.HasProgress && item.ProgressTotal > 0
            ? Math.Round(item.ProgressCurrent / (double)item.ProgressTotal * 100d, 1)
            : 0;

        var queueText = Loc.F(
            "app_settings.operation_monitor_queue_format",
            item.PendingQueueCount,
            item.RunningQueueCount,
            item.RetryQueueCount,
            item.ConflictQueueCount,
            item.FailedQueueCount + item.DeadLetterQueueCount,
            totalQueued);

        var progressText = item.HasProgress && item.ProgressTotal > 0
            ? Loc.F(
                "app_settings.operation_monitor_progress_format",
                item.ProgressCurrent,
                item.ProgressTotal,
                progressPercent.ToString("0.#"))
            : Loc.T("common.not_available_short");

        var updatedText = item.ProgressUpdatedAtUtc.HasValue
            ? item.ProgressUpdatedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : Loc.T("common.not_available_short");

        return new OperationMonitorActiveOperationItemViewModel(
            item.RepositoryId,
            item.Name,
            LocalizeStatus(item.StatusCode),
            queueText,
            item.HasProgress && item.ProgressTotal > 0,
            progressPercent,
            progressText,
            item.ProgressStartedAtUtc.HasValue
                ? FormatDuration(DateTime.UtcNow - item.ProgressStartedAtUtc.Value)
                : Loc.T("common.not_available_short"),
            updatedText,
            UserFacingMessageLocalizer.TryLocalize(item.LastError) ?? item.LastError ?? string.Empty,
            !string.IsNullOrWhiteSpace(item.LastError));
    }

    private static OperationMonitorRecentOperationItemViewModel MapRecentOperation(OperationMonitorJournalSnapshotDto item)
    {
        return new OperationMonitorRecentOperationItemViewModel(
            item.Id,
            item.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            HumanizeValue(item.Category),
            HumanizeAction(item.Action),
            UserFacingMessageLocalizer.TryLocalize(item.Message) ?? item.Message,
            item.DurationMs.HasValue ? $"{item.DurationMs.Value} ms" : Loc.T("common.not_available_short"),
            HumanizeValue(item.Level));
    }

    private string LocalizeStatus(string? statusCode)
    {
        return (statusCode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "running" => Loc.T("app_settings.operation_monitor_status_running"),
            "retry" => Loc.T("app_settings.operation_monitor_status_retry"),
            "failed" => Loc.T("app_settings.operation_monitor_status_failed"),
            "conflict" => Loc.T("app_settings.operation_monitor_status_conflict"),
            "pending" => Loc.T("app_settings.operation_monitor_status_pending"),
            "completed" => Loc.T("app_settings.operation_monitor_status_completed"),
            "idle" => Loc.T("app_settings.operation_monitor_status_idle"),
            _ => HumanizeValue(statusCode)
        };
    }

    private void UpdateAutoRefreshTimer()
    {
        if (!_isStarted || !IsAutoRefreshEnabled)
        {
            StopAutoRefreshTimer();
            return;
        }

        _autoRefreshTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        _autoRefreshTimer.Tick -= OnAutoRefreshTick;
        _autoRefreshTimer.Tick += OnAutoRefreshTick;
        if (!_autoRefreshTimer.IsEnabled)
            _autoRefreshTimer.Start();
    }

    private void StopAutoRefreshTimer()
    {
        if (_autoRefreshTimer is null)
            return;

        _autoRefreshTimer.Tick -= OnAutoRefreshTick;
        _autoRefreshTimer.Stop();
        _autoRefreshTimer = null;
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (IsRefreshing || IsLoading)
            return;

        await RefreshAsync(initial: false);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_lastSnapshot is not null)
        {
            ApplySnapshot(_lastSnapshot);
            return;
        }

        ClearSnapshotState();
    }

    private static string HumanizeAction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Loc.T("common.not_available_short");

        return value.Trim() switch
        {
            "ScanRepositoryCommand" => Loc.T("operation_journal.action.scan_repository"),
            "EnsureRepositoriesCommand" => Loc.T("operation_journal.action.refresh_repositories"),
            "CreateRepositoryWithFormatsCommand" => Loc.T("operation_journal.action.create_repository"),
            "dialog_login" or "settings_login" => Loc.T("operation_journal.action.sign_in"),
            "dialog_register" or "settings_register" => Loc.T("operation_journal.action.register"),
            "settings_cloud_storage_refresh" => Loc.T("operation_journal.action.refresh_cloud_status"),
            "settings_cloud_storage_repair" => Loc.T("operation_journal.action.repair_cloud_storage"),
            "scheduled_integrity_verification" => Loc.T("operation_journal.action.scheduled_integrity_verification"),
            _ => HumanizeValue(value)
        };
    }

    private static string HumanizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Loc.T("common.not_available_short");

        var normalized = value.Trim().Replace('_', ' ').Replace('-', ' ');
        return string.Join(" ", normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.#} {units[unitIndex]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return "0s";

        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h";

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";

        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";

        return $"{Math.Max(1, duration.Seconds)}s";
    }
}
