namespace Veyra.Desktop.Services.Monitoring.Models;

public sealed record OperationMonitorJournalSnapshotDto(
    long Id,
    global::System.DateTime OccurredAtUtc,
    string Level,
    string Category,
    string Action,
    int? RepositoryId,
    string Message,
    string? Details,
    long? DurationMs);
