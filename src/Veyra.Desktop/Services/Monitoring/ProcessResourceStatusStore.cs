using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.DTOs.OperationJournal;
using Veyra.Desktop.Services.Execution;
using Veyra.Desktop.Services.Monitoring.Models;
using Veyra.Desktop.Services.Repositories;

namespace Veyra.Desktop.Services.Monitoring;

public sealed class ProcessResourceStatusStore : IProcessResourceStatusStore, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecentJournalWindow = TimeSpan.FromSeconds(20);
    private const int HistoryCapacity = 40;

    private readonly IProcessResourceMonitorService _monitor;
    private readonly IMonitoringControlService _monitoringControl;
    private readonly IRepositoryLiveSyncStatusStore _liveSyncStatusStore;
    private readonly IServiceScopeExecutor _scopeExecutor;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private int _isRefreshing;
    private ProcessResourceSnapshotDto? _snapshot;
    private readonly List<ProcessResourceHistoryEntryDto> _history = [];

    public ProcessResourceStatusStore(
        IProcessResourceMonitorService monitor,
        IMonitoringControlService monitoringControl,
        IRepositoryLiveSyncStatusStore liveSyncStatusStore,
        IServiceScopeExecutor scopeExecutor)
    {
        _monitor = monitor;
        _monitoringControl = monitoringControl;
        _liveSyncStatusStore = liveSyncStatusStore;
        _scopeExecutor = scopeExecutor;
        _timer = new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _monitoringControl.StateChanged += OnMonitoringStateChanged;

        if (_monitoringControl.IsEnabled)
        {
            _timer.Change(PollInterval, PollInterval);
            _ = RefreshNowAsync();
        }
    }

    public event EventHandler<ProcessResourceStatusChangedEventArgs>? StatusChanged;

    public ProcessResourceSnapshotDto? Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public IReadOnlyList<ProcessResourceHistoryEntryDto> History
    {
        get
        {
            lock (_gate)
                return _history.ToArray();
        }
    }

    public void Dispose()
    {
        _monitoringControl.StateChanged -= OnMonitoringStateChanged;
        _timer.Dispose();
    }

    private void OnTick(object? state)
    {
        _ = RefreshNowAsync();
    }

    private void OnMonitoringStateChanged(bool enabled)
    {
        if (enabled)
        {
            _timer.Change(PollInterval, PollInterval);
            _ = RefreshNowAsync();
            return;
        }

        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        lock (_gate)
        {
            _snapshot = null;
            _history.Clear();
        }

        StatusChanged?.Invoke(this, new ProcessResourceStatusChangedEventArgs(null));
    }

    private async Task RefreshNowAsync()
    {
        if (!_monitoringControl.IsEnabled)
            return;

        if (Interlocked.Exchange(ref _isRefreshing, 1) != 0)
            return;

        try
        {
            var snapshot = _monitor.Capture();
            var cause = await ResolveCurrentCauseAsync(snapshot.CapturedAtUtc).ConfigureAwait(false);
            if (!_monitoringControl.IsEnabled)
                return;

            var historyEntry = new ProcessResourceHistoryEntryDto(
                snapshot.CapturedAtUtc,
                snapshot.CpuPercent,
                snapshot.WorkingSetBytes,
                snapshot.PrivateMemoryBytes,
                snapshot.ManagedHeapBytes,
                cause);

            lock (_gate)
            {
                _snapshot = snapshot;
                _history.Insert(0, historyEntry);

                if (_history.Count > HistoryCapacity)
                    _history.RemoveRange(HistoryCapacity, _history.Count - HistoryCapacity);
            }

            StatusChanged?.Invoke(this, new ProcessResourceStatusChangedEventArgs(snapshot));
        }
        finally
        {
            Volatile.Write(ref _isRefreshing, 0);
        }
    }

    private async Task<ProcessLoadCauseSnapshotDto> ResolveCurrentCauseAsync(DateTime nowUtc)
    {
        if (ResolveLiveSyncCause() is { } liveSyncCause)
            return liveSyncCause;

        if (await ResolveJournalCauseAsync(nowUtc).ConfigureAwait(false) is { } journalCause)
            return journalCause;

        return new ProcessLoadCauseSnapshotDto("background");
    }

    private ProcessLoadCauseSnapshotDto? ResolveLiveSyncCause()
    {
        var activeStatus = _liveSyncStatusStore.GetAll()
            .Where(RepositoryLiveSyncStatusPresenter.IsBusy)
            .OrderByDescending(RepositoryLiveSyncStatusPresenter.GetActivityPriority)
            .ThenByDescending(status => status.QueueTotalCount)
            .FirstOrDefault();

        if (activeStatus is null)
            return null;

        if (activeStatus.LastOperation.Kind == RepositoryLiveSyncOperationKind.FallbackRequested
            && activeStatus.IsScanRunning)
        {
            return new ProcessLoadCauseSnapshotDto("live_sync_fallback", activeStatus.RepositoryName);
        }

        return activeStatus.SaveFileVersions
            ? new ProcessLoadCauseSnapshotDto("live_sync_versions", activeStatus.RepositoryName)
            : new ProcessLoadCauseSnapshotDto("live_sync_index", activeStatus.RepositoryName);
    }

    private async Task<ProcessLoadCauseSnapshotDto?> ResolveJournalCauseAsync(DateTime nowUtc)
    {
        try
        {
            var entries = await _scopeExecutor.ExecuteAsync<IOperationJournalService, IReadOnlyList<OperationJournalEntryDto>>(
                (journal, ct) => journal.GetRecentAsync(6, ct)).ConfigureAwait(false);

            var candidate = entries
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .FirstOrDefault(entry => nowUtc - entry.OccurredAtUtc <= RecentJournalWindow);

            if (candidate is null)
                return null;

            return candidate.Action switch
            {
                "settings_process_queue" => new ProcessLoadCauseSnapshotDto("cloud_queue"),
                "settings_push_all" => new ProcessLoadCauseSnapshotDto("cloud_push_all"),
                "settings_restore_from_cloud" => new ProcessLoadCauseSnapshotDto("cloud_restore"),
                "settings_cloud_storage_repair" => new ProcessLoadCauseSnapshotDto("cloud_repair"),
                "settings_cloud_storage_refresh" => new ProcessLoadCauseSnapshotDto("cloud_refresh"),
                "settings_retry_repository_sync" => new ProcessLoadCauseSnapshotDto("cloud_retry"),
                "scheduled_integrity_verification" => new ProcessLoadCauseSnapshotDto("integrity_check"),
                _ when string.Equals(candidate.Category, "sync", StringComparison.OrdinalIgnoreCase)
                    => new ProcessLoadCauseSnapshotDto("sync_background"),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
