using System;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Security;
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
    private readonly ICloudMetadataProtectionService _metadataProtection;
    private readonly CloudSyncFaultInjectionOptions _fault;
    private int _listCalls;
    private int _pushCalls;
    private int _uploadCalls;
    private int _uploadBatchCalls;
    private int _downloadCalls;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new UtcDateTimeJsonConverter(),
            new NullableUtcDateTimeJsonConverter()
        }
    };

    public CloudSyncHttpService(
        HttpClient http,
        IConfiguration configuration,
        ICloudMetadataProtectionService metadataProtection,
        ILogger<CloudSyncHttpService> log)
    {
        _http = http;
        _log = log;
        _metadataProtection = metadataProtection;
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

        var requestTimer = Stopwatch.StartNew();
        _log.LogInformation("Cloud API request started. Operation {Operation}", "list_repositories");
        using var req = BuildRequest(HttpMethod.Get, "/api/sync/repositories", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        requestTimer.Stop();

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _log.LogWarning(
                "Cloud API request unauthorized. Operation {Operation}. StatusCode {StatusCode}. DurationMs {DurationMs}",
                "list_repositories",
                (int)resp.StatusCode,
                requestTimer.ElapsedMilliseconds);
            return [];
        }

        await EnsureSuccessAsync(resp, "list_repositories", ct);

        var parseTimer = Stopwatch.StartNew();
        var payload = await resp.Content.ReadFromJsonAsync<ListRepositoriesResponse>(JsonOptions, ct);
        parseTimer.Stop();
        if (payload is null || !payload.Ok || payload.Repositories is null)
            return [];

        var repositories = payload.Repositories
            .Select(r =>
            {
                var name = UnprotectOrDefault(r.Name, string.Empty, "repository.name");
                var description = UnprotectOrDefault(r.Description, null, "repository.description");
                var latestTitle = UnprotectOrDefault(r.LatestSnapshotTitle, null, "snapshot.title");

                return new CloudRepositoryHeaderDto(
                    r.RepositoryId,
                    name ?? string.Empty,
                    description,
                    r.LatestSnapshotId,
                    r.LatestSnapshotCreatedAt,
                    latestTitle,
                    r.LatestSnapshotTrigger,
                    r.LatestSnapshotFileCount,
                    r.LatestSnapshotEntryCount);
            })
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

        _log.LogInformation(
            "Cloud API request completed. Operation {Operation}. Repositories {Repositories}. RequestMs {RequestMs}. ParseMs {ParseMs}. TotalMs {TotalMs}",
            "list_repositories",
            repositories.Count,
            requestTimer.ElapsedMilliseconds,
            parseTimer.ElapsedMilliseconds,
            requestTimer.ElapsedMilliseconds + parseTimer.ElapsedMilliseconds);

        return repositories;
    }

    public async Task<CloudSnapshotPackageDto?> GetLatestSnapshotAsync(
        string accessToken,
        int repositoryId,
        CancellationToken ct = default)
    {
        var requestTimer = Stopwatch.StartNew();
        _log.LogInformation(
            "Cloud API request started. Operation {Operation}. RepositoryId {RepositoryId}",
            "get_latest_snapshot",
            repositoryId);
        using var req = BuildRequest(HttpMethod.Get, $"/api/sync/repositories/{repositoryId}/latest", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        requestTimer.Stop();

        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            _log.LogWarning(
                "Cloud API request returned empty result. Operation {Operation}. RepositoryId {RepositoryId}. StatusCode {StatusCode}. DurationMs {DurationMs}",
                "get_latest_snapshot",
                repositoryId,
                (int)resp.StatusCode,
                requestTimer.ElapsedMilliseconds);
            return null;
        }

        await EnsureSuccessAsync(resp, "get_latest_snapshot", ct, ("RepositoryId", repositoryId));

        var parseTimer = Stopwatch.StartNew();
        var payload = await resp.Content.ReadFromJsonAsync<GetLatestSnapshotResponse>(JsonOptions, ct);
        parseTimer.Stop();
        if (payload is null || !payload.Ok || payload.Repository is null || payload.Snapshot is null)
            return null;

        var repository = payload.Repository;
        var snapshot = payload.Snapshot;

        var metadataProtected = (
            _metadataProtection.IsProtected(repository.Name)
            || _metadataProtection.IsProtected(repository.Description)
            || _metadataProtection.IsProtected(snapshot.Title)
            || payload.Entries?.Any(e => _metadataProtection.IsProtected(e.RelativePath) || _metadataProtection.IsProtected(e.Name)) == true
            || payload.FileVersions?.Any(v => _metadataProtection.IsProtected(v.RelativePath)) == true);

        var entries = payload.Entries?
            .Select(e =>
            {
                var relativePath = UnprotectOrDefault(e.RelativePath, string.Empty, "entry.relativePath");
                var parentRelativePath = UnprotectOrDefault(e.ParentRelativePath, null, "entry.parentRelativePath");
                var name = UnprotectOrDefault(e.Name, string.Empty, "entry.name");

                return new CloudSnapshotEntryDto(
                    relativePath ?? string.Empty,
                    parentRelativePath,
                    name ?? string.Empty,
                    e.IsDirectory,
                    e.Extension,
                    e.SizeBytes,
                    e.LastWriteUtc,
                    e.ContentHashSha256);
            })
            .ToList() ?? [];

        var fileVersions = payload.FileVersions?
            .Select(v =>
            {
                var relativePath = UnprotectOrDefault(v.RelativePath, string.Empty, "fileVersion.relativePath");

                return new CloudFileVersionDto(
                    relativePath ?? string.Empty,
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
                        .ToList() ?? []);
            })
            .ToList() ?? [];

        _log.LogInformation(
            "Cloud API request completed. Operation {Operation}. RepositoryId {RepositoryId}. Entries {Entries}. FileVersions {FileVersions}. RequestMs {RequestMs}. ParseMs {ParseMs}. TotalMs {TotalMs}",
            "get_latest_snapshot",
            repositoryId,
            entries.Count,
            fileVersions.Count,
            requestTimer.ElapsedMilliseconds,
            parseTimer.ElapsedMilliseconds,
            requestTimer.ElapsedMilliseconds + parseTimer.ElapsedMilliseconds);

        return new CloudSnapshotPackageDto(
            new CloudRepositoryMetadataDto(
                repository.Id,
                UnprotectOrDefault(repository.Name, string.Empty, "repository.name") ?? string.Empty,
                UnprotectOrDefault(repository.Description, null, "repository.description")),
            new CloudSnapshotMetadataDto(
                snapshot.Id,
                UnprotectOrDefault(snapshot.Title, null, "snapshot.title"),
                snapshot.Trigger ?? string.Empty,
                snapshot.CreatedAt,
                snapshot.TotalEntries,
                snapshot.FileEntries,
                snapshot.DirectoryEntries,
                snapshot.TotalFileBytes,
                snapshot.PayloadSha256),
            entries,
            fileVersions,
            metadataProtected);
    }

    public async Task<CloudPushResultDto?> PushSnapshotAsync(
        string accessToken,
        int repositoryId,
        CloudSnapshotPackageDto package,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        MaybeInjectFault("push_snapshot", Interlocked.Increment(ref _pushCalls));
        var blockCount = package.FileVersions.Sum(v => v.Blocks.Count);

        var requestBuildTimer = Stopwatch.StartNew();
        var requestPayload = new PushSnapshotRequest
        {
            Repository = new PushRepositoryMeta
            {
                Id = package.Repository.Id,
                Name = package.MetadataProtected
                    ? ProtectRequired(package.Repository.Name)
                    : package.Repository.Name,
                Description = package.MetadataProtected
                    ? _metadataProtection.ProtectNullable(package.Repository.Description)
                    : package.Repository.Description
            },
            Snapshot = new PushSnapshotMeta
            {
                Id = package.Snapshot.Id,
                Title = package.MetadataProtected
                    ? _metadataProtection.ProtectNullable(package.Snapshot.Title)
                    : package.Snapshot.Title,
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
                RelativePath = package.MetadataProtected ? ProtectRequired(e.RelativePath) : e.RelativePath,
                ParentRelativePath = package.MetadataProtected ? _metadataProtection.ProtectNullable(e.ParentRelativePath) : e.ParentRelativePath,
                Name = package.MetadataProtected ? ProtectRequired(e.Name) : e.Name,
                IsDirectory = e.IsDirectory,
                Extension = e.Extension,
                SizeBytes = e.SizeBytes,
                LastWriteUtc = e.LastWriteUtc,
                ContentHashSha256 = e.ContentHashSha256
            }).ToList(),
            FileVersions = package.FileVersions.Select(v => new PushFileVersion
            {
                RelativePath = package.MetadataProtected ? ProtectRequired(v.RelativePath) : v.RelativePath,
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
        requestBuildTimer.Stop();

        var serializationTimer = Stopwatch.StartNew();
        var requestPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(requestPayload, JsonOptions);
        serializationTimer.Stop();

        _log.LogInformation(
            "Cloud snapshot push started. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Entries {Entries}. FileVersions {FileVersions}. Blocks {Blocks}. IdempotencyKey {IdempotencyKey}. PayloadSha {PayloadSha}. BuildMs {BuildMs}. SerializeMs {SerializeMs}. PayloadBytes {PayloadBytes}",
            repositoryId,
            package.Snapshot.Id,
            package.Entries.Count,
            package.FileVersions.Count,
            blockCount,
            idempotencyKey ?? "(none)",
            package.Snapshot.PayloadSha256 ?? "(none)",
            requestBuildTimer.ElapsedMilliseconds,
            serializationTimer.ElapsedMilliseconds,
            requestPayloadBytes.Length);

        var first = await SendPushSnapshotRequestAsync(accessToken, repositoryId, requestPayload, requestPayloadBytes, idempotencyKey, ct);
        if (first is null)
            return null;

        _log.LogInformation(
            "Cloud snapshot push acknowledged. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. MissingBlocks {MissingBlocks}",
            repositoryId,
            package.Snapshot.Id,
            first.MissingBlockHashes.Count);

        if (_fault.DuplicateAckOnPush && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var replay = await SendPushSnapshotRequestAsync(accessToken, repositoryId, requestPayload, requestPayloadBytes, idempotencyKey, ct);
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
        var uploadTimer = Stopwatch.StartNew();
        _log.LogDebug(
            "Cloud block upload started. BlockHash {BlockHash}. ContentLength {ContentLength}",
            blockHash,
            contentLength ?? -1);

        using var req = BuildRequest(HttpMethod.Post, $"/api/sync/blocks/{Uri.EscapeDataString(blockHash)}", accessToken);
        req.Content = new StreamContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (contentLength is > 0)
            req.Content.Headers.ContentLength = contentLength.Value;

        var sendTimer = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        sendTimer.Stop();
        await EnsureSuccessAsync(resp, "upload_block", ct, ("BlockHash", blockHash), ("ContentLength", contentLength ?? -1));
        uploadTimer.Stop();
        _log.LogDebug(
            "Cloud block upload completed. BlockHash {BlockHash}. ContentLength {ContentLength}. SendMs {SendMs}. TotalMs {TotalMs}",
            blockHash,
            contentLength ?? -1,
            sendTimer.ElapsedMilliseconds,
            uploadTimer.ElapsedMilliseconds);
    }

    public async Task<CloudBatchUploadResultDto?> UploadBlockBatchAsync(
        string accessToken,
        IReadOnlyList<CloudUploadBlockItemDto> blocks,
        CancellationToken ct = default)
    {
        if (blocks is null || blocks.Count == 0)
            return new CloudBatchUploadResultDto(true, 0, 0);

        MaybeInjectFault("upload_block_batch", Interlocked.Increment(ref _uploadBatchCalls));

        var totalBytes = blocks.Sum(static b => Math.Max(0, b.ContentLength));
        var uploadTimer = Stopwatch.StartNew();
        _log.LogInformation(
            "Cloud batch block upload started. Blocks {Blocks}. TotalBytes {TotalBytes}",
            blocks.Count,
            totalBytes);

        using var req = BuildRequest(HttpMethod.Post, "/api/sync/blocks/batch", accessToken);
        using var content = new MultipartFormDataContent("veyra-" + Guid.NewGuid().ToString("N"));

        foreach (var block in blocks)
        {
            var stream = new FileStream(
                block.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var part = new StreamContent(stream);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (block.ContentLength > 0)
                part.Headers.ContentLength = block.ContentLength;
            part.Headers.TryAddWithoutValidation("X-Block-Hash", block.BlockHash);
            content.Add(part, "blocks", block.BlockHash);
        }

        req.Content = content;

        var sendTimer = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        sendTimer.Stop();
        await EnsureSuccessAsync(
            resp,
            "upload_block_batch",
            ct,
            ("Blocks", blocks.Count),
            ("TotalBytes", totalBytes));

        var parseTimer = Stopwatch.StartNew();
        var payload = await resp.Content.ReadFromJsonAsync<BatchUploadBlocksResponse>(JsonOptions, ct);
        parseTimer.Stop();
        if (payload is null)
            return null;

        uploadTimer.Stop();
        _log.LogInformation(
            "Cloud batch block upload completed. Blocks {Blocks}. StoredBlocks {StoredBlocks}. SkippedBlocks {SkippedBlocks}. SendMs {SendMs}. ParseMs {ParseMs}. TotalMs {TotalMs}",
            blocks.Count,
            payload.StoredBlocks,
            payload.SkippedBlocks,
            sendTimer.ElapsedMilliseconds,
            parseTimer.ElapsedMilliseconds,
            uploadTimer.ElapsedMilliseconds);

        return new CloudBatchUploadResultDto(
            payload.Ok,
            payload.StoredBlocks,
            payload.SkippedBlocks);
    }

    public async Task<bool> DownloadBlockToFileAsync(
        string accessToken,
        string blockHash,
        string targetPath,
        CancellationToken ct = default)
    {
        MaybeInjectFault("download_block", Interlocked.Increment(ref _downloadCalls));
        var totalTimer = Stopwatch.StartNew();
        _log.LogDebug("Cloud block download started. BlockHash {BlockHash}. TargetPath {TargetPath}", blockHash, targetPath);

        using var req = BuildRequest(HttpMethod.Get, $"/api/sync/blocks/{Uri.EscapeDataString(blockHash)}", accessToken);
        var headerTimer = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        headerTimer.Stop();

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            _log.LogWarning(
                "Cloud block download returned 404. BlockHash {BlockHash}. HeaderMs {HeaderMs}",
                blockHash,
                headerTimer.ElapsedMilliseconds);
            return false;
        }

        await EnsureSuccessAsync(resp, "download_block", ct, ("BlockHash", blockHash), ("TargetPath", targetPath));

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            await using var remoteStream = await resp.Content.ReadAsStreamAsync(ct);
            var copyTimer = Stopwatch.StartNew();
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
            copyTimer.Stop();

            File.Move(tempPath, targetPath, overwrite: true);
            totalTimer.Stop();
            _log.LogDebug(
                "Cloud block download completed. BlockHash {BlockHash}. TargetPath {TargetPath}. ResponseContentLength {ContentLength}. HeaderMs {HeaderMs}. CopyMs {CopyMs}. TotalMs {TotalMs}",
                blockHash,
                targetPath,
                resp.Content.Headers.ContentLength ?? -1,
                headerTimer.ElapsedMilliseconds,
                copyTimer.ElapsedMilliseconds,
                totalTimer.ElapsedMilliseconds);
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
        var requestTimer = Stopwatch.StartNew();
        _log.LogInformation("Cloud storage metrics request started.");
        using var req = BuildRequest(HttpMethod.Get, "/api/admin/storage/metrics", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        requestTimer.Stop();

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _log.LogInformation(
                "Cloud storage metrics request requires re-authentication. DurationMs {DurationMs}",
                requestTimer.ElapsedMilliseconds);
            throw new UnauthorizedAccessException("Cloud session expired. Sign in again.");
        }

        await EnsureSuccessAsync(resp, "storage_metrics", ct);

        var parseTimer = Stopwatch.StartNew();
        var payload = await resp.Content.ReadFromJsonAsync<StorageMetricsResponse>(JsonOptions, ct);
        parseTimer.Stop();
        if (payload is null || !payload.Ok || payload.Summary is null || payload.Blocks is null || payload.Packs is null || payload.Filesystem is null)
            return null;

        var metrics = new CloudStorageMetricsDto(
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

        _log.LogInformation(
            "Cloud storage metrics request completed. LogicalBlocks {LogicalBlocks}. PhysicalObjects {PhysicalObjects}. MissingBlocks {MissingBlocks}. RequestMs {RequestMs}. ParseMs {ParseMs}. TotalMs {TotalMs}",
            metrics.Summary.LogicalBlockCount,
            metrics.Summary.PhysicalObjectCount,
            metrics.Summary.MissingBlockCount,
            requestTimer.ElapsedMilliseconds,
            parseTimer.ElapsedMilliseconds,
            requestTimer.ElapsedMilliseconds + parseTimer.ElapsedMilliseconds);

        return metrics;
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

        var serializationTimer = Stopwatch.StartNew();
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(requestPayload, JsonOptions);
        serializationTimer.Stop();

        _log.LogInformation(
            "Cloud storage repair request started. ScanLimit {ScanLimit}. CompactLimit {CompactLimit}. SerializeMs {SerializeMs}. PayloadBytes {PayloadBytes}",
            scanLimit,
            compactLimit,
            serializationTimer.ElapsedMilliseconds,
            payloadBytes.Length);

        using var req = BuildRequest(HttpMethod.Post, "/api/admin/storage/repair", accessToken);
        req.Content = new ByteArrayContent(payloadBytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName
        };

        var requestTimer = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        requestTimer.Stop();
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _log.LogInformation(
                "Cloud storage repair request requires re-authentication. DurationMs {DurationMs}",
                requestTimer.ElapsedMilliseconds);
            throw new UnauthorizedAccessException("Cloud session expired. Sign in again.");
        }

        await EnsureSuccessAsync(resp, "storage_repair", ct, ("ScanLimit", scanLimit), ("CompactLimit", compactLimit));

        var parseTimer = Stopwatch.StartNew();
        var payload = await resp.Content.ReadFromJsonAsync<StorageRepairResponse>(JsonOptions, ct);
        parseTimer.Stop();
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

        _log.LogInformation(
            "Cloud storage repair request completed. Scanned {Scanned}. MissingMarked {MissingMarked}. Compacted {Compacted}. RequestMs {RequestMs}. ParseMs {ParseMs}. TotalMs {TotalMs}",
            payload.Repair.Scanned,
            payload.Repair.MissingMarked,
            payload.Repair.Compacted,
            requestTimer.ElapsedMilliseconds,
            parseTimer.ElapsedMilliseconds,
            serializationTimer.ElapsedMilliseconds + requestTimer.ElapsedMilliseconds + parseTimer.ElapsedMilliseconds);

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
        byte[] requestPayloadBytes,
        string? idempotencyKey,
        CancellationToken ct)
    {
        using var req = BuildRequest(HttpMethod.Post, $"/api/sync/repositories/{repositoryId}/snapshots", accessToken, idempotencyKey);
        req.Content = new ByteArrayContent(requestPayloadBytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName
        };

        var requestTimer = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        requestTimer.Stop();
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _log.LogWarning(
                "Cloud snapshot push unauthorized. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. IdempotencyKey {IdempotencyKey}. PayloadBytes {PayloadBytes}. RequestMs {RequestMs}",
                repositoryId,
                requestPayload.Snapshot?.Id,
                idempotencyKey ?? "(none)",
                requestPayloadBytes.Length,
                requestTimer.ElapsedMilliseconds);
            return null;
        }

        await EnsureSuccessAsync(
            resp,
            "push_snapshot",
            ct,
            ("RepositoryId", repositoryId),
            ("SnapshotId", requestPayload.Snapshot?.Id ?? 0),
            ("Entries", requestPayload.Entries?.Count ?? 0),
            ("FileVersions", requestPayload.FileVersions?.Count ?? 0),
            ("IdempotencyKey", idempotencyKey ?? "(none)"));

        var parseTimer = Stopwatch.StartNew();
        var payload = await resp.Content.ReadFromJsonAsync<PushSnapshotResponse>(JsonOptions, ct);
        parseTimer.Stop();
        if (payload is null)
            return null;

        _log.LogInformation(
            "Cloud snapshot push request completed. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. MissingBlocks {MissingBlocks}. PayloadBytes {PayloadBytes}. RequestMs {RequestMs}. ParseMs {ParseMs}. TotalMs {TotalMs}",
            repositoryId,
            requestPayload.Snapshot?.Id ?? 0,
            payload.MissingBlockHashes?.Count ?? 0,
            requestPayloadBytes.Length,
            requestTimer.ElapsedMilliseconds,
            parseTimer.ElapsedMilliseconds,
            requestTimer.ElapsedMilliseconds + parseTimer.ElapsedMilliseconds);

        return new CloudPushResultDto(payload.Ok, payload.MissingBlockHashes ?? []);
    }

    private string ProtectRequired(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : _metadataProtection.Protect(value);
    }

    private string? UnprotectOrDefault(string? value, string? fallback, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback ?? value;

        if (!_metadataProtection.IsProtected(value))
            return value;

        if (_metadataProtection.TryUnprotect(value, out var plaintext) && !string.IsNullOrWhiteSpace(plaintext))
            return plaintext;

        _log.LogWarning("Protected cloud metadata could not be decrypted. Field {Field}", fieldName);
        return fallback;
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

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct,
        params (string Key, object? Value)[] properties)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await ReadResponseBodySafeAsync(response, ct);
        var values = properties
            .Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .Select(p => $"{p.Key}={p.Value}")
            .ToArray();
        var context = values.Length == 0 ? string.Empty : " " + string.Join(" ", values);

        _log.LogWarning(
            "Cloud API request failed. Operation {Operation}. StatusCode {StatusCode}. ReasonPhrase {ReasonPhrase}. Context {Context}. ResponseBody {ResponseBody}",
            operation,
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            context,
            body);

        throw new HttpRequestException(
            $"Cloud API {operation} failed with status {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"}). Response: {body}",
            null,
            response.StatusCode);
    }

    private static async Task<string> ReadResponseBodySafeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return "(empty)";

            return body.Length <= 4096 ? body : body[..4096];
        }
        catch (Exception ex)
        {
            return $"(failed to read response body: {ex.Message})";
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

    private sealed class BatchUploadBlocksResponse
    {
        public bool Ok { get; init; }
        public int StoredBlocks { get; init; }
        public int SkippedBlocks { get; init; }
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

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };
    }

    private sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            NormalizeUtc(reader.GetDateTime());

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(NormalizeUtc(value).ToString("O"));
    }

    private sealed class NullableUtcDateTimeJsonConverter : JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : NormalizeUtc(reader.GetDateTime());

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(NormalizeUtc(value.Value).ToString("O"));
        }
    }
}


