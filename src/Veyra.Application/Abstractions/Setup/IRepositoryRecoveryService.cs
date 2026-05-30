using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Recovery;

namespace Veyra.Application.Abstractions.Setup;

public interface IRepositoryRecoveryService
{
    Task<RepositoryRecoveryResultDto> RepairRepositoryAsync(
        int repositoryId,
        bool repairMissingBlocksFromCloud = true,
        CancellationToken ct = default);

    Task<RepositoryRecoveryResultDto> ReindexRepositoryAsync(
        int repositoryId,
        CancellationToken ct = default);

    Task<RepositoryRecoveryResultDto> RelinkRepositoryAsync(
        int repositoryId,
        CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryRecoveryResultDto>> RunStartupHealthCheckAsync(
        CancellationToken ct = default);
}
