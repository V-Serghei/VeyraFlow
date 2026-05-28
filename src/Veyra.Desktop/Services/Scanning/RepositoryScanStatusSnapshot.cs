using System;

namespace Veyra.Desktop.Services.Scanning;

public sealed record RepositoryScanStatusSnapshot(
    int RepositoryId,
    bool IsActive,
    string Trigger,
    string Stage,
    int Percent,
    int FilesProcessed,
    int FilesTotal,
    string Message,
    bool IsIndeterminate,
    DateTime StartedAtUtc,
    DateTime UpdatedAtUtc,
    TimeSpan? EstimatedRemaining)
{
    public static RepositoryScanStatusSnapshot Inactive(int repositoryId, string trigger, DateTime nowUtc) =>
        new(
            RepositoryId: repositoryId,
            IsActive: false,
            Trigger: trigger,
            Stage: "idle",
            Percent: 100,
            FilesProcessed: 0,
            FilesTotal: 0,
            Message: string.Empty,
            IsIndeterminate: false,
            StartedAtUtc: nowUtc,
            UpdatedAtUtc: nowUtc,
            EstimatedRemaining: null);
}
