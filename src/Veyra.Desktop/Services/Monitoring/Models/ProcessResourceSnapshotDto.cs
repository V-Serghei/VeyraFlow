namespace Veyra.Desktop.Services.Monitoring.Models;

public sealed record ProcessResourceSnapshotDto(
    global::System.DateTime CapturedAtUtc,
    double? CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    int ThreadCount,
    int HandleCount,
    global::System.DateTime? StartedAtUtc);
