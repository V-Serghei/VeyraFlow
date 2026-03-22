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

        return new GlobalSearchIndexLoadDto(repositoryList, entries, snapshotHistory);
    }
}
