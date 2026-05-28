using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Common.Repository;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class RestoreRepositorySnapshotHandler(
    IRepositoryRepository repositories,
    IRepositorySnapshotRepository snapshots,
    IFileContentStore contentStore,
    IRepositoryCloudSyncOrchestrator cloudSync,
    ILogger<RestoreRepositorySnapshotHandler> log)
    : IRequestHandler<RestoreRepositorySnapshotCommand, OperationResult<RepositorySnapshotRestoreResultDto>>
{
    private const string RollbackTrigger = "repository_rollback_snapshot";

    public async Task<OperationResult<RepositorySnapshotRestoreResultDto>> Handle(
        RestoreRepositorySnapshotCommand request,
        CancellationToken ct)
    {
        try
        {
            var mode = RepositorySnapshotRestoreMode.Normalize(request.Mode);
            var repo = await repositories.GetRepositoryByIdAsync(request.RepositoryId, ct);
            if (repo is null || repo.IsDeleted)
                return OperationResult<RepositorySnapshotRestoreResultDto>.Fail("Repository was not found.");

            var restoreData = await snapshots.GetSnapshotRestoreDataAsync(request.RepositoryId, request.SnapshotId, ct);
            if (restoreData is null)
                return OperationResult<RepositorySnapshotRestoreResultDto>.Fail("Snapshot was not found.");

            var versionsToRestore = restoreData.FileVersions
                .Where(static version => !version.IsDeletionMarker)
                .Where(static version => !RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(version.RelativePath))
                .OrderBy(static version => version.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var missing = await FindMissingBlocksAsync(versionsToRestore, ct);
            if (missing.Count > 0)
            {
                var preview = string.Join(", ", missing.Take(6));
                return OperationResult<RepositorySnapshotRestoreResultDto>.Fail(
                    $"Cannot restore snapshot because {missing.Count} content block(s) are missing locally. Missing blocks: {preview}");
            }

            Directory.CreateDirectory(repo.DirectoryPath);

            if (mode == RepositorySnapshotRestoreMode.Copies)
                return await RestoreAsCopiesAsync(repo.DirectoryPath, request.RepositoryId, request.SnapshotId, restoreData, versionsToRestore, ct);

            var entriesToRestore = restoreData.Entries
                .Where(static entry => !RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(entry.RelativePath))
                .ToList();

            return await RollbackAsync(repo.DirectoryPath, request.RepositoryId, request.SnapshotId, entriesToRestore, versionsToRestore, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Failed to restore repository snapshot. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Mode {Mode}",
                request.RepositoryId,
                request.SnapshotId,
                request.Mode);

            return OperationResult<RepositorySnapshotRestoreResultDto>.Fail(ex.Message);
        }
    }

    private async Task<OperationResult<RepositorySnapshotRestoreResultDto>> RollbackAsync(
        string repositoryRoot,
        int repositoryId,
        long snapshotId,
        IReadOnlyList<RepositoryScanEntryDto> entriesToRestore,
        IReadOnlyList<FileVersionRestoreDto> versionsToRestore,
        CancellationToken ct)
    {
        var targetPaths = versionsToRestore
            .Select(static version => NormalizeRelativePath(version.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetDirectories = entriesToRestore
            .Where(static entry => entry.IsDirectory)
            .Select(static entry => NormalizeRelativePath(entry.RelativePath))
            .Concat(versionsToRestore.SelectMany(static version => EnumerateParentDirectories(version.RelativePath)))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var latestEntries = await snapshots.GetLatestEntriesAsync(repositoryId, ct);
        var extraCurrentFiles = latestEntries
            .Where(static entry => !entry.IsDirectory)
            .Select(static entry => NormalizeRelativePath(entry.RelativePath))
            .Where(static path => !RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(path))
            .Where(path => !targetPaths.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var extraCurrentDirectories = latestEntries
            .Where(static entry => entry.IsDirectory)
            .Select(static entry => NormalizeRelativePath(entry.RelativePath))
            .Where(static path => !RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(path))
            .Where(path => !targetDirectories.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path.Count(static ch => ch == '/'))
            .ThenBy(static path => path.Length)
            .ToList();

        var backupRoot = extraCurrentFiles.Count == 0 && extraCurrentDirectories.Count == 0
            ? null
            : BuildUniqueDirectory(
                Path.Combine(
                    repositoryRoot,
                    "..",
                    ".veyra-rollback-backups",
                    $"repository-{repositoryId}",
                    $"snapshot-{snapshotId}-{DateTime.UtcNow:yyyyMMddHHmmss}"));

        var backedUp = 0;
        if (backupRoot is not null)
        {
            Directory.CreateDirectory(backupRoot);
            foreach (var relativePath in extraCurrentDirectories)
            {
                ct.ThrowIfCancellationRequested();

                var source = ToAbsolutePath(repositoryRoot, relativePath);
                if (!Directory.Exists(source))
                    continue;

                var target = ToAbsolutePath(backupRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(source, target);
                backedUp++;
            }

            foreach (var relativePath in extraCurrentFiles)
            {
                ct.ThrowIfCancellationRequested();

                var source = ToAbsolutePath(repositoryRoot, relativePath);
                if (!File.Exists(source))
                    continue;

                var target = ToAbsolutePath(backupRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target, overwrite: false);
                backedUp++;
            }
        }

        CreateSnapshotDirectories(repositoryRoot, entriesToRestore);

        var overwritten = 0;
        foreach (var version in versionsToRestore)
        {
            ct.ThrowIfCancellationRequested();

            var targetPath = ToAbsolutePath(repositoryRoot, version.RelativePath);
            if (File.Exists(targetPath))
                overwritten++;

            await contentStore.RestoreFileAsync(
                version.Blocks,
                targetPath,
                overwriteExisting: true,
                version.ContentHashSha256,
                ct);
        }

        var saveResult = await snapshots.SaveSnapshotAsync(
            repositoryId,
            RollbackTrigger,
            DateTime.UtcNow,
            entriesToRestore,
            saveFileVersions: true,
            snapshotTitle: $"Rollback to snapshot {snapshotId}",
            snapshotTags: ["rollback"],
            ct: ct,
            forceSnapshotCreation: true);

        if (saveResult.SnapshotCreated)
            await cloudSync.TryPushLatestSnapshotAsync(repositoryId, ct);

        log.LogInformation(
            "Repository rollback completed. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Restored {Restored}. Overwritten {Overwritten}. BackedUp {BackedUp}. CreatedSnapshot {CreatedSnapshot}",
            repositoryId,
            snapshotId,
            versionsToRestore.Count,
            overwritten,
            backedUp,
            saveResult.SnapshotCreated);

        var result = new RepositorySnapshotRestoreResultDto(
            repositoryId,
            snapshotId,
            RepositorySnapshotRestoreMode.Rollback,
            versionsToRestore.Count,
            overwritten,
            backedUp,
            saveResult.SnapshotCreated,
            backupRoot,
            null);

        return OperationResult<RepositorySnapshotRestoreResultDto>.Ok(result);
    }

    private async Task<OperationResult<RepositorySnapshotRestoreResultDto>> RestoreAsCopiesAsync(
        string repositoryRoot,
        int repositoryId,
        long snapshotId,
        RepositorySnapshotRestoreDataDto restoreData,
        IReadOnlyList<FileVersionRestoreDto> versionsToRestore,
        CancellationToken ct)
    {
        var restoreRoot = BuildUniqueDirectory(Path.Combine(repositoryRoot, ".veyra-restores", $"snapshot-{snapshotId}"));
        Directory.CreateDirectory(restoreRoot);
        var entriesToRestore = restoreData.Entries
            .Where(static entry => !RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(entry.RelativePath))
            .ToList();
        CreateSnapshotDirectories(restoreRoot, entriesToRestore);

        foreach (var version in versionsToRestore)
        {
            ct.ThrowIfCancellationRequested();

            var targetPath = ToAbsolutePath(restoreRoot, version.RelativePath);
            await contentStore.RestoreFileAsync(
                version.Blocks,
                targetPath,
                overwriteExisting: false,
                version.ContentHashSha256,
                ct);
        }

        log.LogInformation(
            "Repository snapshot restored as copies. RepositoryId {RepositoryId}. SnapshotId {SnapshotId}. Files {Files}. RestoreRoot {RestoreRoot}",
            repositoryId,
            snapshotId,
            versionsToRestore.Count,
            restoreRoot);

        var result = new RepositorySnapshotRestoreResultDto(
            repositoryId,
            snapshotId,
            RepositorySnapshotRestoreMode.Copies,
            versionsToRestore.Count,
            OverwrittenFilesCount: 0,
            BackedUpFilesCount: 0,
            CreatedSnapshot: false,
            BackupDirectoryPath: null,
            RestoreDirectoryPath: restoreRoot);

        return OperationResult<RepositorySnapshotRestoreResultDto>.Ok(result);
    }

    private async Task<IReadOnlyList<string>> FindMissingBlocksAsync(
        IReadOnlyList<FileVersionRestoreDto> versions,
        CancellationToken ct)
    {
        foreach (var version in versions)
        {
            if (version.SizeBytes > 0 && version.Blocks.Count == 0)
                return [$"{version.RelativePath}: no blocks"];
        }

        var blockKeys = versions
            .SelectMany(static version => version.Blocks)
            .Select(static block => block.BlockStorageKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await contentStore.FindMissingBlocksAsync(blockKeys, ct);
    }

    private static void CreateSnapshotDirectories(string repositoryRoot, IReadOnlyList<RepositoryScanEntryDto> entries)
    {
        foreach (var entry in entries.Where(static entry => entry.IsDirectory))
        {
            var path = ToAbsolutePath(repositoryRoot, entry.RelativePath);
            Directory.CreateDirectory(path);
        }
    }

    private static string BuildUniqueDirectory(string preferredPath)
    {
        var full = Path.GetFullPath(preferredPath);
        if (!Directory.Exists(full) && !File.Exists(full))
            return full;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{full}-{i}";
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }

        return $"{full}-{Guid.NewGuid():N}";
    }

    private static string NormalizeRelativePath(string value)
        => value.Trim().Replace('\\', '/').Trim('/');

    private static IEnumerable<string> EnumerateParentDirectories(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var lastSlash = normalized.LastIndexOf('/');
        while (lastSlash > 0)
        {
            normalized = normalized[..lastSlash];
            yield return normalized;
            lastSlash = normalized.LastIndexOf('/');
        }
    }

    private static string ToAbsolutePath(string rootPath, string relativePath)
    {
        var rel = NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(rootPath, rel);
    }
}
