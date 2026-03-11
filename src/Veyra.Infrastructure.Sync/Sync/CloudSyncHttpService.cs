using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;

namespace Veyra.Infrastructure.Sync.Sync;

public sealed class CloudSyncHttpService : ICloudSyncService
{
    private const string SyncProtocolHeader = "X-Veyra-Sync-Protocol";
    private const string SyncProtocolVersion = "1";
    private const string IdempotencyKeyHeader = "X-Idempotency-Key";

    private readonly HttpClient _http;
    private readonly ILogger<CloudSyncHttpService> _log;
    private readonly CloudSyncFaultInjectionOptions _fault;
    private int _listCalls;
    private int _pushCalls;
    private int _uploadCalls;
    private int _downloadCalls;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CloudSyncHttpService(
        HttpClient http,
        IConfiguration configuration,
        ILogger<CloudSyncHttpService> log)
    {
        _http = http;
        _log = log;
        _fault = CloudSyncFaultInjectionOptions.FromConfiguration(configuration);

        if (_fault.Enabled)
        {
            _log.LogWarning(
                "Cloud sync fault-injection is enabled. NetworkDropEvery {NetworkDropEvery}. TimeoutEvery {TimeoutEvery}. DuplicateAckOnPush {DuplicateAck}. StaleRemoteHeadOnList {StaleRemoteHead}.",
                _fault.NetworkDropEvery,
                _fault.TimeoutEvery,
                _fault.DuplicateAckOnPush,
                _fault.StaleRemoteHeadOnList);
        }
    }

    public async Task<IReadOnlyList<CloudRepositoryHeaderDto>> GetRepositoriesAsync(string accessToken, CancellationToken ct = default)
    {
        MaybeInjectFault("list_repositories", Interlocked.Increment(ref _listCalls));

        using var req = BuildRequest(HttpMethod.Get, "/api/sync/repositories", accessToken);
        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return [];

        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<ListRepositoriesResponse>(JsonOptions, ct);
        if (payload is null || !payload.Ok || payload.Repositories is null)
            return [];

        var repositories = payload.Repositories
            .Select(r => new CloudRepositoryHeaderDto(
                r.RepositoryId,
                r.Name ?? string.Empty,
                r.Description,
                r.LatestSnapshotId,
                r.LatestSnapshotCreatedAt,
                r.LatestSnapshotTitle,
                r.LatestSnapshotTrigger,
                r.LatestSnapshotFileCount,
                r.LatestSnapshotEntryCount))
            .ToList();

        if (_fault.StaleRemoteHeadOnList)
        {
            repositories = repositories
                .Select(r => new CloudRepositoryHeaderDto(
                    r.RepositoryId,
                    r.Name,
                    r.Description,
                    r.LatestSnapshotId is > 1 ? r.LatestSnapshotId - 1 : r.LatestSnapshotId,
                    r.LatestSnapshotCreatedAtUtc,
                    r.LatestSnapshotTitle,
                    r.LatestSnapshotTrigger,
                    r.LatestSnapshotFileCount,
                    r.LatestSnapshotEntryCount))
                .ToList();
        }

        return repositories;
    }

    public async Task<CloudSnapshotPackageDto?> GetLatestSnapshotAsync(
        string accessToken,
        int repositoryId,
        CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, $"/api/sync/repositories/{repositoryId}/latest", accessToken);
        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            return null;

        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<GetLatestSnapshotResponse>(JsonOptions, ct);
        if (payload is null || !payload.Ok || payload.Repository is null || payload.Snapshot is null)
            return null;

