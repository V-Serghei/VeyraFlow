using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Indexing;

public interface IRepositoryScanner
{
    public Task<RepositoryScanResultDto> ScanRepositoryAsync(
        int repositoryId,
        IProgress<RepositoryScanProgressDto>? progress = null,
        RepositoryScanOptionsDto? options = null,
        CancellationToken ct = default);

    public Task ScanAllRepositoriesAsync(CancellationToken ct = default);
}
