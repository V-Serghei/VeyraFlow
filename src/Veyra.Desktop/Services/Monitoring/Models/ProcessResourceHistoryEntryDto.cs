namespace Veyra.Desktop.Services.Monitoring.Models;

public sealed record ProcessResourceHistoryEntryDto(
    global::System.DateTime CapturedAtUtc,
    double? CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    ProcessLoadCauseSnapshotDto Cause);
