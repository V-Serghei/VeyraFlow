using Veyra.Application.DTOs;
using Veyra.Application.DTOs.CloudSync;

namespace Veyra.Application.Abstractions.Sync;

public interface ICloudRepositoryManagementService
{
    Task<CloudRepositoryManagerOverviewDto> GetOverviewAsync(CancellationToken ct = default);

    Task<CloudRepositoryRestorePlanDto> BuildRestorePlanAsync(
        CloudRepositoryRestoreOptionsDto options,
        CancellationToken ct = default);

    Task<CloudRepositoryQueuedOperationDto> QueueRestoreAsync(
        CloudRepositoryRestoreOptionsDto options,
        CancellationToken ct = default);

    Task<CloudRepositoryQueuedOperationDto> QueueSyncNowAsync(
        int repositoryId,
        CancellationToken ct = default);

    Task<CloudRepositoryQueuedOperationDto> QueueCompareWithLocalAsync(
        int cloudRepositoryId,
        string? localPath,
        CancellationToken ct = default);

    Task<CloudRepositoryQueuedOperationDto> QueueDeleteCloudRepositoryAsync(
        int cloudRepositoryId,
        string confirmationText,
        CancellationToken ct = default);

    Task<CloudRepositoryQueuedOperationDto> QueueRemoveCloudOnlyHistoryAsync(
        int cloudRepositoryId,
        string confirmationText,
        CancellationToken ct = default);
}
