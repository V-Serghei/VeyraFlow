using System;
using System.IO;
using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Sync;

public interface ICloudSyncService
{
    Task<IReadOnlyList<CloudRepositoryHeaderDto>> GetRepositoriesAsync(string accessToken, CancellationToken ct = default);

    Task<CloudSnapshotPackageDto?> GetLatestSnapshotAsync(
        string accessToken,
        int repositoryId,
        CancellationToken ct = default);

    Task<IReadOnlyList<CloudSnapshotPackageDto>> GetRepositorySnapshotsAsync(
        string accessToken,
        int repositoryId,
        CancellationToken ct = default);

    Task<CloudPushResultDto?> PushSnapshotAsync(
        string accessToken,
        int repositoryId,
        CloudSnapshotPackageDto package,
        string? idempotencyKey = null,
        CancellationToken ct = default);

    Task<bool> BlockExistsAsync(string accessToken, string blockHash, CancellationToken ct = default);

    Task UploadBlockAsync(
        string accessToken,
        string blockHash,
        Stream content,
        long? contentLength = null,
        CancellationToken ct = default);

    Task<CloudBatchUploadResultDto?> UploadBlockBatchAsync(
        string accessToken,
        IReadOnlyList<CloudUploadBlockItemDto> blocks,
        CancellationToken ct = default)
        => throw new NotSupportedException("Batch block upload is not supported by this cloud sync provider.");

    Task<bool> DownloadBlockToFileAsync(
        string accessToken,
        string blockHash,
        string targetPath,
        CancellationToken ct = default);

    Task<CloudStorageMetricsDto?> GetStorageMetricsAsync(
        string accessToken,
        CancellationToken ct = default);

    Task<CloudStorageRepairResultDto?> RepairStorageAsync(
        string accessToken,
        int scanLimit = 512,
        int compactLimit = 128,
        CancellationToken ct = default);

    Task<bool> DeleteRepositoryAsync(
        string accessToken,
        int repositoryId,
        CancellationToken ct = default)
        => throw new NotSupportedException("Cloud repository deletion is not supported by this cloud sync provider.");
}
