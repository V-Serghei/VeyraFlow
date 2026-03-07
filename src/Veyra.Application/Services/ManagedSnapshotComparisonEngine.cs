using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;

namespace Veyra.Application.Services;

public sealed class ManagedSnapshotComparisonEngine : ISnapshotComparisonEngine
{
    public Task<SnapshotLinkComparisonResultDto> CompareSnapshotLinksAsync(
        IReadOnlyCollection<SnapshotLinkStateDto> current,
        IReadOnlyCollection<SnapshotLinkStateDto> previous,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var previousByIdentity = previous
            .GroupBy(x => x.FileIdentityId)
            .ToDictionary(g => g.Key, g => g.First());

        var changes = new List<SnapshotLinkChangeDto>();

        foreach (var state in current)
        {
            ct.ThrowIfCancellationRequested();

            previousByIdentity.TryGetValue(state.FileIdentityId, out var prev);

            if (prev is not null && prev.FileVersionId == state.FileVersionId)
                continue;

            var changeKind = ResolveLinkChangeKind(state, prev);
            var previousSize = prev?.SizeBytes ?? 0;

            changes.Add(new SnapshotLinkChangeDto(
                state.FileIdentityId,
                state.FileVersionId,
                state.RelativePath,
                state.Name,
                changeKind,
                state.SizeBytes,
                previousSize,
                state.VersionCreatedAtUtc));
        }

        var ordered = changes
            .OrderBy(c => c.ChangeKind switch
            {
                "added" => 0,
                "modified" => 1,
                "deleted" => 2,
                _ => 9
            })
            .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(new SnapshotLinkComparisonResultDto(ordered.Count, ordered));
    }

    public Task<RepositoryPathComparisonResultDto> CompareRepositoryPathsAsync(
        IReadOnlyCollection<RepositoryPathStateDto> current,
        IReadOnlyCollection<RepositoryPathStateDto> baseline,
        int take = 2000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var currentByPath = current
            .GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var baselineByPath = baseline
            .GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var modified = 0;
        var deleted = 0;

        var entries = new List<RepositoryPendingChangeEntryDto>();

        foreach (var cur in currentByPath.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (!baselineByPath.TryGetValue(cur.RelativePath, out var prev))
            {
                added++;
                entries.Add(new RepositoryPendingChangeEntryDto(
                    cur.RelativePath,
                    cur.Name,
                    "added",
                    cur.SizeBytes,
                    0,
                    cur.LastWriteUtc,
                    null));
                continue;
            }

            var curHash = cur.ContentHashSha256 ?? string.Empty;
            var prevHash = prev.ContentHashSha256 ?? string.Empty;

            if (cur.SizeBytes == prev.SizeBytes
                && string.Equals(curHash, prevHash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            modified++;
            entries.Add(new RepositoryPendingChangeEntryDto(
                cur.RelativePath,
                cur.Name,
                "modified",
                cur.SizeBytes,
                prev.SizeBytes,
                cur.LastWriteUtc,
                prev.LastWriteUtc));
        }

        foreach (var prev in baselineByPath.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (currentByPath.ContainsKey(prev.RelativePath))
                continue;

            deleted++;
            entries.Add(new RepositoryPendingChangeEntryDto(
                prev.RelativePath,
                prev.Name,
                "deleted",
                0,
                prev.SizeBytes,
                prev.LastWriteUtc,
                prev.LastWriteUtc));
        }

        var limit = Math.Clamp(take, 1, 5000);
        var ordered = entries
            .OrderBy(x => x.ChangeKind switch
            {
                "added" => 0,
                "modified" => 1,
                "deleted" => 2,
                _ => 9
            })
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        var changedFiles = added + modified + deleted;

        return Task.FromResult(new RepositoryPathComparisonResultDto(
            added,
            modified,
            deleted,
            changedFiles,
            ordered));
    }

    private static string ResolveLinkChangeKind(SnapshotLinkStateDto current, SnapshotLinkStateDto? previous)
    {
        if (current.IsDeletionMarker)
            return "deleted";

        if (previous is null || previous.IsDeletionMarker)
            return "added";

        return "modified";
    }
}
