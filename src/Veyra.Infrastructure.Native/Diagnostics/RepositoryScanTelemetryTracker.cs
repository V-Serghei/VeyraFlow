using System.Collections.Concurrent;

namespace Veyra.Infrastructure.Native.Diagnostics;

internal static class RepositoryScanTelemetryTracker
{
    private static readonly ConcurrentDictionary<int, RepositoryScanTelemetrySnapshot> Snapshots = new();

    public static void Record(
        int repositoryId,
        string directoryPath,
        string engine,
        int entryCount,
        int fileCount,
        long totalFileBytes,
        long scanDurationMs,
        long totalDurationMs,
        string trigger,
        DateTime capturedAtUtc)
    {
        var snapshot = new RepositoryScanTelemetrySnapshot(
            repositoryId,
            directoryPath,
            engine,
            entryCount,
            fileCount,
            totalFileBytes,
            Math.Max(1L, scanDurationMs),
            Math.Max(1L, totalDurationMs),
            trigger,
            capturedAtUtc);

        Snapshots.AddOrUpdate(repositoryId, snapshot, (_, _) => snapshot);
    }

    public static bool TryGetFresh(int repositoryId, TimeSpan maxAge, out RepositoryScanTelemetrySnapshot snapshot)
    {
        if (Snapshots.TryGetValue(repositoryId, out var candidate)
            && DateTime.UtcNow - candidate.CapturedAtUtc <= maxAge)
        {
            snapshot = candidate;
            return true;
        }

        snapshot = default!;
        return false;
    }

    public static RepositoryScanTelemetrySnapshot? GetMostRecent(TimeSpan maxAge, Func<int, bool>? repositoryFilter = null)
    {
        var cutoff = DateTime.UtcNow - maxAge;

        return Snapshots.Values
            .Where(x => x.CapturedAtUtc >= cutoff)
            .Where(x => repositoryFilter is null || repositoryFilter(x.RepositoryId))
            .OrderByDescending(x => x.CapturedAtUtc)
            .ThenByDescending(x => x.TotalFileBytes)
            .FirstOrDefault();
    }
}

internal sealed record RepositoryScanTelemetrySnapshot(
    int RepositoryId,
    string DirectoryPath,
    string Engine,
    int EntryCount,
    int FileCount,
    long TotalFileBytes,
    long ScanDurationMs,
    long TotalDurationMs,
    string Trigger,
    DateTime CapturedAtUtc)
{
    public double ThroughputMbPerSecond => TotalFileBytes <= 0 || ScanDurationMs <= 0
        ? 0d
        : TotalFileBytes / 1024d / 1024d / (ScanDurationMs / 1000d);
}
