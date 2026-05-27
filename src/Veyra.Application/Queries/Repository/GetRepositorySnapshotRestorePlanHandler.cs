using MediatR;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Repository;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed class GetRepositorySnapshotRestorePlanHandler(
    IRepositoryRepository repositories,
    IRepositorySnapshotRepository snapshots,
    IFileContentStore contentStore)
    : IRequestHandler<GetRepositorySnapshotRestorePlanQuery, OperationResult<RepositorySnapshotRestorePlanDto>>
{
    public async Task<OperationResult<RepositorySnapshotRestorePlanDto>> Handle(
        GetRepositorySnapshotRestorePlanQuery request,
        CancellationToken ct)
    {
        var repo = await repositories.GetRepositoryByIdAsync(request.RepositoryId, ct);
        if (repo is null || repo.IsDeleted)
            return OperationResult<RepositorySnapshotRestorePlanDto>.Fail("Repository was not found.");

        var restoreData = await snapshots.GetSnapshotRestoreDataAsync(request.RepositoryId, request.SnapshotId, ct);
        if (restoreData is null)
            return OperationResult<RepositorySnapshotRestorePlanDto>.Fail("Snapshot was not found.");

        var latestEntries = await snapshots.GetLatestEntriesAsync(request.RepositoryId, ct);
        var latestFiles = latestEntries
            .Where(static entry => !entry.IsDirectory)
            .Where(static entry => !RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(entry.RelativePath))
            .ToDictionary(static entry => NormalizeRelativePath(entry.RelativePath), StringComparer.OrdinalIgnoreCase);

        var targetFiles = restoreData.FileVersions
            .Where(static version => !version.IsDeletionMarker)
            .Where(static version => !RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(version.RelativePath))
            .ToDictionary(static version => NormalizeRelativePath(version.RelativePath), StringComparer.OrdinalIgnoreCase);

        var filesToChange = targetFiles.Values.Count(version =>
        {
            if (!latestFiles.TryGetValue(NormalizeRelativePath(version.RelativePath), out var current))
                return true;

            return current.SizeBytes != version.SizeBytes
                   || !string.Equals(
                       current.ContentHashSha256 ?? string.Empty,
                       version.ContentHashSha256 ?? string.Empty,
                       StringComparison.OrdinalIgnoreCase);
        });

        var filesToMove = latestFiles.Keys.Count(path => !targetFiles.ContainsKey(path));
        var noBlockMarkers = targetFiles.Values
            .Where(static version => version.SizeBytes > 0 && version.Blocks.Count == 0)
            .Select(static version => $"{version.RelativePath}: no blocks")
            .ToList();
        var blockKeys = targetFiles.Values
            .SelectMany(static version => version.Blocks)
            .Select(static block => block.BlockStorageKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missing = noBlockMarkers
            .Concat(await contentStore.FindMissingBlocksAsync(blockKeys, ct))
            .ToList();

        var plan = new RepositorySnapshotRestorePlanDto(
            request.RepositoryId,
            request.SnapshotId,
            restoreData.Snapshot.Title,
            restoreData.Snapshot.CreatedAtUtc,
            targetFiles.Count,
            filesToChange,
            filesToMove,
            missing);

        return OperationResult<RepositorySnapshotRestorePlanDto>.Ok(plan);
    }

    private static string NormalizeRelativePath(string value)
        => value.Trim().Replace('\\', '/').Trim('/');
}
