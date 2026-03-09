using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Desktop.Services.Sync;

public sealed class RepositoryCloudSyncOrchestrator(
    VeyraDbContext db,
    ICloudSyncService cloudSync,
    IUserProfileRepository userProfiles,
    IRepositoryRepository repositories,
    IFileContentStore fileContentStore,
    IMediator mediator,
    IConfiguration configuration,
    ILogger<RepositoryCloudSyncOrchestrator> log)
    : IRepositoryCloudSyncOrchestrator
{
    private const string ManagedHashPrefix = "sha256-";

    public async Task TryPushLatestSnapshotAsync(int repositoryId, CancellationToken ct = default)
    {
        var profile = await userProfiles.GetActiveProfileAsync(ct);
        if (profile is null || string.IsNullOrWhiteSpace(profile.AccessToken))
            return;

        var repository = await db.Repositories
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repository is null)
            return;

        var snapshot = await db.RepositorySnapshots
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.SnapshotFileLinks.Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (snapshot is null)
            return;

        var entries = await db.RepositorySnapshotEntries
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == snapshot.Id && !e.IsDeleted)
            .OrderBy(e => e.RelativePath)
            .Select(e => new CloudSnapshotEntryDto(
                e.RelativePath,
                e.ParentRelativePath,
                e.Name,
                e.IsDirectory,
                e.Extension,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToListAsync(ct);

        var links = await db.SnapshotFileLinks
            .Where(l => l.SnapshotId == snapshot.Id && !l.IsDeleted)
            .Include(l => l.FileIdentity)
            .Include(l => l.FileVersion)
                .ThenInclude(v => v.Blocks)
            .OrderBy(l => l.FileIdentity.RelativePath)
            .ToListAsync(ct);

        if (links.Count == 0)
        {
            log.LogDebug(
                "Cloud push skipped: snapshot has no file links. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}",
                repositoryId,
                snapshot.Id);
            return;
        }

        var fileVersions = links
            .Select(link => new CloudFileVersionDto(
                link.FileIdentity.RelativePath,
                link.FileVersionId,
                link.FileVersion.ContentHashSha256,
                link.FileVersion.SizeBytes,
                link.FileVersion.IsDeletionMarker,
                link.FileVersion.CreatedAt,
                link.FileVersion.Blocks
                    .Where(b => !b.IsDeleted)
                    .OrderBy(b => b.Sequence)
                    .Select(b => new CloudBlockRefDto(
                        b.Sequence,
                        b.BlockHashBlake3,
                        b.LengthBytes,
                        b.StoredSizeBytes))
                    .ToList()))
            .ToList();

        var package = new CloudSnapshotPackageDto(
            new CloudRepositoryMetadataDto(repository.Id, repository.Name, repository.Description),
            new CloudSnapshotMetadataDto(
                snapshot.Id,
                snapshot.Title,
                snapshot.Trigger,
                snapshot.CreatedAt,
                snapshot.TotalEntries,
                snapshot.FileEntries,
                snapshot.DirectoryEntries,
                snapshot.TotalFileBytes,
                BuildPayloadSha(snapshot, entries.Count, fileVersions.Count)),
            entries,
            fileVersions);

        var pushResult = await cloudSync.PushSnapshotAsync(
            profile.AccessToken,
            repository.Id,
            package,
            ct);

        if (pushResult is null)
            return;

        if (pushResult.MissingBlockHashes.Count == 0)
            return;

        var uploaded = 0;

        foreach (var missingHash in pushResult.MissingBlockHashes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await TryUploadMissingBlockAsync(profile.AccessToken, missingHash, ct))
                uploaded++;
        }

        if (uploaded > 0)
        {
            await cloudSync.PushSnapshotAsync(
                profile.AccessToken,
                repository.Id,
                package,
                ct);
        }

        log.LogInformation(
            "Cloud push finished for repository {RepositoryId}. SnapshotId {SnapshotId}. Missing {Missing}. Uploaded {Uploaded}",
            repository.Id,
            snapshot.Id,
            pushResult.MissingBlockHashes.Count,
            uploaded);
    }

    public async Task<int> RestoreRepositoriesFromCloudAsync(CancellationToken ct = default)
    {
        var profile = await userProfiles.GetActiveProfileAsync(ct);
        if (profile is null || string.IsNullOrWhiteSpace(profile.AccessToken))
            return 0;

        var remoteRepositories = await cloudSync.GetRepositoriesAsync(profile.AccessToken, ct);
        if (remoteRepositories.Count == 0)
            return 0;

        var localRepositories = await repositories.GetAllRepositoriesAsync(ct);
        var localNames = localRepositories
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var restoredCount = 0;

        foreach (var remote in remoteRepositories)
        {
            if (string.IsNullOrWhiteSpace(remote.Name))
                continue;

            if (localNames.Contains(remote.Name))
                continue;

            var package = await cloudSync.GetLatestSnapshotAsync(profile.AccessToken, remote.RepositoryId, ct);
            if (package is null)
                continue;

            var restorePath = BuildRestorePath(profile.Username, remote.Name);
            Directory.CreateDirectory(restorePath);

            await RestoreFilesFromPackageAsync(profile.AccessToken, package, restorePath, ct);

            var formats = package.Entries
                .Where(e => !e.IsDirectory)
                .Select(e => NormalizeFormat(e.Extension))
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (formats.Count == 0)
            {
                formats = [".txt"];
            }

            var createResult = await mediator.Send(
                new CreateRepositoryWithFormatsCommand(
                    package.Repository.Name,
                    package.Repository.Description,
                    restorePath,
                    formats),
                ct);

            if (!createResult.Success || createResult.Value <= 0)
            {
                log.LogWarning(
                    "Cloud restore created files but repository bootstrap failed. Name {Name}. Path {Path}. Error {Error}",
                    package.Repository.Name,
                    restorePath,
                    createResult.Error ?? "(none)");
                continue;
            }

            await mediator.Send(
                new ScanRepositoryCommand(
                    createResult.Value,
                    Progress: null,
                    new RepositoryScanOptionsDto(
                        SaveFileVersions: true,
                        TriggerOverride: "cloud_restore_sync",
                        SnapshotTitle: $"cloud_restore_{DateTime.Now:yyyyMMdd_HHmmss}")),
                ct);

            localNames.Add(remote.Name);
            restoredCount++;
        }

        return restoredCount;
    }

    private async Task RestoreFilesFromPackageAsync(
        string accessToken,
        CloudSnapshotPackageDto package,
        string restoreRoot,
        CancellationToken ct)
    {
        foreach (var entry in package.Entries.Where(e => e.IsDirectory))
        {
            var dirPath = ResolvePath(restoreRoot, entry.RelativePath);
            if (dirPath is null)
                continue;

            Directory.CreateDirectory(dirPath);
        }

        var latestByPath = package.FileVersions
            .GroupBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(v => v.CreatedAtUtc)
                .ThenByDescending(v => v.FileVersionId)
                .First())
            .ToList();

        foreach (var version in latestByPath)
        {
            var targetPath = ResolvePath(restoreRoot, version.RelativePath);
            if (targetPath is null)
                continue;

            if (version.IsDeletionMarker)
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                continue;
            }

            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            var blocks = new List<StoredFileBlockDto>(version.Blocks.Count);
            var hasMissingBlock = false;

            foreach (var block in version.Blocks.OrderBy(b => b.Sequence))
            {
                var localOk = await EnsureLocalBlockAsync(accessToken, block.BlockHash, ct);
                if (!localOk)
                {
                    hasMissingBlock = true;
                    log.LogWarning(
                        "Missing block during cloud restore. Repo {Repository}. File {File}. Block {BlockHash}",
                        package.Repository.Name,
                        version.RelativePath,
                        block.BlockHash);
                    break;
                }

                blocks.Add(new StoredFileBlockDto(
                    block.Sequence,
                    block.BlockHash,
                    block.LengthBytes,
                    block.StoredSizeBytes));
            }

            if (hasMissingBlock)
                continue;

            await fileContentStore.RestoreFileAsync(
                blocks,
                targetPath,
                overwriteExisting: true,
                ct);
        }
    }

    private async Task<bool> EnsureLocalBlockAsync(string accessToken, string blockHash, CancellationToken ct)
    {
        var localPath = ResolveLocalBlockPath(blockHash);
        if (string.IsNullOrWhiteSpace(localPath))
            return false;

        if (File.Exists(localPath))
            return true;

        var remote = await cloudSync.DownloadBlockAsync(accessToken, blockHash, ct);
        if (remote is null)
            return false;

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(localPath, remote, ct);
        return true;
    }

    private async Task<bool> TryUploadMissingBlockAsync(string accessToken, string blockHash, CancellationToken ct)
    {
        var path = ResolveLocalBlockPath(blockHash);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var bytes = await File.ReadAllBytesAsync(path, ct);
        await cloudSync.UploadBlockAsync(accessToken, blockHash, bytes, ct);
        return true;
    }

    private string? ResolveLocalBlockPath(string blockHash)
    {
        var root = ResolveBlockStoreRoot();

        if (blockHash.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var hash = blockHash[ManagedHashPrefix.Length..].Trim().ToLowerInvariant();
            if (hash.Length < 4)
                return null;

            var p1 = hash[..2];
            var p2 = hash[2..4];
            return Path.Combine(root, "managed", "blocks", p1, p2, hash + ".bin");
        }

        var safe = blockHash.Trim().ToLowerInvariant()
            .Replace('/', '_')
            .Replace('\\', '_');

        if (safe.Length < 4)
            return null;

        var candidate1 = Path.Combine(root, "managed", "blocks", safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(candidate1))
            return candidate1;

        var candidate2 = Path.Combine(root, safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(candidate2))
            return candidate2;

        return candidate1;
    }

    private string ResolveBlockStoreRoot()
    {
        var fromCfg = configuration["Storage:BlockStorePath"];
        var fromEnv = Environment.GetEnvironmentVariable("VEYRA_BLOCK_STORE");

        var root = !string.IsNullOrWhiteSpace(fromCfg)
            ? fromCfg.Trim()
            : !string.IsNullOrWhiteSpace(fromEnv)
                ? fromEnv.Trim()
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyraFlow",
                    "block-store");

        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static string BuildPayloadSha(RepositorySnapshot snapshot, int entries, int fileVersions)
    {
        var raw = $"{snapshot.Id}|{snapshot.CreatedAt:O}|{snapshot.Trigger}|{entries}|{fileVersions}|{snapshot.TotalFileBytes}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildRestorePath(string username, string repositoryName)
    {
        var safeRepo = SanitizeSegment(repositoryName);
        var safeUser = SanitizeSegment(username);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow",
            "cloud-restored",
            safeUser,
            safeRepo);
    }

    private static string SanitizeSegment(string value)
    {
        var cleaned = new string(value
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? "repository" : cleaned;
    }

    private static string NormalizeFormat(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var value = extension.Trim();
        if (!value.StartsWith('.'))
            value = "." + value;

        return value.ToLowerInvariant();
    }

    private static string? ResolvePath(string root, string relativePath)
    {
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            return null;

        return full;
    }
}



