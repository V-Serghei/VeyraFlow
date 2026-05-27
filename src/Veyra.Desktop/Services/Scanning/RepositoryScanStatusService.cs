using System;
using System.Collections.Concurrent;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.Services.Scanning;

public sealed class RepositoryScanStatusService : IRepositoryScanStatusService
{
    private readonly ConcurrentDictionary<int, RepositoryScanStatusSnapshot> _snapshots = new();

    public event EventHandler<RepositoryScanStatusChangedEventArgs>? StatusChanged;

    public RepositoryScanStatusSnapshot? GetSnapshot(int repositoryId) =>
        _snapshots.TryGetValue(repositoryId, out var snapshot) ? snapshot : null;

    public RepositoryScanStatusSnapshot Begin(int repositoryId, string trigger, string message)
    {
        var nowUtc = DateTime.UtcNow;
        var snapshot = new RepositoryScanStatusSnapshot(
            RepositoryId: repositoryId,
            IsActive: true,
            Trigger: trigger,
            Stage: "prepare",
            Percent: 0,
            FilesProcessed: 0,
            FilesTotal: 0,
            Message: message,
            IsIndeterminate: true,
            StartedAtUtc: nowUtc,
            UpdatedAtUtc: nowUtc,
            EstimatedRemaining: null);

        _snapshots[repositoryId] = snapshot;
        Publish(snapshot);
        return snapshot;
    }

    public RepositoryScanStatusSnapshot Report(int repositoryId, string trigger, RepositoryScanProgressDto progress)
    {
        var nowUtc = DateTime.UtcNow;
        var previous = GetSnapshot(repositoryId);
        var startedAtUtc = previous?.StartedAtUtc ?? nowUtc;
        var percent = Math.Clamp(progress.Percent, 0, 100);
        var filesTotal = Math.Max(0, progress.FilesTotal);
        var isIndeterminate = filesTotal <= 0 && percent < 100;
        var snapshot = new RepositoryScanStatusSnapshot(
            RepositoryId: repositoryId,
            IsActive: percent < 100,
            Trigger: trigger,
            Stage: progress.Stage,
            Percent: percent,
            FilesProcessed: Math.Max(0, progress.FilesProcessed),
            FilesTotal: filesTotal,
            Message: progress.Message,
            IsIndeterminate: isIndeterminate,
            StartedAtUtc: startedAtUtc,
            UpdatedAtUtc: nowUtc,
            EstimatedRemaining: EstimateRemaining(startedAtUtc, nowUtc, percent, isIndeterminate));

        _snapshots[repositoryId] = snapshot;
        Publish(snapshot);
        return snapshot;
    }

    public RepositoryScanStatusSnapshot Complete(int repositoryId, string trigger, bool success, string? message = null)
    {
        var nowUtc = DateTime.UtcNow;
        var previous = GetSnapshot(repositoryId);
        var snapshot = new RepositoryScanStatusSnapshot(
            RepositoryId: repositoryId,
            IsActive: false,
            Trigger: trigger,
            Stage: success ? "done" : "failed",
            Percent: success ? 100 : Math.Clamp(previous?.Percent ?? 0, 0, 99),
            FilesProcessed: previous?.FilesProcessed ?? 0,
            FilesTotal: previous?.FilesTotal ?? 0,
            Message: message ?? previous?.Message ?? string.Empty,
            IsIndeterminate: false,
            StartedAtUtc: previous?.StartedAtUtc ?? nowUtc,
            UpdatedAtUtc: nowUtc,
            EstimatedRemaining: null);

        _snapshots[repositoryId] = snapshot;
        Publish(snapshot);
        return snapshot;
    }

    private static TimeSpan? EstimateRemaining(DateTime startedAtUtc, DateTime nowUtc, int percent, bool isIndeterminate)
    {
        if (isIndeterminate || percent <= 0 || percent >= 100)
            return null;

        var elapsed = nowUtc - startedAtUtc;
        if (elapsed < TimeSpan.FromSeconds(5))
            return null;

        var totalSeconds = elapsed.TotalSeconds / percent * 100d;
        var remainingSeconds = totalSeconds - elapsed.TotalSeconds;
        if (remainingSeconds <= 1 || double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds))
            return null;

        return TimeSpan.FromSeconds(Math.Min(remainingSeconds, TimeSpan.FromDays(1).TotalSeconds));
    }

    private void Publish(RepositoryScanStatusSnapshot snapshot) =>
        StatusChanged?.Invoke(this, new RepositoryScanStatusChangedEventArgs(snapshot));
}
