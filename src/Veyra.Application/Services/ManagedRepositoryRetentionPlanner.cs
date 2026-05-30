using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Services;

public sealed class ManagedRepositoryRetentionPlanner : IRepositoryRetentionPlanner
{
    public Task<RepositoryRetentionPlanResult> PlanAsync(
        RepositoryRetentionPlanRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var eligible = request.Snapshots
            .Where(static s => !s.IsProtected)
            .Where(s => MatchesTriggerFilter(s, request.TriggerFilter, request.AllowManualSnapshotCleanup))
            .ToList();

        var toDelete = new HashSet<long>();
        var automaticSnapshotsCompacted = 0;

        if (request.MaxAgeDays is > 0)
        {
            var cutoff = request.NowUtc.AddDays(-request.MaxAgeDays.Value);
            foreach (var snapshot in eligible)
            {
                if (snapshot.CreatedAt < cutoff)
                    toDelete.Add(snapshot.Id);
            }
        }

        automaticSnapshotsCompacted = ApplyAutomaticCompactionCandidates(
            request.Snapshots,
            toDelete,
            request.TriggerFilter,
            request.AllowManualSnapshotCleanup,
            request.AutomaticCompactionEnabled,
            request.AutomaticCompactionWindowHours);

        if (request.MaxSnapshots is > 0)
        {
            var sorted = eligible
                .Where(s => !toDelete.Contains(s.Id))
                .OrderByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.Id)
                .ToList();

            var keepSet = sorted
                .Take(request.MaxSnapshots.Value)
                .Select(static s => s.Id)
                .ToHashSet();

            foreach (var snapshot in sorted)
            {
                if (!keepSet.Contains(snapshot.Id))
                    toDelete.Add(snapshot.Id);
            }
        }

        if (request.MaxTotalSizeBytes is > 0)
        {
            var total = request.Snapshots
                .Where(s => !toDelete.Contains(s.Id))
                .Sum(static s => s.TotalFileBytes);

            if (total > request.MaxTotalSizeBytes.Value)
            {
                var removable = request.Snapshots
                    .Where(s => !toDelete.Contains(s.Id)
                                && MatchesTriggerFilter(s, request.TriggerFilter, request.AllowManualSnapshotCleanup))
                    .OrderBy(static s => s.CreatedAt)
                    .ThenBy(static s => s.Id)
                    .ToList();

                foreach (var snapshot in removable)
                {
                    if (total <= request.MaxTotalSizeBytes.Value)
                        break;

                    var remainingCount = request.Snapshots.Count - toDelete.Count;
                    if (remainingCount <= 1)
                        break;

                    if (toDelete.Add(snapshot.Id))
                        total -= snapshot.TotalFileBytes;
                }
            }
        }

        return Task.FromResult(new RepositoryRetentionPlanResult(toDelete, automaticSnapshotsCompacted));
    }

    private static bool MatchesTriggerFilter(
        RepositoryRetentionSnapshotPlanState snapshot,
        IReadOnlyCollection<string> triggerFilter,
        bool allowManualSnapshotCleanup)
    {
        var normalizedTrigger = snapshot.Trigger.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTrigger))
            return false;

        if (!allowManualSnapshotCleanup && snapshot.IsManual)
            return false;

        if (triggerFilter.Count == 0)
            return true;

        foreach (var token in triggerFilter)
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (string.Equals(token, "all", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(token, normalizedTrigger, StringComparison.OrdinalIgnoreCase))
                return true;

            if (token.EndsWith('*') &&
                normalizedTrigger.StartsWith(token[..^1], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(token, "automatic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "auto", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshot.IsAutomaticMatch)
                    return true;

                continue;
            }

            if (string.Equals(token, "manual", StringComparison.OrdinalIgnoreCase))
            {
                if (allowManualSnapshotCleanup && snapshot.IsManual)
                    return true;

                continue;
            }

            if (string.Equals(token, "working", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshot.IsWorking)
                    return true;

                continue;
            }

            if (string.Equals(token, "scheduled", StringComparison.OrdinalIgnoreCase)
                && normalizedTrigger.StartsWith("scheduled_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ApplyAutomaticCompactionCandidates(
        IReadOnlyList<RepositoryRetentionSnapshotPlanState> snapshots,
        HashSet<long> toDelete,
        IReadOnlyCollection<string> triggerFilter,
        bool allowManualSnapshotCleanup,
        bool automaticCompactionEnabled,
        int? automaticCompactionWindowHours)
    {
        if (!automaticCompactionEnabled || automaticCompactionWindowHours is not > 0)
            return 0;

        var ticksPerBucket = TimeSpan.FromHours(automaticCompactionWindowHours.Value).Ticks;
        if (ticksPerBucket <= 0)
            return 0;

        var compactable = snapshots
            .Where(s => !toDelete.Contains(s.Id))
            .Where(static s => s.IsAutomatic)
            .Where(s => MatchesTriggerFilter(s, triggerFilter, allowManualSnapshotCleanup))
            .GroupBy(s => GetCompactionBucket(s.CreatedAt, ticksPerBucket));

        var compacted = 0;

        foreach (var bucket in compactable)
        {
            var ordered = bucket
                .OrderByDescending(static s => s.CreatedAt)
                .ThenByDescending(static s => s.Id)
                .ToList();

            foreach (var snapshot in ordered.Skip(1))
            {
                if (toDelete.Add(snapshot.Id))
                    compacted++;
            }
        }

        return compacted;
    }

    private static long GetCompactionBucket(DateTime createdAt, long ticksPerBucket)
    {
        var utc = createdAt.Kind == DateTimeKind.Utc
            ? createdAt
            : createdAt.ToUniversalTime();

        return utc.Ticks / ticksPerBucket;
    }
}
