using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Indexing;

public interface IRepositorySnapshotRepository
{
    Task SaveSnapshotAsync(
        int repositoryId,
        string trigger,
        DateTime scannedAtUtc,
        IReadOnlyCollection<RepositoryScanEntryDto> entries,
        CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryScanEntryDto>> GetLatestEntriesAsync(
        int repositoryId,
        CancellationToken ct = default);

    Task<IReadOnlyList<FileVersionInfoDto>> GetFileVersionsAsync(
        int repositoryId,
        string relativePath,
        int take = 50,
        CancellationToken ct = default);

    Task<FileVersionRestoreDto?> GetFileVersionRestoreDataAsync(
        long fileVersionId,
        CancellationToken ct = default);
}
