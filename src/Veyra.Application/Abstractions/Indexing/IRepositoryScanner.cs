using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Scanning;

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
