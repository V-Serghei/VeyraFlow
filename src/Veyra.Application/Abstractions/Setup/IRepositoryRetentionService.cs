using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Retention;

namespace Veyra.Application.Abstractions.Setup;

public interface IRepositoryRetentionService
{
    Task<RepositoryRetentionRunResultDto> RunRetentionAsync(
        int repositoryId,
        bool dryRun,
        RepositoryRetentionPolicyDto? policyOverride = null,
        IProgress<RepositoryRetentionProgressDto>? progress = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryRetentionRunResultDto>> RunDueRetentionAsync(
        IProgress<RepositoryRetentionProgressDto>? progress = null,
        CancellationToken ct = default);
}
