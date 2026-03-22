namespace Veyra.Application.Abstractions.Setup;

public interface IRepositorySnapshotArchiveService
{
    Task<int> EnsureSnapshotsArchivedAsync(
        int repositoryId,
        IReadOnlyCollection<long> snapshotIds,
        CancellationToken ct = default);

    Task<int> EnsureArchivedBlocksAvailableAsync(
        IReadOnlyCollection<string> blockStorageKeys,
        CancellationToken ct = default);

    Task<(int DeletedFiles, long DeletedBytes)> PruneArchivedOnlyLocalBlocksAsync(
        int repositoryId,
        CancellationToken ct = default);
}
