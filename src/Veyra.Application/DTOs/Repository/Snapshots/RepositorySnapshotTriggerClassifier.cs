namespace Veyra.Application.DTOs;

public static class RepositorySnapshotTriggerClassifier
{
    public static string GetKind(string? trigger)
    {
        var normalized = (trigger ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return RepositorySnapshotKind.Manual;

        if (IsWorkingCore(normalized))
            return RepositorySnapshotKind.Working;

        if (IsAutomaticCore(normalized))
            return RepositorySnapshotKind.Automatic;

        return RepositorySnapshotKind.Manual;
    }

    public static bool IsManual(string? trigger)
        => string.Equals(GetKind(trigger), RepositorySnapshotKind.Manual, StringComparison.OrdinalIgnoreCase);

    public static bool IsAutomatic(string? trigger)
        => string.Equals(GetKind(trigger), RepositorySnapshotKind.Automatic, StringComparison.OrdinalIgnoreCase);

    public static bool IsWorking(string? trigger)
        => string.Equals(GetKind(trigger), RepositorySnapshotKind.Working, StringComparison.OrdinalIgnoreCase);

    private static bool IsAutomaticCore(string trigger)
    {
        return trigger.StartsWith("auto_snapshot", StringComparison.OrdinalIgnoreCase)
               || trigger.StartsWith("scheduled_snapshot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkingCore(string trigger)
    {
        return trigger.StartsWith("scheduled_sync", StringComparison.OrdinalIgnoreCase)
               || trigger.StartsWith("sync_index", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trigger, "sync_live_watcher", StringComparison.OrdinalIgnoreCase);
    }
}
