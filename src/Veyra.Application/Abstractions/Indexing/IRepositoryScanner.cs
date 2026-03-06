using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Indexing;

public interface IRepositoryScanner
{
    Task<RepositoryScanResultDto> ScanRepositoryAsync(
        int repositoryId,
        IProgress<RepositoryScanProgressDto>? progress = null,
        CancellationToken ct = default);

    Task ScanAllRepositoriesAsync(CancellationToken ct = default);
}