        var entries = payload.Entries?
            .Select(e => new CloudSnapshotEntryDto(
                e.RelativePath ?? string.Empty,
                e.ParentRelativePath,
                e.Name ?? string.Empty,
                e.IsDirectory,
                e.Extension,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToList() ?? [];

        var fileVersions = payload.FileVersions?
            .Select(v => new CloudFileVersionDto(
                v.RelativePath ?? string.Empty,
                v.FileVersionId,
                v.ContentHashSha256 ?? string.Empty,
                v.SizeBytes,
                v.IsDeletionMarker,
                v.CreatedAt,
                v.Blocks?.Select(b => new CloudBlockRefDto(
                        b.Sequence,
                        b.BlockHash ?? string.Empty,
                        b.LengthBytes,
                        b.StoredSizeBytes))
                    .ToList() ?? []))
            .ToList() ?? [];

        return new CloudSnapshotPackageDto(
            new CloudRepositoryMetadataDto(
                payload.Repository.Id,
                payload.Repository.Name ?? string.Empty,
                payload.Repository.Description),
            new CloudSnapshotMetadataDto(
                payload.Snapshot.Id,
                payload.Snapshot.Title,
                payload.Snapshot.Trigger ?? string.Empty,
                payload.Snapshot.CreatedAt,
                payload.Snapshot.TotalEntries,
                payload.Snapshot.FileEntries,
                payload.Snapshot.DirectoryEntries,
                payload.Snapshot.TotalFileBytes,
                payload.Snapshot.PayloadSha256),
            entries,
            fileVersions);
    }

    public async Task<CloudPushResultDto?> PushSnapshotAsync(
        string accessToken,
        int repositoryId,
        CloudSnapshotPackageDto package,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        MaybeInjectFault("push_snapshot", Interlocked.Increment(ref _pushCalls));

        var requestPayload = new PushSnapshotRequest
        {
            Repository = new PushRepositoryMeta
            {
                Id = package.Repository.Id,
                Name = package.Repository.Name,
                Description = package.Repository.Description
            },
            Snapshot = new PushSnapshotMeta
            {
                Id = package.Snapshot.Id,
                Title = package.Snapshot.Title,
                Trigger = package.Snapshot.Trigger,
                CreatedAt = package.Snapshot.CreatedAtUtc,
                TotalEntries = package.Snapshot.TotalEntries,
                FileEntries = package.Snapshot.FileEntries,
                DirectoryEntries = package.Snapshot.DirectoryEntries,
                TotalFileBytes = package.Snapshot.TotalFileBytes,
                PayloadSha256 = package.Snapshot.PayloadSha256
            },
            Entries = package.Entries.Select(e => new PushEntry
            {
                RelativePath = e.RelativePath,
                ParentRelativePath = e.ParentRelativePath,
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Extension = e.Extension,
                SizeBytes = e.SizeBytes,
                LastWriteUtc = e.LastWriteUtc,
                ContentHashSha256 = e.ContentHashSha256
            }).ToList(),
            FileVersions = package.FileVersions.Select(v => new PushFileVersion
            {
                RelativePath = v.RelativePath,
                FileVersionId = v.FileVersionId,
                ContentHashSha256 = v.ContentHashSha256,
                SizeBytes = v.SizeBytes,
                IsDeletionMarker = v.IsDeletionMarker,
                CreatedAt = v.CreatedAtUtc,
                Blocks = v.Blocks.Select(b => new PushBlock
                {
                    Sequence = b.Sequence,
                    BlockHash = b.BlockHash,
                    LengthBytes = b.LengthBytes,
                    StoredSizeBytes = b.StoredSizeBytes
                }).ToList()
            }).ToList()
        };

        var first = await SendPushSnapshotRequestAsync(accessToken, repositoryId, requestPayload, idempotencyKey, ct);
        if (first is null)
            return null;

        if (_fault.DuplicateAckOnPush && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var replay = await SendPushSnapshotRequestAsync(accessToken, repositoryId, requestPayload, idempotencyKey, ct);
            if (replay is not null && !first.MissingBlockHashes.SequenceEqual(replay.MissingBlockHashes, StringComparer.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Fault-injection duplicate-ack replay mismatch for repository {RepositoryId}. FirstMissing {FirstCount}. ReplayMissing {ReplayCount}.",
                    repositoryId,
                    first.MissingBlockHashes.Count,
                    replay.MissingBlockHashes.Count);
            }
        }

        return first;
    }

    public async Task<bool> BlockExistsAsync(string accessToken, string blockHash, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Head, $"/api/sync/blocks/{Uri.EscapeDataString(blockHash)}", accessToken);
        using var resp = await _http.SendAsync(req, ct);

        return resp.StatusCode == HttpStatusCode.OK;
    }

