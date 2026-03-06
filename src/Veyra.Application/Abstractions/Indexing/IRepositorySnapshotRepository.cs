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
}
