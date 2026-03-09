using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Sync;

public interface ICloudSyncService
{
    Task<IReadOnlyList<CloudRepositoryHeaderDto>> GetRepositoriesAsync(string accessToken, CancellationToken ct = default);

    Task<CloudSnapshotPackageDto?> GetLatestSnapshotAsync(
        string accessToken,
        int repositoryId,
        CancellationToken ct = default);

    Task<CloudPushResultDto?> PushSnapshotAsync(
        string accessToken,
        int repositoryId,
        CloudSnapshotPackageDto package,
        CancellationToken ct = default);

    Task<bool> BlockExistsAsync(string accessToken, string blockHash, CancellationToken ct = default);

    Task UploadBlockAsync(
        string accessToken,
        string blockHash,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default);

    Task<byte[]?> DownloadBlockAsync(string accessToken, string blockHash, CancellationToken ct = default);
}
