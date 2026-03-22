
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositoryBundleService(
    VeyraDbContext db,
    IConfiguration configuration,
    ILogger<EfRepositoryBundleService> log)
    : IRepositoryBundleService
{
    private const int CurrentBundleFormatVersion = 1;
    private const string ManifestEntryName = "manifest.json";
    private const int MaxNameLength = 256;
    private const int ImportBatchSize = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<RepositoryBundleValidationResultDto> ValidateBundleAsync(
        string bundlePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            return new RepositoryBundleValidationResultDto(
                IsValid: false,
                BundleFormatVersion: 0,
                Message: "Bundle path is empty.",
                Warnings: []);
        }

        var fullPath = Path.GetFullPath(bundlePath);
        if (!File.Exists(fullPath))
        {
            return new RepositoryBundleValidationResultDto(
                IsValid: false,
                BundleFormatVersion: 0,
                Message: "Bundle file does not exist.",
                Warnings: []);
        }

        try
        {
            using var zip = ZipFile.OpenRead(fullPath);
            var manifest = await ReadManifestAsync(zip, ct);
            if (manifest is null)
            {
                return new RepositoryBundleValidationResultDto(
                    IsValid: false,
                    BundleFormatVersion: 0,
                    Message: "manifest.json not found or invalid.",
                    Warnings: []);
            }

            var warnings = new List<string>();
            var isValid = ValidateManifestShape(manifest, warnings, out var message);

            if (manifest.BundleFormatVersion > CurrentBundleFormatVersion)
            {
                isValid = false;
                message = $"Bundle format version {manifest.BundleFormatVersion} is newer than supported version {CurrentBundleFormatVersion}.";
            }

            return new RepositoryBundleValidationResultDto(
                IsValid: isValid,
                BundleFormatVersion: manifest.BundleFormatVersion,
                Message: message,
                Warnings: warnings);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Bundle validation failed for {Path}", fullPath);
            return new RepositoryBundleValidationResultDto(
                IsValid: false,
                BundleFormatVersion: 0,
                Message: ex.Message,
                Warnings: []);
        }
    }

    public async Task<RepositoryBundleExportResultDto> ExportRepositoryAsync(
        int repositoryId,
        string bundlePath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var repository = await db.Set<Repository>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found.");

        var snapshots = await db.Set<RepositorySnapshot>()
            .AsNoTracking()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new BundleSnapshotInfo
            {
                Id = s.Id,
                CreatedAtUtc = s.CreatedAt,
                Trigger = s.Trigger,
                Title = s.Title,
                TotalEntries = s.TotalEntries,
                FileEntries = s.FileEntries,
                DirectoryEntries = s.DirectoryEntries,
                TotalFileBytes = s.TotalFileBytes
            })
            .ToListAsync(ct);

        var snapshotEntries = await db.Set<RepositorySnapshotEntry>()
            .AsNoTracking()
            .Where(e => e.RepositoryId == repositoryId && !e.IsDeleted)
            .OrderBy(e => e.SnapshotId)
            .ThenBy(e => e.RelativePath)
            .Select(e => new BundleSnapshotEntryInfo
            {
                Id = e.Id,
                SnapshotId = e.SnapshotId,
                RelativePath = e.RelativePath,
                ParentRelativePath = e.ParentRelativePath,
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Extension = e.Extension,
                SizeBytes = e.SizeBytes,
                LastWriteUtc = e.LastWriteUtc,
                ContentHashSha256 = e.ContentHashSha256,
                CreatedAtUtc = e.CreatedAt
            })
            .ToListAsync(ct);

        var fileIdentities = await db.Set<FileIdentity>()
            .AsNoTracking()
            .Where(i => i.RepositoryId == repositoryId && !i.IsDeleted)
            .OrderBy(i => i.RelativePath)
            .ThenBy(i => i.Id)
            .Select(i => new BundleFileIdentityInfo
            {
                Id = i.Id,
                RelativePath = i.RelativePath,
                Name = i.Name,
                Extension = i.Extension,
                CreatedAtUtc = i.CreatedAt,
                UpdatedAtUtc = i.UpdatedAt
            })
            .ToListAsync(ct);

        var fileVersions = await db.Set<FileVersion>()
            .AsNoTracking()
            .Where(v => !v.IsDeleted && v.FileIdentity.RepositoryId == repositoryId)
            .OrderBy(v => v.FileIdentityId)
            .ThenBy(v => v.CreatedAt)
            .ThenBy(v => v.Id)
            .Select(v => new BundleFileVersionInfo
            {
                Id = v.Id,
                FileIdentityId = v.FileIdentityId,
                ContentHashSha256 = v.ContentHashSha256,
                SizeBytes = v.SizeBytes,
                LastWriteUtc = v.LastWriteUtc,
                IsDeletionMarker = v.IsDeletionMarker,
                CreatedAtUtc = v.CreatedAt
            })
            .ToListAsync(ct);

        var fileVersionIds = fileVersions
            .Select(v => v.Id)
            .ToList();

        var fileVersionBlocks = await LoadFileVersionBlocksAsync(fileVersionIds, ct);

        var snapshotIds = snapshots
            .Select(s => s.Id)
            .ToList();

        var snapshotFileLinks = await LoadSnapshotFileLinksAsync(snapshotIds, ct);

        var textDiffs = await LoadTextDiffsAsync(fileVersionIds, ct);

        var diffIds = textDiffs.Select(d => d.Id).ToHashSet();

        var textDiffHunks = await LoadTextDiffHunksAsync(diffIds, ct);

        var textDiffLines = await LoadTextDiffLinesAsync(diffIds, ct);

        var textLineAtomIds = textDiffLines
            .Select(l => l.TextLineAtomId)
            .Distinct()
            .ToList();

        var textLineAtoms = await LoadTextLineAtomsAsync(textLineAtomIds, ct);

        var manifest = new RepositoryBundleManifest
        {
            BundleFormatVersion = CurrentBundleFormatVersion,
            CreatedBy = "veyra-desktop",
            SourceProductVersion = typeof(EfRepositoryBundleService).Assembly.GetName().Version?.ToString() ?? "unknown",
            ExportedAtUtc = DateTime.UtcNow,
            Repository = new BundleRepositoryInfo
            {
                Name = repository.Name,
                Description = repository.Description,
                FileCount = repository.FileCount,
                VersionCount = repository.VersionCount,
                TotalSizeBytes = repository.TotalSizeBytes,
                LastScannedAtUtc = repository.LastScannedAt,
                AutoCaptureFileVersions = repository.AutoCaptureFileVersions,
                ProtectCloudMetadata = repository.ProtectCloudMetadata
            },
            Snapshots = snapshots,
            SnapshotEntries = snapshotEntries,
            FileIdentities = fileIdentities,
            FileVersions = fileVersions,
            FileVersionBlocks = fileVersionBlocks,
            SnapshotFileLinks = snapshotFileLinks,
            TextDiffs = textDiffs,
            TextDiffHunks = textDiffHunks,
            TextDiffLines = textDiffLines,
            TextLineAtoms = textLineAtoms,
            Stats = new BundleStatsInfo
            {
                SnapshotCount = snapshots.Count,
                SnapshotEntryCount = snapshotEntries.Count,
                FileIdentityCount = fileIdentities.Count,
                FileVersionCount = fileVersions.Count,
                FileVersionBlockCount = fileVersionBlocks.Count,
                SnapshotFileLinkCount = snapshotFileLinks.Count,
                TextDiffCount = textDiffs.Count,
                TextDiffHunkCount = textDiffHunks.Count,
                TextDiffLineCount = textDiffLines.Count,
                TextLineAtomCount = textLineAtoms.Count,
                BlockArtifactCount = 0
            }
        };

        var fullBundlePath = Path.GetFullPath(bundlePath);
        var bundleDirectory = Path.GetDirectoryName(fullBundlePath);
        if (!string.IsNullOrWhiteSpace(bundleDirectory))
            Directory.CreateDirectory(bundleDirectory);

        if (File.Exists(fullBundlePath))
            File.Delete(fullBundlePath);

        var storeRoot = RepositoryBundleBlockPathResolver.ResolveStoreRoot(configuration);
        var uniqueBlockHashes = fileVersionBlocks
            .Select(b => b.BlockStorageKey)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missingBlocks = 0;

        await using (var output = new FileStream(
                         fullBundlePath,
                         FileMode.Create,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         bufferSize: 128 * 1024,
                         useAsync: true))
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);

            var artifactIndex = 0;
            foreach (var blockHash in uniqueBlockHashes)
            {
                ct.ThrowIfCancellationRequested();

                var blockPath = RepositoryBundleBlockPathResolver.ResolveLocalBlockPath(storeRoot, blockHash);
                if (string.IsNullOrWhiteSpace(blockPath) || !File.Exists(blockPath))
                {
                    missingBlocks++;
                    continue;
                }

                var entryName = $"blocks/{artifactIndex:D8}.bin";
                artifactIndex++;

                var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var blockStream = new FileStream(
                    blockPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 128 * 1024,
                    useAsync: true);

                await blockStream.CopyToAsync(entryStream, 128 * 1024, ct);

                manifest.BlockArtifacts.Add(new BundleBlockArtifactInfo
                {
                    Hash = blockHash,
                    EntryName = entryName,
                    SizeBytes = new FileInfo(blockPath).Length
                });
            }

            manifest.Stats.BlockArtifactCount = manifest.BlockArtifacts.Count;

            var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, ct);
        }

        var bundleSize = new FileInfo(fullBundlePath).Length;
        if (missingBlocks > 0)
        {
            log.LogWarning(
                "Repository bundle exported with missing block artifacts. RepositoryId {RepositoryId}. Missing {Missing}",
                repositoryId,
                missingBlocks);
        }

        var summary = missingBlocks == 0
            ? $"Bundle exported. Snapshots={manifest.Stats.SnapshotCount}, Versions={manifest.Stats.FileVersionCount}, Blocks={manifest.BlockArtifacts.Count}."
            : $"Bundle exported with warnings. Missing block artifacts={missingBlocks}.";

        return new RepositoryBundleExportResultDto(
            RepositoryId: repositoryId,
            BundlePath: fullBundlePath,
            SnapshotCount: manifest.Stats.SnapshotCount,
            FileIdentityCount: manifest.Stats.FileIdentityCount,
            FileVersionCount: manifest.Stats.FileVersionCount,
            DiffCount: manifest.Stats.TextDiffCount,
            BlockFileCount: manifest.BlockArtifacts.Count,
            BundleSizeBytes: bundleSize,
            ExportedAtUtc: manifest.ExportedAtUtc,
            Summary: summary);
    }

    public async Task<RepositoryBundleImportResultDto> ImportRepositoryAsync(
        string bundlePath,
        string targetDirectoryPath,
        string? repositoryNameOverride = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var fullBundlePath = Path.GetFullPath(bundlePath);
        if (!File.Exists(fullBundlePath))
            throw new FileNotFoundException("Bundle file not found.", fullBundlePath);

        var targetPath = Path.GetFullPath(targetDirectoryPath);
        Directory.CreateDirectory(targetPath);

        using var zip = ZipFile.OpenRead(fullBundlePath);
        var manifest = await ReadManifestAsync(zip, ct)
            ?? throw new InvalidOperationException("manifest.json was not found in bundle.");

        var warnings = new List<string>();
        if (!ValidateManifestShape(manifest, warnings, out var validationMessage))
            throw new InvalidOperationException($"Invalid repository bundle: {validationMessage}");

        if (manifest.BundleFormatVersion > CurrentBundleFormatVersion)
        {
            throw new InvalidOperationException(
                $"Bundle format version {manifest.BundleFormatVersion} is newer than supported version {CurrentBundleFormatVersion}.");
        }

        var now = DateTime.UtcNow;
        var repositoryName = NormalizeRepositoryName(repositoryNameOverride, manifest.Repository.Name);

        var snapshotIdMap = new Dictionary<long, long>();
        var identityIdMap = new Dictionary<long, long>();
        var versionIdMap = new Dictionary<long, long>();
        var diffIdMap = new Dictionary<long, long>();
        var hunkIdMap = new Dictionary<long, long>();
        var atomIdMap = new Dictionary<long, long>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var watchedDirectory = await db.Set<WatchedDirectory>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Path == targetPath, ct);

        if (watchedDirectory is null)
        {
            watchedDirectory = new WatchedDirectory
            {
                Path = targetPath,
                IsEnabled = true,
                ErrorMessage = null,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false,
                DeletedAt = null
            };
            db.Add(watchedDirectory);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            watchedDirectory.IsDeleted = false;
            watchedDirectory.DeletedAt = null;
            watchedDirectory.IsEnabled = true;
            watchedDirectory.ErrorMessage = null;
            watchedDirectory.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        var existingRepository = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.DirectoryId == watchedDirectory.Id, ct);

        if (existingRepository is not null)
        {
            throw new InvalidOperationException(
                $"Directory {targetPath} is already assigned to repository id {existingRepository.Id}. Please choose another target path.");
        }

        var repository = new Repository
        {
            Name = repositoryName,
            Description = manifest.Repository.Description,
            DirectoryId = watchedDirectory.Id,
            FileCount = 0,
            VersionCount = 0,
            TotalSizeBytes = 0,
            LastScannedAt = null,
            AutoCaptureFileVersions = manifest.Repository.AutoCaptureFileVersions,
            ProtectCloudMetadata = manifest.Repository.ProtectCloudMetadata,
            RetentionEnabled = false,
            RetentionMaxAgeDays = null,
            RetentionMaxSnapshots = null,
            RetentionMaxTotalSizeBytes = null,
            RetentionTriggerFilter = null,
            RetentionRunIntervalMinutes = 60,
            RetentionLastRunAt = null,
            RetentionLastStatus = null,
            SyncConflictStrategy = Repository.DefaultSyncConflictStrategy,
            SyncRetryMaxAttempts = 5,
            SyncRetryBaseDelaySeconds = 30,
            CloudLastSyncedAt = null,
            CloudLastLocalSnapshotId = null,
            CloudLastRemoteSnapshotId = null,
            CloudSyncLastStatus = null,
            CloudSyncLastError = null,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(repository);
        await db.SaveChangesAsync(ct);

        await EnsureDirectoryFormatsAsync(watchedDirectory.Id, manifest, now, ct);

        var snapshotRows = manifest.Snapshots
            .OrderBy(s => s.CreatedAtUtc)
            .ThenBy(s => s.Id)
            .Select(s => new
            {
                Source = s,
                Entity = new RepositorySnapshot
                {
                    RepositoryId = repository.Id,
                    CreatedAt = s.CreatedAtUtc,
                    Trigger = TruncateForColumn(s.Trigger, 64),
                    Title = TruncateNullable(s.Title, MaxNameLength),
                    TotalEntries = s.TotalEntries,
                    FileEntries = s.FileEntries,
                    DirectoryEntries = s.DirectoryEntries,
                    TotalFileBytes = s.TotalFileBytes,
                    IsDeleted = false,
                    DeletedAt = null
                }
            })
            .ToList();

        if (snapshotRows.Count > 0)
        {
            await SaveMappedRowsInBatchesAsync(
                snapshotRows,
                row => row.Entity,
                (row, entity) => snapshotIdMap[row.Source.Id] = entity.Id,
                ct);
        }

        var entryRows = manifest.SnapshotEntries
            .Where(e => snapshotIdMap.ContainsKey(e.SnapshotId))
            .Select(e => new RepositorySnapshotEntry
            {
                SnapshotId = snapshotIdMap[e.SnapshotId],
                RepositoryId = repository.Id,
                RelativePath = e.RelativePath,
                ParentRelativePath = e.ParentRelativePath,
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Extension = e.Extension,
                SizeBytes = e.SizeBytes,
                LastWriteUtc = e.LastWriteUtc,
                ContentHashSha256 = e.ContentHashSha256,
                CreatedAt = e.CreatedAtUtc,
                IsDeleted = false,
                DeletedAt = null
            })
            .ToList();

        if (entryRows.Count > 0)
            await SaveEntitiesInBatchesAsync(entryRows, ct);

        var identityRows = manifest.FileIdentities
            .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Id)
            .Select(i => new
            {
                Source = i,
                Entity = new FileIdentity
                {
                    RepositoryId = repository.Id,
                    RelativePath = i.RelativePath,
                    Name = i.Name,
                    Extension = i.Extension,
                    CreatedAt = i.CreatedAtUtc,
                    UpdatedAt = i.UpdatedAtUtc,
                    IsDeleted = false,
                    DeletedAt = null
                }
            })
            .ToList();

        if (identityRows.Count > 0)
        {
            await SaveMappedRowsInBatchesAsync(
                identityRows,
                row => row.Entity,
                (row, entity) => identityIdMap[row.Source.Id] = entity.Id,
                ct);
        }

        var versionRows = manifest.FileVersions
            .Where(v => identityIdMap.ContainsKey(v.FileIdentityId))
            .OrderBy(v => v.CreatedAtUtc)
            .ThenBy(v => v.Id)
            .Select(v => new
            {
                Source = v,
                Entity = new FileVersion
                {
                    FileIdentityId = identityIdMap[v.FileIdentityId],
                    ContentHashSha256 = v.ContentHashSha256,
                    SizeBytes = v.SizeBytes,
                    LastWriteUtc = v.LastWriteUtc,
                    IsDeletionMarker = v.IsDeletionMarker,
                    CreatedAt = v.CreatedAtUtc,
                    IsDeleted = false,
                    DeletedAt = null
                }
            })
            .ToList();

        if (versionRows.Count > 0)
        {
            await SaveMappedRowsInBatchesAsync(
                versionRows,
                row => row.Entity,
                (row, entity) => versionIdMap[row.Source.Id] = entity.Id,
                ct);
        }

        var blockRows = manifest.FileVersionBlocks
            .Where(b => versionIdMap.ContainsKey(b.FileVersionId))
            .OrderBy(b => b.FileVersionId)
            .ThenBy(b => b.Sequence)
            .Select(b => new FileVersionBlock
            {
                FileVersionId = versionIdMap[b.FileVersionId],
                Sequence = b.Sequence,
                BlockStorageKey = b.BlockStorageKey,
                LengthBytes = b.LengthBytes,
                StoredSizeBytes = b.StoredSizeBytes,
                CreatedAt = b.CreatedAtUtc,
                IsDeleted = false,
                DeletedAt = null
            })
            .ToList();

        if (blockRows.Count > 0)
            await SaveEntitiesInBatchesAsync(blockRows, ct);

        var linkRows = manifest.SnapshotFileLinks
            .Where(l => snapshotIdMap.ContainsKey(l.SnapshotId)
                        && identityIdMap.ContainsKey(l.FileIdentityId)
                        && versionIdMap.ContainsKey(l.FileVersionId))
            .OrderBy(l => l.SnapshotId)
            .ThenBy(l => l.FileIdentityId)
            .Select(l => new SnapshotFileLink
            {
                SnapshotId = snapshotIdMap[l.SnapshotId],
                FileIdentityId = identityIdMap[l.FileIdentityId],
                FileVersionId = versionIdMap[l.FileVersionId],
                CreatedAt = l.CreatedAtUtc,
                IsDeleted = false,
                DeletedAt = null
            })
            .ToList();

        if (linkRows.Count > 0)
            await SaveEntitiesInBatchesAsync(linkRows, ct);

        var diffRows = manifest.TextDiffs
            .Where(d => versionIdMap.ContainsKey(d.LeftFileVersionId)
                        && versionIdMap.ContainsKey(d.RightFileVersionId))
            .OrderBy(d => d.Id)
            .Select(d => new
            {
                Source = d,
                Entity = new FileVersionTextDiff
                {
                    LeftFileVersionId = versionIdMap[d.LeftFileVersionId],
                    RightFileVersionId = versionIdMap[d.RightFileVersionId],
                    MaxLines = d.MaxLines,
                    DiffKeySha256 = d.DiffKeySha256,
                    RelativePath = d.RelativePath,
                    AddedLines = d.AddedLines,
                    RemovedLines = d.RemovedLines,
                    IsTruncated = d.IsTruncated,
                    StorageFormatVersion = d.StorageFormatVersion,
                    LinesJson = d.LinesJson,
                    CreatedAt = d.CreatedAtUtc,
                    UpdatedAt = d.UpdatedAtUtc,
                    IsDeleted = false,
                    DeletedAt = null
                }
            })
            .ToList();

        if (diffRows.Count > 0)
        {
            await SaveMappedRowsInBatchesAsync(
                diffRows,
                row => row.Entity,
                (row, entity) => diffIdMap[row.Source.Id] = entity.Id,
                ct);
        }

        var atomRows = await UpsertTextLineAtomsAsync(manifest.TextLineAtoms, atomIdMap, ct);

        var hunkRows = manifest.TextDiffHunks
            .Where(h => diffIdMap.ContainsKey(h.DiffId))
            .OrderBy(h => h.DiffId)
            .ThenBy(h => h.Sequence)
            .Select(h => new
            {
                Source = h,
                Entity = new FileVersionTextDiffHunk
                {
                    DiffId = diffIdMap[h.DiffId],
                    Sequence = h.Sequence,
                    StartLineSequence = h.StartLineSequence,
                    EndLineSequence = h.EndLineSequence,
                    OldStartLine = h.OldStartLine,
                    OldLineCount = h.OldLineCount,
                    NewStartLine = h.NewStartLine,
                    NewLineCount = h.NewLineCount,
                    ChangeKind = h.ChangeKind,
                    CreatedAt = h.CreatedAtUtc,
                    IsDeleted = false,
                    DeletedAt = null
                }
            })
            .ToList();

        if (hunkRows.Count > 0)
        {
            await SaveMappedRowsInBatchesAsync(
                hunkRows,
                row => row.Entity,
                (row, entity) => hunkIdMap[row.Source.Id] = entity.Id,
                ct);
        }

        var lineRows = manifest.TextDiffLines
            .Where(l => diffIdMap.ContainsKey(l.DiffId)
                        && atomIdMap.ContainsKey(l.TextLineAtomId)
                        && (!l.HunkId.HasValue || hunkIdMap.ContainsKey(l.HunkId.Value)))
            .OrderBy(l => l.DiffId)
            .ThenBy(l => l.Sequence)
            .Select(l => new FileVersionTextDiffLine
            {
                DiffId = diffIdMap[l.DiffId],
                Sequence = l.Sequence,
                Kind = l.Kind,
                LeftLineNumber = l.LeftLineNumber,
                RightLineNumber = l.RightLineNumber,
                HunkId = l.HunkId.HasValue ? hunkIdMap[l.HunkId.Value] : null,
                InHunkSequence = l.InHunkSequence,
                TextLineAtomId = atomIdMap[l.TextLineAtomId],
                CreatedAt = l.CreatedAtUtc,
                IsDeleted = false,
                DeletedAt = null
            })
            .ToList();

        if (lineRows.Count > 0)
            await SaveEntitiesInBatchesAsync(lineRows, ct);

        var latestSnapshot = snapshotRows
            .Select(r => r.Entity)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .FirstOrDefault();

        repository.FileCount = manifest.Repository.FileCount > 0
            ? manifest.Repository.FileCount
            : latestSnapshot?.FileEntries ?? 0;

        repository.TotalSizeBytes = manifest.Repository.TotalSizeBytes > 0
            ? manifest.Repository.TotalSizeBytes
            : latestSnapshot?.TotalFileBytes ?? 0;

        repository.VersionCount = versionRows.Count;
        repository.LastScannedAt = manifest.Repository.LastScannedAtUtc ?? latestSnapshot?.CreatedAt;
        repository.AutoCaptureFileVersions = manifest.Repository.AutoCaptureFileVersions;
        repository.ProtectCloudMetadata = manifest.Repository.ProtectCloudMetadata;
        repository.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var storeRoot = RepositoryBundleBlockPathResolver.ResolveStoreRoot(configuration);
        var importedBlockFiles = await ImportBlockArtifactsAsync(
            zip,
            manifest.BlockArtifacts,
            storeRoot,
            warnings,
            ct);

        if (manifest.BlockArtifacts.Count == 0 && blockRows.Count > 0)
            warnings.Add("Bundle does not contain physical block artifacts. Version metadata was imported only.");

        if (atomRows > 0)
            warnings.Add($"Text atoms reused/created: {atomRows}.");

        var summary =
            $"Bundle imported. Snapshots={snapshotRows.Count}, Versions={versionRows.Count}, Diffs={diffRows.Count}, Blocks={importedBlockFiles}.";

        return new RepositoryBundleImportResultDto(
            RepositoryId: repository.Id,
            RepositoryName: repository.Name,
            TargetDirectoryPath: targetPath,
            SnapshotCount: snapshotRows.Count,
            FileIdentityCount: identityRows.Count,
            FileVersionCount: versionRows.Count,
            DiffCount: diffRows.Count,
            BlockFileCount: importedBlockFiles,
            ImportedAtUtc: DateTime.UtcNow,
            Warnings: warnings,
            Summary: summary);
    }

    private async Task SaveEntitiesInBatchesAsync<TEntity>(
        IReadOnlyList<TEntity> entities,
        CancellationToken ct)
        where TEntity : class
    {
        foreach (var chunk in Chunk(entities, ImportBatchSize))
        {
            var previousAutoDetectChanges = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                db.AddRange(chunk);
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetectChanges;
                DetachEntities(chunk);
            }
        }
    }

    private async Task SaveMappedRowsInBatchesAsync<TSource, TEntity>(
        IReadOnlyList<TSource> rows,
        Func<TSource, TEntity> entitySelector,
        Action<TSource, TEntity> afterSave,
        CancellationToken ct)
        where TEntity : class
    {
        foreach (var chunk in Chunk(rows, ImportBatchSize))
        {
            var entities = chunk.Select(entitySelector).ToList();
            var previousAutoDetectChanges = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                db.AddRange(entities);
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetectChanges;
            }

            foreach (var row in chunk)
                afterSave(row, entitySelector(row));

            DetachEntities(entities);
        }
    }

    private void DetachEntities<TEntity>(IReadOnlyList<TEntity> entities)
        where TEntity : class
    {
        foreach (var entity in entities)
        {
            var entry = db.Entry(entity);
            if (entry.State != EntityState.Detached)
                entry.State = EntityState.Detached;
        }
    }

    private static bool ValidateManifestShape(
        RepositoryBundleManifest manifest,
        List<string> warnings,
        out string message)
    {
        if (manifest.BundleFormatVersion <= 0)
        {
            message = "Bundle format version is missing.";
            return false;
        }

        if (manifest.Repository is null || string.IsNullOrWhiteSpace(manifest.Repository.Name))
        {
            message = "Repository metadata is missing.";
            return false;
        }

        if (manifest.Snapshots.Count == 0)
            warnings.Add("Bundle contains no snapshots.");

        if (manifest.FileIdentities.Count == 0)
            warnings.Add("Bundle contains no file identities.");

        if (manifest.FileVersions.Count == 0)
            warnings.Add("Bundle contains no file versions.");

        if (manifest.FileVersionBlocks.Count > 0 && manifest.BlockArtifacts.Count == 0)
            warnings.Add("Bundle contains block references without block artifacts.");

        message = "Bundle is valid.";
        return true;
    }

    private async Task<List<BundleTextLineAtomInfo>> LoadTextLineAtomsAsync(
        IReadOnlyList<long> atomIds,
        CancellationToken ct)
    {
        if (atomIds.Count == 0)
            return [];

        var output = new List<BundleTextLineAtomInfo>(atomIds.Count);

        foreach (var chunk in Chunk(atomIds, 500))
        {
            var items = await db.Set<TextLineAtom>()
                .AsNoTracking()
                .Where(a => chunk.Contains(a.Id))
                .Select(a => new BundleTextLineAtomInfo
                {
                    Id = a.Id,
                    HashSha256 = a.HashSha256,
                    Text = a.Text,
                    CreatedAtUtc = a.CreatedAt
                })
                .ToListAsync(ct);

            output.AddRange(items);
        }

        output.Sort((left, right) => left.Id.CompareTo(right.Id));
        return output;
    }

    private async Task<List<BundleFileVersionBlockInfo>> LoadFileVersionBlocksAsync(
        IReadOnlyList<long> fileVersionIds,
        CancellationToken ct)
    {
        if (fileVersionIds.Count == 0)
            return [];

        var output = new List<BundleFileVersionBlockInfo>();

        foreach (var chunk in Chunk(fileVersionIds, 500))
        {
            var items = await db.Set<FileVersionBlock>()
                .AsNoTracking()
                .Where(b => !b.IsDeleted && chunk.Contains(b.FileVersionId))
                .OrderBy(b => b.FileVersionId)
                .ThenBy(b => b.Sequence)
                .Select(b => new BundleFileVersionBlockInfo
                {
                    Id = b.Id,
                    FileVersionId = b.FileVersionId,
                    Sequence = b.Sequence,
                    BlockStorageKey = b.BlockStorageKey,
                    LengthBytes = b.LengthBytes,
                    StoredSizeBytes = b.StoredSizeBytes,
                    CreatedAtUtc = b.CreatedAt
                })
                .ToListAsync(ct);

            output.AddRange(items);
        }

        output.Sort((left, right) =>
        {
            var versionCompare = left.FileVersionId.CompareTo(right.FileVersionId);
            return versionCompare != 0 ? versionCompare : left.Sequence.CompareTo(right.Sequence);
        });
        return output;
    }

    private async Task<List<BundleSnapshotLinkInfo>> LoadSnapshotFileLinksAsync(
        IReadOnlyList<long> snapshotIds,
        CancellationToken ct)
    {
        if (snapshotIds.Count == 0)
            return [];

        var output = new List<BundleSnapshotLinkInfo>();

        foreach (var chunk in Chunk(snapshotIds, 500))
        {
            var items = await db.Set<SnapshotFileLink>()
                .AsNoTracking()
                .Where(l => !l.IsDeleted && chunk.Contains(l.SnapshotId))
                .OrderBy(l => l.SnapshotId)
                .ThenBy(l => l.FileIdentityId)
                .Select(l => new BundleSnapshotLinkInfo
                {
                    Id = l.Id,
                    SnapshotId = l.SnapshotId,
                    FileIdentityId = l.FileIdentityId,
                    FileVersionId = l.FileVersionId,
                    CreatedAtUtc = l.CreatedAt
                })
                .ToListAsync(ct);

            output.AddRange(items);
        }

        output.Sort((left, right) =>
        {
            var snapshotCompare = left.SnapshotId.CompareTo(right.SnapshotId);
            return snapshotCompare != 0 ? snapshotCompare : left.FileIdentityId.CompareTo(right.FileIdentityId);
        });
        return output;
    }

    private async Task<List<BundleTextDiffInfo>> LoadTextDiffsAsync(
        IReadOnlyList<long> fileVersionIds,
        CancellationToken ct)
    {
        if (fileVersionIds.Count == 0)
            return [];

        var byId = new Dictionary<long, BundleTextDiffInfo>();

        foreach (var chunk in Chunk(fileVersionIds, 500))
        {
            var items = await db.Set<FileVersionTextDiff>()
                .AsNoTracking()
                .Where(d => !d.IsDeleted
                            && (chunk.Contains(d.LeftFileVersionId) || chunk.Contains(d.RightFileVersionId)))
                .OrderBy(d => d.Id)
                .Select(d => new BundleTextDiffInfo
                {
                    Id = d.Id,
                    LeftFileVersionId = d.LeftFileVersionId,
                    RightFileVersionId = d.RightFileVersionId,
                    MaxLines = d.MaxLines,
                    DiffKeySha256 = d.DiffKeySha256,
                    RelativePath = d.RelativePath,
                    AddedLines = d.AddedLines,
                    RemovedLines = d.RemovedLines,
                    IsTruncated = d.IsTruncated,
                    StorageFormatVersion = d.StorageFormatVersion,
                    LinesJson = d.LinesJson,
                    CreatedAtUtc = d.CreatedAt,
                    UpdatedAtUtc = d.UpdatedAt
                })
                .ToListAsync(ct);

            foreach (var item in items)
            {
                if (!byId.ContainsKey(item.Id))
                    byId[item.Id] = item;
            }
        }

        return byId.Values
            .OrderBy(item => item.Id)
            .ToList();
    }

    private async Task<List<BundleTextDiffHunkInfo>> LoadTextDiffHunksAsync(
        IReadOnlyCollection<long> diffIds,
        CancellationToken ct)
    {
        if (diffIds.Count == 0)
            return [];

        var output = new List<BundleTextDiffHunkInfo>();

        foreach (var chunk in Chunk(diffIds.ToList(), 500))
        {
            var items = await db.Set<FileVersionTextDiffHunk>()
                .AsNoTracking()
                .Where(h => !h.IsDeleted && chunk.Contains(h.DiffId))
                .OrderBy(h => h.DiffId)
                .ThenBy(h => h.Sequence)
                .Select(h => new BundleTextDiffHunkInfo
                {
                    Id = h.Id,
                    DiffId = h.DiffId,
                    Sequence = h.Sequence,
                    StartLineSequence = h.StartLineSequence,
                    EndLineSequence = h.EndLineSequence,
                    OldStartLine = h.OldStartLine,
                    OldLineCount = h.OldLineCount,
                    NewStartLine = h.NewStartLine,
                    NewLineCount = h.NewLineCount,
                    ChangeKind = h.ChangeKind,
                    CreatedAtUtc = h.CreatedAt
                })
                .ToListAsync(ct);

            output.AddRange(items);
        }

        output.Sort((left, right) =>
        {
            var diffCompare = left.DiffId.CompareTo(right.DiffId);
            return diffCompare != 0 ? diffCompare : left.Sequence.CompareTo(right.Sequence);
        });
        return output;
    }

    private async Task<List<BundleTextDiffLineInfo>> LoadTextDiffLinesAsync(
        IReadOnlyCollection<long> diffIds,
        CancellationToken ct)
    {
        if (diffIds.Count == 0)
            return [];

        var output = new List<BundleTextDiffLineInfo>();

        foreach (var chunk in Chunk(diffIds.ToList(), 500))
        {
            var items = await db.Set<FileVersionTextDiffLine>()
                .AsNoTracking()
                .Where(l => !l.IsDeleted && chunk.Contains(l.DiffId))
                .OrderBy(l => l.DiffId)
                .ThenBy(l => l.Sequence)
                .Select(l => new BundleTextDiffLineInfo
                {
                    Id = l.Id,
                    DiffId = l.DiffId,
                    Sequence = l.Sequence,
                    Kind = l.Kind,
                    LeftLineNumber = l.LeftLineNumber,
                    RightLineNumber = l.RightLineNumber,
                    HunkId = l.HunkId,
                    InHunkSequence = l.InHunkSequence,
                    TextLineAtomId = l.TextLineAtomId,
                    CreatedAtUtc = l.CreatedAt
                })
                .ToListAsync(ct);

            output.AddRange(items);
        }

        output.Sort((left, right) =>
        {
            var diffCompare = left.DiffId.CompareTo(right.DiffId);
            return diffCompare != 0 ? diffCompare : left.Sequence.CompareTo(right.Sequence);
        });
        return output;
    }

    private async Task<int> UpsertTextLineAtomsAsync(
        IReadOnlyList<BundleTextLineAtomInfo> atoms,
        IDictionary<long, long> atomIdMap,
        CancellationToken ct)
    {
        if (atoms.Count == 0)
            return 0;

        var hashes = atoms.Select(a => a.HashSha256)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = new Dictionary<(string Hash, string Text), TextLineAtom>();

        foreach (var chunk in Chunk(hashes, 500))
        {
            var rows = await db.Set<TextLineAtom>()
                .Where(a => chunk.Contains(a.HashSha256))
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                var key = (row.HashSha256, row.Text);
                if (!existing.ContainsKey(key))
                    existing[key] = row;
            }
        }

        var pendingByKey = new Dictionary<(string Hash, string Text), PendingTextLineAtomImportRow>();

        foreach (var atom in atoms.OrderBy(a => a.Id))
        {
            if (string.IsNullOrWhiteSpace(atom.HashSha256))
                continue;

            var key = (atom.HashSha256, atom.Text ?? string.Empty);
            if (existing.TryGetValue(key, out var found))
            {
                atomIdMap[atom.Id] = found.Id;
                continue;
            }

            if (pendingByKey.TryGetValue(key, out var pending))
            {
                pending.SourceIds.Add(atom.Id);
                continue;
            }

            pendingByKey[key] = new PendingTextLineAtomImportRow(
                new TextLineAtom
                {
                    HashSha256 = atom.HashSha256,
                    Text = atom.Text ?? string.Empty,
                    CreatedAt = atom.CreatedAtUtc
                },
                new List<long> { atom.Id });
        }

        if (pendingByKey.Count == 0)
            return atomIdMap.Count;

        foreach (var chunk in Chunk(pendingByKey.Values.ToList(), ImportBatchSize))
        {
            var entities = chunk.Select(row => row.Entity).ToList();
            var previousAutoDetectChanges = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                db.AddRange(entities);
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetectChanges;
            }

            foreach (var row in chunk)
            {
                foreach (var sourceId in row.SourceIds)
                    atomIdMap[sourceId] = row.Entity.Id;
            }

            DetachEntities(entities);
        }

        return atomIdMap.Count;
    }

    private sealed class PendingTextLineAtomImportRow(TextLineAtom entity, List<long> sourceIds)
    {
        public TextLineAtom Entity { get; } = entity;
        public List<long> SourceIds { get; } = sourceIds;
    }

    private async Task<int> ImportBlockArtifactsAsync(
        ZipArchive zip,
        IReadOnlyList<BundleBlockArtifactInfo> artifacts,
        string storeRoot,
        List<string> warnings,
        CancellationToken ct)
    {
        if (artifacts.Count == 0)
            return 0;

        var imported = 0;

        foreach (var artifact in artifacts)
        {
            ct.ThrowIfCancellationRequested();

            var destinationPath = RepositoryBundleBlockPathResolver.ResolveLocalBlockPath(storeRoot, artifact.Hash);
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                warnings.Add($"Cannot resolve block path for hash {artifact.Hash}.");
                continue;
            }

            if (File.Exists(destinationPath))
                continue;

            var entry = zip.GetEntry(artifact.EntryName);
            if (entry is null)
            {
                warnings.Add($"Block artifact entry '{artifact.EntryName}' is missing in bundle.");
                continue;
            }

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);

            await source.CopyToAsync(destination, 128 * 1024, ct);
            imported++;
        }

        return imported;
    }

    private static async Task<RepositoryBundleManifest?> ReadManifestAsync(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry(ManifestEntryName);
        if (entry is null)
            return null;

        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<RepositoryBundleManifest>(stream, JsonOptions, ct);
    }

    private static string NormalizeRepositoryName(string? explicitName, string fallbackName)
    {
        var value = !string.IsNullOrWhiteSpace(explicitName)
            ? explicitName
            : fallbackName;

        var normalized = string.IsNullOrWhiteSpace(value)
            ? "imported_repository"
            : value.Trim();

        if (normalized.Length <= MaxNameLength)
            return normalized;

        return normalized[..MaxNameLength];
    }

    private async Task EnsureDirectoryFormatsAsync(
        int directoryId,
        RepositoryBundleManifest manifest,
        DateTime now,
        CancellationToken ct)
    {
        var extensions = manifest.SnapshotEntries
            .Where(e => !e.IsDirectory)
            .Select(e => NormalizeExtension(e.Extension))
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extensions.Count == 0)
            return;

        var existingFormats = await db.Set<D_WatchedFormat>()
            .IgnoreQueryFilters()
            .Where(f => extensions.Contains(f.Pattern))
            .ToListAsync(ct);

        var byPattern = existingFormats
            .ToDictionary(f => f.Pattern, StringComparer.OrdinalIgnoreCase);

        foreach (var extension in extensions)
        {
            if (byPattern.TryGetValue(extension, out var format))
            {
                format.IsDeleted = false;
                format.DeletedAt = null;
                format.IsEnabled = true;
                format.UpdatedAt = now;
                continue;
            }

            format = new D_WatchedFormat
            {
                Pattern = extension,
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false,
                DeletedAt = null
            };

            db.Add(format);
            byPattern[extension] = format;
        }

        await db.SaveChangesAsync(ct);

        var formatIds = byPattern.Values.Select(f => f.Id).ToList();
        var existingLinks = await db.Set<WatchedDirectoryFormat>()
            .IgnoreQueryFilters()
            .Where(l => l.DirectoryId == directoryId && formatIds.Contains(l.FormatId))
            .ToListAsync(ct);

        var byFormatId = existingLinks.ToDictionary(l => l.FormatId);

        foreach (var formatId in formatIds)
        {
            if (byFormatId.TryGetValue(formatId, out var link))
            {
                link.IsDeleted = false;
                link.DeletedAt = null;
                link.UpdatedAt = now;
                continue;
            }

            db.Add(new WatchedDirectoryFormat
            {
                DirectoryId = directoryId,
                FormatId = formatId,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false,
                DeletedAt = null
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;

        return normalized.ToLowerInvariant();
    }

    private static string TruncateForColumn(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? TruncateNullable(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        if (source.Count == 0)
            yield break;

        var offset = 0;
        while (offset < source.Count)
        {
            var take = Math.Min(size, source.Count - offset);
            var chunk = new List<T>(take);
            for (var i = 0; i < take; i++)
                chunk.Add(source[offset + i]);

            yield return chunk;
            offset += take;
        }
    }
}
