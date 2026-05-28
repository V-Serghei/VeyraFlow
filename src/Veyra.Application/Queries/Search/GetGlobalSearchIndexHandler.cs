using MediatR;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Search;

public sealed class GetGlobalSearchIndexHandler(
    IRepositoryRepository repositories,
    IRepositorySnapshotRepository snapshots)
    : IRequestHandler<GetGlobalSearchIndexQuery, GlobalSearchIndexLoadDto>
{
    public async Task<GlobalSearchIndexLoadDto> Handle(GetGlobalSearchIndexQuery request, CancellationToken ct)
    {
        var repositoryList = await repositories.GetAllRepositoriesAsync(ct);
        if (repositoryList.Count == 0)
            return new GlobalSearchIndexLoadDto(
                Array.Empty<RepositoryDto>(),
                Array.Empty<GlobalSearchRepositoryEntriesDto>(),
                Array.Empty<GlobalSearchRepositorySnapshotsDto>());

        var repositoryIds = repositoryList
            .Select(repository => repository.Id)
            .ToArray();

        var entriesTask = snapshots.GetLatestEntriesBatchAsync(repositoryIds, ct);
        var snapshotHistoryTask = snapshots.GetSnapshotHistoryBatchAsync(repositoryIds, request.SnapshotTake, ct);

        await Task.WhenAll(entriesTask, snapshotHistoryTask);
        var entriesByRepository = await entriesTask;
        var snapshotHistoryByRepository = await snapshotHistoryTask;

        var entries = repositoryIds
            .Select(repositoryId => new GlobalSearchRepositoryEntriesDto(
                repositoryId,
                entriesByRepository.GetValueOrDefault(repositoryId, Array.Empty<RepositoryScanEntryDto>())))
            .ToList();

        var snapshotHistory = repositoryIds
            .Select(repositoryId => new GlobalSearchRepositorySnapshotsDto(
                repositoryId,
                snapshotHistoryByRepository.GetValueOrDefault(repositoryId, Array.Empty<RepositorySnapshotHistoryItemDto>())))
            .ToList();

        var taggedFiles = await BuildTaggedFilesAsync(snapshotHistory, ct);

        return new GlobalSearchIndexLoadDto(repositoryList, entries, snapshotHistory, taggedFiles);
    }

    private async Task<IReadOnlyList<GlobalSearchTaggedFileDto>> BuildTaggedFilesAsync(
        IReadOnlyList<GlobalSearchRepositorySnapshotsDto> snapshotHistory,
        CancellationToken ct)
    {
        var taggedSnapshots = snapshotHistory
            .SelectMany(group => group.Snapshots
                .Where(snapshot => snapshot.Tags is { Count: > 0 })
                .Select(snapshot => new
                {
                    group.RepositoryId,
                    snapshot.SnapshotId,
                    Tags = snapshot.Tags!
                }))
            .ToList();

        if (taggedSnapshots.Count == 0)
            return Array.Empty<GlobalSearchTaggedFileDto>();

        var tagsByFile = new Dictionary<(int RepositoryId, string RelativePath), HashSet<string>>();

        foreach (var snapshot in taggedSnapshots)
        {
            var changes = await snapshots.GetSnapshotChangedFilesAsync(snapshot.RepositoryId, snapshot.SnapshotId, 5000, ct);
            foreach (var change in changes)
            {
                var key = (snapshot.RepositoryId, change.RelativePath);
                if (!tagsByFile.TryGetValue(key, out var tags))
                {
                    tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    tagsByFile[key] = tags;
                }

                foreach (var tag in snapshot.Tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                        tags.Add(tag.Trim());
                }
            }
        }

        return tagsByFile
            .Select(pair => new GlobalSearchTaggedFileDto(
                pair.Key.RepositoryId,
                pair.Key.RelativePath,
                pair.Value
                    .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
    }
}
