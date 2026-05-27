using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Setup;

public interface IRepositoryIntegrityService
{
    Task<RepositoryIntegrityRunResultDto> VerifyRepositoryAsync(
        int repositoryId,
        bool repairFromCloud = false,
        int maxIssueSamples = 200,
        IProgress<RepositoryIntegrityProgressDto>? progress = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryIntegrityRunResultDto>> VerifyDueRepositoriesAsync(
        int intervalMinutes,
        bool repairFromCloud = false,
        int maxIssueSamples = 100,
        IProgress<RepositoryIntegrityProgressDto>? progress = null,
        CancellationToken ct = default);
}
