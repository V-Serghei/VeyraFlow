using System.Collections.Concurrent;
using Veyra.Application.DTOs;

namespace Veyra.Infrastructure.Data.Sync;

public sealed class CloudRepositoryOperationTracker
{
    private readonly ConcurrentDictionary<string, MutableOperation> _operations = new(StringComparer.OrdinalIgnoreCase);

    public string Start(string kind, int? repositoryId = null, int? cloudRepositoryId = null, string phase = "queued")
    {
        var id = $"{kind}-{Guid.NewGuid():N}";
        _operations[id] = new MutableOperation(
            id,
            repositoryId,
            cloudRepositoryId,
            kind,
            "running",
            phase,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            DateTime.UtcNow);
        return id;
    }

    public void Complete(string id, string phase = "completed")
        => Update(id, status: "completed", phase: phase, percent: 100, error: null);

    public void Fail(string id, string error, string phase = "failed")
        => Update(id, status: "failed", phase: phase, percent: 100, error: error);

    public void Update(
        string id,
        string? status = null,
        string? phase = null,
        int? percent = null,
        long? uploadedBytes = null,
        long? downloadedBytes = null,
        int? uploadedBlocks = null,
        int? downloadedBlocks = null,
        int? processedSnapshots = null,
        string? error = null)
    {
        if (!_operations.TryGetValue(id, out var current))
            return;

        _operations[id] = current with
        {
            Status = status ?? current.Status,
            Phase = phase ?? current.Phase,
            Percent = percent ?? current.Percent,
            UploadedBytes = uploadedBytes ?? current.UploadedBytes,
            DownloadedBytes = downloadedBytes ?? current.DownloadedBytes,
            UploadedBlocks = uploadedBlocks ?? current.UploadedBlocks,
            DownloadedBlocks = downloadedBlocks ?? current.DownloadedBlocks,
            ProcessedSnapshots = processedSnapshots ?? current.ProcessedSnapshots,
            Error = error,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public IReadOnlyList<CloudRepositoryOperationStatusDto> Snapshot()
    {
        var cutoff = DateTime.UtcNow.AddHours(-6);
        foreach (var stale in _operations
                     .Where(pair => pair.Value.UpdatedAtUtc < cutoff && pair.Value.Status is "completed" or "failed")
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _operations.TryRemove(stale, out _);
        }

        return _operations.Values
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new CloudRepositoryOperationStatusDto(
                x.OperationId,
                x.RepositoryId,
                x.CloudRepositoryId,
                x.OperationKind,
                x.Status,
                x.Phase,
                x.Percent,
                x.UploadedBytes,
                x.DownloadedBytes,
                x.UploadedBlocks,
                x.DownloadedBlocks,
                x.ProcessedSnapshots,
                x.Error))
            .ToList();
    }

    private sealed record MutableOperation(
        string OperationId,
        int? RepositoryId,
        int? CloudRepositoryId,
        string OperationKind,
        string Status,
        string Phase,
        int Percent,
        long UploadedBytes,
        long DownloadedBytes,
        int UploadedBlocks,
        int DownloadedBlocks,
        int ProcessedSnapshots,
        string? Error,
        DateTime UpdatedAtUtc);
}
