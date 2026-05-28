using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.DTOs;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Services.Monitoring.Models;

namespace Veyra.Desktop.Services.Monitoring;

public sealed class OperationMonitorService(
    IProcessResourceMonitorService processResourceMonitor,
    IMediator mediator,
    IOperationJournalService journal,
    ILogger<OperationMonitorService> log)
    : IOperationMonitorService
{
    public async Task<OperationMonitorSnapshotDto> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var processSnapshot = processResourceMonitor.Capture();
        var nowUtc = processSnapshot.CapturedAtUtc;

        var repositories = await mediator.Send(new GetAllRepositoriesQuery(), cancellationToken);
        var activeRepositories = repositories
            .Select(MapActiveRepository)
            .Where(item => item is not null)
            .Cast<OperationMonitorRepositorySnapshotDto>()
            .OrderByDescending(item => GetStatusWeight(item.StatusCode))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recentOperations = (await journal.GetRecentAsync(25, cancellationToken))
            .OrderByDescending(item => item.OccurredAtUtc)
            .Select(MapJournalEntry)
            .ToList();

        log.LogDebug(
            "Operation monitor snapshot captured. ActiveRepositories {ActiveRepositories}. RecentOperations {RecentOperations}",
            activeRepositories.Count,
            recentOperations.Count);

        return new OperationMonitorSnapshotDto(
            nowUtc,
            MapProcessSnapshot(processSnapshot),
            activeRepositories,
            recentOperations);
    }

    private static OperationMonitorProcessSnapshotDto MapProcessSnapshot(ProcessResourceSnapshotDto snapshot)
    {
        return new OperationMonitorProcessSnapshotDto(
            snapshot.CpuPercent,
            snapshot.WorkingSetBytes,
            snapshot.PrivateMemoryBytes,
            snapshot.ManagedHeapBytes,
            snapshot.ThreadCount,
            snapshot.HandleCount,
            snapshot.StartedAtUtc);
    }

    private static OperationMonitorRepositorySnapshotDto? MapActiveRepository(RepositoryDto repository)
    {
        var sync = repository.CloudSync;
        if (sync is null)
            return null;

        var hasQueue =
            sync.PendingQueueCount > 0 ||
            sync.RunningQueueCount > 0 ||
            sync.RetryQueueCount > 0 ||
            sync.ConflictQueueCount > 0 ||
            sync.FailedQueueCount > 0 ||
            sync.DeadLetterQueueCount > 0;
        var hasProgress = sync.UploadProgressTotal > 0;
        var hasError = !string.IsNullOrWhiteSpace(sync.LastError);

        if (!hasQueue && !hasProgress && !hasError)
            return null;

        return new OperationMonitorRepositorySnapshotDto(
            repository.Id,
            repository.Name,
            ResolveStatusCode(sync),
            sync.LastStatus,
            sync.LastError,
            sync.PendingQueueCount,
            sync.RunningQueueCount,
            sync.RetryQueueCount,
            sync.ConflictQueueCount,
            sync.FailedQueueCount,
            sync.DeadLetterQueueCount,
            hasProgress,
            sync.UploadProgressCurrent,
            sync.UploadProgressTotal,
            sync.UploadProgressStartedAtUtc,
            sync.UploadProgressUpdatedAtUtc);
    }

    private static OperationMonitorJournalSnapshotDto MapJournalEntry(OperationJournalEntryDto entry)
    {
        return new OperationMonitorJournalSnapshotDto(
            entry.Id,
            entry.OccurredAtUtc,
            entry.Level,
            entry.Category,
            entry.Action,
            entry.RepositoryId,
            entry.Message,
            entry.Details,
            TryReadDetailLong(entry.Details, "ElapsedMs"));
    }

    private static string ResolveStatusCode(RepositoryCloudSyncStatusDto sync)
    {
        if (sync.RunningQueueCount > 0)
            return "running";

        if (sync.RetryQueueCount > 0)
            return "retry";

        if (sync.FailedQueueCount > 0 || sync.DeadLetterQueueCount > 0)
            return "failed";

        if (sync.ConflictQueueCount > 0)
            return "conflict";

        if (sync.PendingQueueCount > 0)
            return "pending";

        if (!string.IsNullOrWhiteSpace(sync.LastStatus))
            return sync.LastStatus!.Trim().ToLowerInvariant();

        return "idle";
    }

    private static int GetStatusWeight(string statusCode)
    {
        return statusCode switch
        {
            "running" => 5,
            "failed" => 4,
            "conflict" => 4,
            "retry" => 3,
            "pending" => 2,
            _ => 1
        };
    }

    private static long? TryReadDetailLong(string? details, string key)
    {
        var raw = TryReadDetailValue(details, key);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? TryReadDetailValue(string? details, string key)
    {
        if (string.IsNullOrWhiteSpace(details))
            return null;

        foreach (var segment in details.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var candidateKey = segment[..separatorIndex].Trim();
            if (!candidateKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            return segment[(separatorIndex + 1)..].Trim();
        }

        return null;
    }
}