    public async Task UploadBlockAsync(
        string accessToken,
        string blockHash,
        Stream content,
        long? contentLength = null,
        CancellationToken ct = default)
    {
        MaybeInjectFault("upload_block", Interlocked.Increment(ref _uploadCalls));

        using var req = BuildRequest(HttpMethod.Post, $"/api/sync/blocks/{Uri.EscapeDataString(blockHash)}", accessToken);
        req.Content = new StreamContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (contentLength is > 0)
            req.Content.Headers.ContentLength = contentLength.Value;

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<bool> DownloadBlockToFileAsync(
        string accessToken,
        string blockHash,
        string targetPath,
        CancellationToken ct = default)
    {
        MaybeInjectFault("download_block", Interlocked.Increment(ref _downloadCalls));

        using var req = BuildRequest(HttpMethod.Get, $"/api/sync/blocks/{Uri.EscapeDataString(blockHash)}", accessToken);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return false;

        resp.EnsureSuccessStatusCode();

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            await using var remoteStream = await resp.Content.ReadAsStreamAsync(ct);
            await using (var fileStream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await remoteStream.CopyToAsync(fileStream, ct);
                await fileStream.FlushAsync(ct);
            }

            File.Move(tempPath, targetPath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup of interrupted download artifacts.
                }
            }
        }
    }

    public async Task<CloudStorageMetricsDto?> GetStorageMetricsAsync(
        string accessToken,
        CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/api/admin/storage/metrics", accessToken);
        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<StorageMetricsResponse>(JsonOptions, ct);
        if (payload is null || !payload.Ok || payload.Summary is null || payload.Blocks is null || payload.Packs is null || payload.Filesystem is null)
            return null;

        return new CloudStorageMetricsDto(
            payload.Ok,
            new CloudStorageSummaryDto(
                payload.Summary.LogicalBlockCount,
                payload.Summary.LogicalBytes,
                payload.Summary.PhysicalObjectCount,
                payload.Summary.PhysicalPayloadBytes,
                payload.Summary.MissingBlockCount,
                payload.Summary.ReducedObjectCount,
                payload.Summary.ReducedObjectPercentFloor),
            new CloudStorageBlockMetricsDto(
                payload.Blocks.TotalBlocks,
                payload.Blocks.PackedBlocks,
                payload.Blocks.LooseBlocks,
                payload.Blocks.MissingBlocks,
                payload.Blocks.LogicalBytes,
                payload.Blocks.PackedBytes,
                payload.Blocks.LooseBytes,
                payload.Blocks.MissingBytes),
            new CloudStoragePackMetricsDto(
                payload.Packs.TotalPacks,
                payload.Packs.ActivePacks,
                payload.Packs.SealedPacks,
                payload.Packs.BytesWritten,
                payload.Packs.PackedBlockRefs),
            new CloudStorageFilesystemStatsDto(
                payload.Filesystem.PackFileCount,
                payload.Filesystem.LooseFileCount,
                payload.Filesystem.OtherFileCount,
                payload.Filesystem.PackFileBytes,
                payload.Filesystem.LooseFileBytes,
                payload.Filesystem.OtherFileBytes,
                payload.Filesystem.TotalPhysicalBytes));
    }

    public async Task<CloudStorageRepairResultDto?> RepairStorageAsync(
        string accessToken,
        int scanLimit = 512,
        int compactLimit = 128,
        CancellationToken ct = default)
    {
        var requestPayload = new StorageRepairRequest
        {
            ScanLimit = scanLimit,
            CompactLimit = compactLimit
        };

        using var req = BuildRequest(HttpMethod.Post, "/api/admin/storage/repair", accessToken);
        req.Content = JsonContent.Create(requestPayload);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<StorageRepairResponse>(JsonOptions, ct);
        if (payload is null || !payload.Ok || payload.Repair is null || payload.Metrics is null)
            return null;

        var metrics = payload.Metrics.Summary is not null &&
                      payload.Metrics.Blocks is not null &&
                      payload.Metrics.Packs is not null &&
                      payload.Metrics.Filesystem is not null
            ? new CloudStorageMetricsDto(
                payload.Metrics.Ok,
                new CloudStorageSummaryDto(
                    payload.Metrics.Summary.LogicalBlockCount,
                    payload.Metrics.Summary.LogicalBytes,
                    payload.Metrics.Summary.PhysicalObjectCount,
                    payload.Metrics.Summary.PhysicalPayloadBytes,
                    payload.Metrics.Summary.MissingBlockCount,
                    payload.Metrics.Summary.ReducedObjectCount,
                    payload.Metrics.Summary.ReducedObjectPercentFloor),
                new CloudStorageBlockMetricsDto(
                    payload.Metrics.Blocks.TotalBlocks,
                    payload.Metrics.Blocks.PackedBlocks,
                    payload.Metrics.Blocks.LooseBlocks,
                    payload.Metrics.Blocks.MissingBlocks,
                    payload.Metrics.Blocks.LogicalBytes,
                    payload.Metrics.Blocks.PackedBytes,
                    payload.Metrics.Blocks.LooseBytes,
                    payload.Metrics.Blocks.MissingBytes),
                new CloudStoragePackMetricsDto(
                    payload.Metrics.Packs.TotalPacks,
                    payload.Metrics.Packs.ActivePacks,
                    payload.Metrics.Packs.SealedPacks,
                    payload.Metrics.Packs.BytesWritten,
                    payload.Metrics.Packs.PackedBlockRefs),
                new CloudStorageFilesystemStatsDto(
                    payload.Metrics.Filesystem.PackFileCount,
                    payload.Metrics.Filesystem.LooseFileCount,
                    payload.Metrics.Filesystem.OtherFileCount,
                    payload.Metrics.Filesystem.PackFileBytes,
                    payload.Metrics.Filesystem.LooseFileBytes,
                    payload.Metrics.Filesystem.OtherFileBytes,
                    payload.Metrics.Filesystem.TotalPhysicalBytes))
            : null;

        if (metrics is null)
            return null;

        return new CloudStorageRepairResultDto(
            payload.Ok,
            new CloudStorageRepairStatsDto(
                payload.Repair.Scanned,
                payload.Repair.MissingMarked,
                payload.Repair.BrokenLooseRefs,
                payload.Repair.BrokenPackRefs,
                payload.Repair.Compacted),
            metrics);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, string accessToken, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(SyncProtocolHeader, SyncProtocolVersion);

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, idempotencyKey.Trim());

        return request;
    }

    private async Task<CloudPushResultDto?> SendPushSnapshotRequestAsync(
        string accessToken,
        int repositoryId,
        PushSnapshotRequest requestPayload,
        string? idempotencyKey,
        CancellationToken ct)
    {
        using var req = BuildRequest(HttpMethod.Post, $"/api/sync/repositories/{repositoryId}/snapshots", accessToken, idempotencyKey);
        req.Content = JsonContent.Create(requestPayload);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<PushSnapshotResponse>(JsonOptions, ct);
        if (payload is null)
            return null;

        return new CloudPushResultDto(payload.Ok, payload.MissingBlockHashes ?? []);
    }

    private void MaybeInjectFault(string operation, int callNumber)
    {
        if (!_fault.Enabled || callNumber <= 0)
            return;

        if (_fault.NetworkDropEvery > 0 && callNumber % _fault.NetworkDropEvery == 0)
        {
            throw new HttpRequestException(
                $"Fault injection: simulated network drop on '{operation}' call #{callNumber}.",
                inner: null,
                statusCode: HttpStatusCode.ServiceUnavailable);
        }

        if (_fault.TimeoutEvery > 0 && callNumber % _fault.TimeoutEvery == 0)
        {
            throw new TaskCanceledException(
                $"Fault injection: simulated timeout on '{operation}' call #{callNumber}.");
        }
    }

    private sealed class CloudSyncFaultInjectionOptions
    {
        public bool Enabled { get; init; }
        public int NetworkDropEvery { get; init; }
        public int TimeoutEvery { get; init; }
        public bool DuplicateAckOnPush { get; init; }
        public bool StaleRemoteHeadOnList { get; init; }

        public static CloudSyncFaultInjectionOptions FromConfiguration(IConfiguration cfg)
        {
            var enabled = cfg.GetValue<bool?>("CloudSync:FaultInjection:Enabled") ?? false;

            return new CloudSyncFaultInjectionOptions
            {
                Enabled = enabled,
                NetworkDropEvery = enabled
                    ? Math.Clamp(cfg.GetValue<int?>("CloudSync:FaultInjection:NetworkDropEvery") ?? 0, 0, 1000)
                    : 0,
                TimeoutEvery = enabled
                    ? Math.Clamp(cfg.GetValue<int?>("CloudSync:FaultInjection:TimeoutEvery") ?? 0, 0, 1000)
                    : 0,
                DuplicateAckOnPush = enabled && (cfg.GetValue<bool?>("CloudSync:FaultInjection:DuplicateAckOnPush") ?? false),
                StaleRemoteHeadOnList = enabled && (cfg.GetValue<bool?>("CloudSync:FaultInjection:StaleRemoteHeadOnList") ?? false)
            };
        }
    }

    private sealed class ListRepositoriesResponse
    {
        public bool Ok { get; init; }
        public List<ListRepositoryItem>? Repositories { get; init; }
    }

    private sealed class ListRepositoryItem
    {
        public int RepositoryId { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public long? LatestSnapshotId { get; init; }
        public DateTime? LatestSnapshotCreatedAt { get; init; }
        public string? LatestSnapshotTitle { get; init; }
        public string? LatestSnapshotTrigger { get; init; }
        public int LatestSnapshotFileCount { get; init; }
        public int LatestSnapshotEntryCount { get; init; }
    }

    private sealed class GetLatestSnapshotResponse
    {
        public bool Ok { get; init; }
        public PushRepositoryMeta? Repository { get; init; }
        public PushSnapshotMeta? Snapshot { get; init; }
        public List<PushEntry>? Entries { get; init; }
        public List<PushFileVersion>? FileVersions { get; init; }
    }

    private sealed class PushSnapshotResponse
    {
        public bool Ok { get; init; }
        public List<string>? MissingBlockHashes { get; init; }
    }

    private sealed class StorageRepairRequest
    {
        public int ScanLimit { get; init; }
        public int CompactLimit { get; init; }
    }

    private sealed class StorageMetricsResponse
    {
        public bool Ok { get; init; }
        public StorageSummaryResponse? Summary { get; init; }
        public StorageBlocksResponse? Blocks { get; init; }
        public StoragePacksResponse? Packs { get; init; }
        public StorageFilesystemResponse? Filesystem { get; init; }
    }

    private sealed class StorageRepairResponse
    {
        public bool Ok { get; init; }
        public StorageRepairStatsResponse? Repair { get; init; }
        public StorageMetricsResponse? Metrics { get; init; }
    }

    private sealed class StorageSummaryResponse
    {
        public long LogicalBlockCount { get; init; }
        public long LogicalBytes { get; init; }
        public long PhysicalObjectCount { get; init; }
        public long PhysicalPayloadBytes { get; init; }
        public long MissingBlockCount { get; init; }
        public long ReducedObjectCount { get; init; }
        public long ReducedObjectPercentFloor { get; init; }
    }

    private sealed class StorageBlocksResponse
    {
        public long TotalBlocks { get; init; }
        public long PackedBlocks { get; init; }
        public long LooseBlocks { get; init; }
        public long MissingBlocks { get; init; }
        public long LogicalBytes { get; init; }
        public long PackedBytes { get; init; }
        public long LooseBytes { get; init; }
        public long MissingBytes { get; init; }
    }

    private sealed class StoragePacksResponse
    {
        public long TotalPacks { get; init; }
        public long ActivePacks { get; init; }
        public long SealedPacks { get; init; }
        public long BytesWritten { get; init; }
        public long PackedBlockRefs { get; init; }
    }

    private sealed class StorageFilesystemResponse
    {
        public long PackFileCount { get; init; }
        public long LooseFileCount { get; init; }
        public long OtherFileCount { get; init; }
        public long PackFileBytes { get; init; }
        public long LooseFileBytes { get; init; }
        public long OtherFileBytes { get; init; }
        public long TotalPhysicalBytes { get; init; }
    }

    private sealed class StorageRepairStatsResponse
    {
        public int Scanned { get; init; }
        public int MissingMarked { get; init; }
        public int BrokenLooseRefs { get; init; }
        public int BrokenPackRefs { get; init; }
        public int Compacted { get; init; }
    }

    private sealed class PushSnapshotRequest
    {
        public PushRepositoryMeta? Repository { get; init; }
        public PushSnapshotMeta? Snapshot { get; init; }
        public List<PushEntry>? Entries { get; init; }
        public List<PushFileVersion>? FileVersions { get; init; }
    }

    private sealed class PushRepositoryMeta
    {
        public int Id { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
    }

    private sealed class PushSnapshotMeta
    {
        public long Id { get; init; }
        public string? Title { get; init; }
        public string? Trigger { get; init; }
        public DateTime CreatedAt { get; init; }
        public int TotalEntries { get; init; }
        public int FileEntries { get; init; }
        public int DirectoryEntries { get; init; }
        public long TotalFileBytes { get; init; }
        public string? PayloadSha256 { get; init; }
    }

    private sealed class PushEntry
    {
        public string? RelativePath { get; init; }
        public string? ParentRelativePath { get; init; }
        public string? Name { get; init; }
        public bool IsDirectory { get; init; }
        public string? Extension { get; init; }
        public long SizeBytes { get; init; }
        public DateTime LastWriteUtc { get; init; }
        public string? ContentHashSha256 { get; init; }
    }

    private sealed class PushFileVersion
    {
        public string? RelativePath { get; init; }
        public long FileVersionId { get; init; }
        public string? ContentHashSha256 { get; init; }
        public long SizeBytes { get; init; }
        public bool IsDeletionMarker { get; init; }
        public DateTime CreatedAt { get; init; }
        public List<PushBlock>? Blocks { get; init; }
    }

    private sealed class PushBlock
    {
        public int Sequence { get; init; }
        public string? BlockHash { get; init; }
        public int LengthBytes { get; init; }
        public long StoredSizeBytes { get; init; }
    }
}


