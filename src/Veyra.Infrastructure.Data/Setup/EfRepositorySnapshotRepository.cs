using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Application.Services.Diff;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositorySnapshotRepository(
    VeyraDbContext db,
    IFileContentStore contentStore,
    ITextDiffEngine diffEngine,
    ISnapshotComparisonEngine snapshotComparison,
    ILogger<EfRepositorySnapshotRepository> log) : IRepositorySnapshotRepository
{
    private const int PrecomputedDiffMaxLines = 4000;
    private const int CurrentTextDiffStorageFormatVersion = 3;
    private const int ManagedPreviewChunkSize = 64 * 1024;
    private const int SnapshotEntryInsertBatchSize = 64;
    private const int SnapshotFileLinkInsertBatchSize = 128;

    private static readonly JsonSerializerOptions DiffJsonOptions = new();

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yml", ".yaml", ".ini", ".toml", ".log",
        ".cs", ".js", ".ts", ".java", ".py", ".rs", ".go", ".c", ".cpp", ".h", ".hpp",
        ".html", ".css", ".sql", ".xaml", ".axaml"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"
    };

    private sealed record PendingSnapshotFileLink(long FileIdentityId, FileVersion FileVersion);

    public async Task<SnapshotSaveResultDto> SaveSnapshotAsync(
        int repositoryId,
        string trigger,
        DateTime scannedAtUtc,
        IReadOnlyCollection<RepositoryScanEntryDto> entries,
        bool saveFileVersions = true,
        string? snapshotTitle = null,
        CancellationToken ct = default)
    {
        var overallTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();
        long preparationMs = 0;
        long snapshotHeaderMs = 0;
        long snapshotEntriesMs = 0;
        long identityPreparationMs = 0;
        long versionBuildMs = 0;
        long versionPersistMs = 0;
        long linkPersistMs = 0;
        long diffPrecomputeMs = 0;

        db.ChangeTracker.Clear();

        var repo = await db.Set<Repository>()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repo is null)
            return SnapshotSaveResultDto.Skipped();

        var safeTrigger = string.IsNullOrWhiteSpace(trigger)
            ? "manual"
            : trigger.Trim();

        var totalEntries = entries.Count;
        var fileEntries = entries.Count(e => !e.IsDirectory);
        var dirEntries = totalEntries - fileEntries;
        var totalFileBytes = entries
            .Where(e => !e.IsDirectory)
            .Sum(e => e.SizeBytes);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var previousSnapshotsQuery = db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted);

        if (saveFileVersions)
        {
            previousSnapshotsQuery = previousSnapshotsQuery
                .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted));
        }

        var previousSnapshotId = await previousSnapshotsQuery
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        var previousFilesByPath = previousSnapshotId == 0
            ? new Dictionary<string, (string? Hash, long SizeBytes, string Name, DateTime LastWriteUtc)>(StringComparer.OrdinalIgnoreCase)
            : await db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == previousSnapshotId && !e.IsDirectory && !e.IsDeleted)
                .ToDictionaryAsync(
                    e => e.RelativePath,
                    e => (Hash: e.ContentHashSha256, SizeBytes: e.SizeBytes, Name: e.Name, LastWriteUtc: e.LastWriteUtc),
                    StringComparer.OrdinalIgnoreCase,
                    ct);
        var currentFilesByPath = entries
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.RelativePath, e => e, StringComparer.OrdinalIgnoreCase);
        if (saveFileVersions && previousSnapshotId > 0)
        {
            var currentStates = currentFilesByPath.Values
                .Select(e => new RepositoryPathStateDto(
                    e.RelativePath,
                    e.Name,
                    e.SizeBytes,
                    e.LastWriteUtc,
                    e.ContentHashSha256))
                .ToList();
            var baselineStates = previousFilesByPath
                .Select(pair => new RepositoryPathStateDto(
                    pair.Key,
                    pair.Value.Name,
                    pair.Value.SizeBytes,
                    pair.Value.LastWriteUtc,
                    pair.Value.Hash))
                .ToList();
            var comparison = await snapshotComparison.CompareRepositoryPathsAsync(currentStates, baselineStates, 1, ct);
            if (comparison.ChangedFilesCount == 0)
            {
                preparationMs = stageTimer.ElapsedMilliseconds;
                repo.FileCount = fileEntries;
                repo.TotalSizeBytes = totalFileBytes;
                repo.LastScannedAt = scannedAtUtc;
                repo.UpdatedAt = scannedAtUtc;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                log.LogInformation(
                    "Snapshot skipped (no changes). RepositoryId {RepositoryId}. Files {Files}. Trigger {Trigger}. PreparationMs {PreparationMs}. TotalMs {TotalMs}",
                    repositoryId,
                    fileEntries,
                    safeTrigger,
                    preparationMs,
                    overallTimer.ElapsedMilliseconds);
                return SnapshotSaveResultDto.NoChanges();
            }
        }
        preparationMs = stageTimer.ElapsedMilliseconds;

        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = safeTrigger,
            Title = NormalizeSnapshotTitle(snapshotTitle),
            CreatedAt = scannedAtUtc,
            TotalEntries = totalEntries,
            FileEntries = fileEntries,
            DirectoryEntries = dirEntries,
            TotalFileBytes = totalFileBytes,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(snapshot);
        stageTimer.Restart();
        await db.SaveChangesAsync(ct);
        snapshotHeaderMs = stageTimer.ElapsedMilliseconds;

        if (totalEntries > 0)
        {
            stageTimer.Restart();
            await InsertSnapshotEntriesAsync(snapshot.Id, repositoryId, entries, scannedAtUtc, ct);
            snapshotEntriesMs = stageTimer.ElapsedMilliseconds;
        }

        if (!saveFileVersions)
        {
            repo.FileCount = fileEntries;
            repo.TotalSizeBytes = totalFileBytes;
            repo.LastScannedAt = scannedAtUtc;
            repo.UpdatedAt = scannedAtUtc;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            log.LogInformation(
                "Repository sync saved without file versions. RepositoryId {RepositoryId}. Entries {Entries}. Files {Files}. Trigger {Trigger}. PreparationMs {PreparationMs}. SnapshotHeaderMs {SnapshotHeaderMs}. SnapshotEntriesMs {SnapshotEntriesMs}. TotalMs {TotalMs}",
                repositoryId,
                totalEntries,
                fileEntries,
                safeTrigger,
                preparationMs,
                snapshotHeaderMs,
                snapshotEntriesMs,
                overallTimer.ElapsedMilliseconds);

            return SnapshotSaveResultDto.Created();
        }
        stageTimer.Restart();
        var allPaths = currentFilesByPath.Keys
            .Concat(previousFilesByPath.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var identitiesByPath = allPaths.Count == 0
            ? new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase)
            : await db.Set<FileIdentity>()
                .IgnoreQueryFilters()
                .Where(i => i.RepositoryId == repositoryId && allPaths.Contains(i.RelativePath))
                .ToDictionaryAsync(i => i.RelativePath, StringComparer.OrdinalIgnoreCase, ct);

        foreach (var path in allPaths)
        {
            if (identitiesByPath.ContainsKey(path))
                continue;

            var current = currentFilesByPath.GetValueOrDefault(path);

            var identity = new FileIdentity
            {
                RepositoryId = repositoryId,
                RelativePath = path,
                Name = current?.Name ?? Path.GetFileName(path),
                Extension = current?.Extension,
                CreatedAt = scannedAtUtc,
                IsDeleted = false,
                DeletedAt = null,
                UpdatedAt = scannedAtUtc
            };

            db.Add(identity);
            identitiesByPath[path] = identity;
        }

        await db.SaveChangesAsync(ct);
        identityPreparationMs = stageTimer.ElapsedMilliseconds;

        var identityIds = identitiesByPath.Values.Select(i => i.Id).Distinct().ToList();

        stageTimer.Restart();
        var latestVersionsByIdentityId = new Dictionary<long, FileVersion>();
        if (identityIds.Count > 0)
        {
            var existingVersions = await db.Set<FileVersion>()
                .Where(v => identityIds.Contains(v.FileIdentityId) && !v.IsDeleted)
                .OrderByDescending(v => v.CreatedAt)
                .ThenByDescending(v => v.Id)
                .ToListAsync(ct);

            latestVersionsByIdentityId = existingVersions
                .GroupBy(v => v.FileIdentityId)
                .ToDictionary(g => g.Key, g => g.First());
        }

        var latestVersionIds = latestVersionsByIdentityId.Values
            .Select(v => v.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var latestVersionIdsWithBlocks = latestVersionIds.Count == 0
            ? new HashSet<long>()
            : (await db.Set<FileVersionBlock>()
                    .Where(b => latestVersionIds.Contains(b.FileVersionId) && !b.IsDeleted)
                    .Select(b => b.FileVersionId)
                    .Distinct()
                    .ToListAsync(ct))
                .ToHashSet();

        var planningStates = new List<RepositoryVersionPlanningFileStateDto>(allPaths.Count);
        foreach (var path in allPaths)
        {
            currentFilesByPath.TryGetValue(path, out var current);
            var hasPrevious = previousFilesByPath.TryGetValue(path, out var previous);
            var identity = identitiesByPath[path];
            var hasLatestVersion = latestVersionsByIdentityId.TryGetValue(identity.Id, out var latestVersion);
            var latestHasBlocks = hasLatestVersion
                                  && latestVersion is not null
                                  && latestVersion.Id > 0
                                  && (latestVersion.SizeBytes == 0 || latestVersionIdsWithBlocks.Contains(latestVersion.Id));

            planningStates.Add(new RepositoryVersionPlanningFileStateDto(
                RelativePath: path,
                HasCurrent: current is not null,
                CurrentSizeBytes: current?.SizeBytes ?? 0,
                CurrentContentHashSha256: current?.ContentHashSha256,
                HasPrevious: hasPrevious,
                PreviousSizeBytes: previous.SizeBytes,
                PreviousContentHashSha256: previous.Hash,
                HasLatestVersion: hasLatestVersion,
                LatestIsDeletionMarker: latestVersion?.IsDeletionMarker ?? false,
                LatestSizeBytes: latestVersion?.SizeBytes ?? 0,
                LatestHasBlocks: latestHasBlocks));
        }

        var versionPlan = await snapshotComparison.PlanRepositoryVersionsAsync(planningStates, ct);
        var planByPath = versionPlan.Entries
            .GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var newVersions = new List<FileVersion>();
        var pendingLinks = new List<PendingSnapshotFileLink>();
        var pendingBlocks = new Dictionary<FileVersion, IReadOnlyList<StoredFileBlockDto>>();
        var pendingPrecomputedDiffs = new List<PendingTextDiffPrecompute>();

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var identity = identitiesByPath[path];
            currentFilesByPath.TryGetValue(path, out var current);
            var hadPrevious = previousFilesByPath.TryGetValue(path, out var previous);
            planByPath.TryGetValue(path, out var planEntry);

            FileVersion selectedVersion;

            if (current is not null)
            {
                identity.Name = current.Name;
                identity.Extension = current.Extension;
                identity.IsDeleted = false;
                identity.DeletedAt = null;
                identity.UpdatedAt = scannedAtUtc;

                var previousHash = previous.Hash ?? string.Empty;
                var currentHash = current.ContentHashSha256 ?? string.Empty;

                var changed = !hadPrevious
                    || !string.Equals(previousHash, currentHash, StringComparison.OrdinalIgnoreCase)
                    || previous.SizeBytes != current.SizeBytes;

                var hasLatest = latestVersionsByIdentityId.TryGetValue(identity.Id, out selectedVersion!);
                var previousVersionForDiff = hasLatest ? selectedVersion : null;

                var latestHasBlocks = hasLatest
                                      && selectedVersion.Id > 0
                                      && (selectedVersion.SizeBytes == 0 || latestVersionIdsWithBlocks.Contains(selectedVersion.Id));

                var shouldCreateNewVersion = planEntry?.ShouldCreateNewVersion
                                             ?? (changed
                                                 || !hasLatest
                                                 || (hasLatest && selectedVersion.IsDeletionMarker)
                                                 || !latestHasBlocks);

                if (!hasLatest && !shouldCreateNewVersion)
                    shouldCreateNewVersion = true;

                if (shouldCreateNewVersion)
                {
                    var newVersion = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = current.ContentHashSha256 ?? "unknown",
                        SizeBytes = current.SizeBytes,
                        LastWriteUtc = current.LastWriteUtc,
                        IsDeletionMarker = false,
                        CreatedAt = scannedAtUtc,
                        IsDeleted = false,
                        DeletedAt = null
                    };

                    var absolutePath = ToAbsolutePath(repo.Directory.Path, path);
                    var stored = await TryStoreBlocksAsync(absolutePath, throwOnFailure: true, ct);
                    if (stored is null || (current.SizeBytes > 0 && stored.Blocks.Count == 0))
                        throw new InvalidOperationException($"Failed to store blocks for file {path}.");

                    pendingBlocks[newVersion] = stored.Blocks;
                    newVersions.Add(newVersion);
                    latestVersionsByIdentityId[identity.Id] = newVersion;
                    selectedVersion = newVersion;

                    if (previousVersionForDiff is { Id: > 0, IsDeletionMarker: false }
                        && latestHasBlocks
                        && CanBuildTextDiff(current.Extension))
                    {
                        pendingPrecomputedDiffs.Add(new PendingTextDiffPrecompute(
                            path,
                            previousVersionForDiff.Id,
                            newVersion));
                    }
                }
            }
            else
            {
                identity.IsDeleted = planEntry?.ShouldMarkIdentityDeleted ?? true;
                identity.DeletedAt = identity.IsDeleted ? scannedAtUtc : null;
                identity.UpdatedAt = scannedAtUtc;

                var hasLatest = latestVersionsByIdentityId.TryGetValue(identity.Id, out selectedVersion!);
                var shouldCreateDeletionMarker = planEntry?.ShouldCreateNewVersion
                                                ?? (!hasLatest
                                                    || !selectedVersion.IsDeletionMarker);

                if (!hasLatest && !shouldCreateDeletionMarker)
                    shouldCreateDeletionMarker = true;

                if (shouldCreateDeletionMarker)
                {
                    selectedVersion = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = previous.Hash ?? "deleted",
                        SizeBytes = previous.SizeBytes,
                        LastWriteUtc = scannedAtUtc,
                        IsDeletionMarker = true,
                        CreatedAt = scannedAtUtc,
                        IsDeleted = false,
                        DeletedAt = null
                    };

                    newVersions.Add(selectedVersion);
                    latestVersionsByIdentityId[identity.Id] = selectedVersion;
                }
            }
            pendingLinks.Add(new PendingSnapshotFileLink(identity.Id, selectedVersion));
        }
        versionBuildMs = stageTimer.ElapsedMilliseconds;

        foreach (var pair in pendingBlocks)
        {
            var version = pair.Key;
            foreach (var block in pair.Value.OrderBy(b => b.Sequence))
            {
                version.Blocks.Add(new FileVersionBlock
                {
                    Sequence = block.Sequence,
                    BlockHashBlake3 = block.BlockHashBlake3,
                    LengthBytes = block.LengthBytes,
                    StoredSizeBytes = block.StoredSizeBytes,
                    CreatedAt = scannedAtUtc,
                    IsDeleted = false,
                    DeletedAt = null
                });
            }
        }

        if (newVersions.Count > 0)
            db.AddRange(newVersions);

        repo.FileCount = fileEntries;
        repo.VersionCount += newVersions.Count;
        repo.TotalSizeBytes = totalFileBytes;
        repo.LastScannedAt = scannedAtUtc;
        repo.UpdatedAt = scannedAtUtc;

        stageTimer.Restart();
        await db.SaveChangesAsync(ct);
        versionPersistMs = stageTimer.ElapsedMilliseconds;

        stageTimer.Restart();
        await InsertSnapshotFileLinksAsync(snapshot.Id, pendingLinks, scannedAtUtc, ct);
        linkPersistMs = stageTimer.ElapsedMilliseconds;

        if (pendingPrecomputedDiffs.Count > 0)
        {
            stageTimer.Restart();
            await PrecomputeSnapshotDiffsAsync(pendingPrecomputedDiffs, ct);
            diffPrecomputeMs = stageTimer.ElapsedMilliseconds;
        }

        await tx.CommitAsync(ct);

        var totalBlockRefs = pendingBlocks.Values.Sum(v => v.Count);

        log.LogInformation(
            "Snapshot saved for repository {RepositoryId}. Entries {Entries}. Files {Files}. FileIdentities {FileIdentities}. NewVersions {NewVersions}. Links {Links}. BlockRefs {BlockRefs}. Trigger {Trigger}. PreparationMs {PreparationMs}. SnapshotHeaderMs {SnapshotHeaderMs}. SnapshotEntriesMs {SnapshotEntriesMs}. IdentityPreparationMs {IdentityPreparationMs}. VersionBuildMs {VersionBuildMs}. VersionPersistMs {VersionPersistMs}. LinkPersistMs {LinkPersistMs}. DiffPrecomputeMs {DiffPrecomputeMs}. TotalMs {TotalMs}",
            repositoryId,
            totalEntries,
            fileEntries,
            identitiesByPath.Count,
            newVersions.Count,
            pendingLinks.Count,
            totalBlockRefs,
            safeTrigger,
            preparationMs,
            snapshotHeaderMs,
            snapshotEntriesMs,
            identityPreparationMs,
            versionBuildMs,
            versionPersistMs,
            linkPersistMs,
            diffPrecomputeMs,
            overallTimer.ElapsedMilliseconds);

        return SnapshotSaveResultDto.Created();
    }

    private async Task InsertSnapshotEntriesAsync(
        long snapshotId,
        int repositoryId,
        IReadOnlyCollection<RepositoryScanEntryDto> entries,
        DateTime createdAtUtc,
        CancellationToken ct)
    {
        if (entries.Count == 0)
            return;

        if (!db.Database.IsSqlite())
        {
            var rows = entries.Select(e => new RepositorySnapshotEntry
            {
                SnapshotId = snapshotId,
                RepositoryId = repositoryId,
                RelativePath = e.RelativePath,
                ParentRelativePath = e.ParentRelativePath,
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Extension = e.Extension,
                SizeBytes = e.SizeBytes,
                LastWriteUtc = e.LastWriteUtc,
                ContentHashSha256 = e.ContentHashSha256,
                CreatedAt = createdAtUtc,
                IsDeleted = false,
                DeletedAt = null
            }).ToList();

            db.AddRange(rows);
            return;
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var entryList = entries as IList<RepositoryScanEntryDto> ?? entries.ToList();

        for (var offset = 0; offset < entryList.Count; offset += SnapshotEntryInsertBatchSize)
        {
            var chunk = entryList.Skip(offset).Take(SnapshotEntryInsertBatchSize).ToList();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;

            var sql = new StringBuilder();
            sql.Append("INSERT INTO \"RepositorySnapshotEntries\" (");
            sql.Append("\"ContentHashSha256\", \"CreatedAt\", \"DeletedAt\", \"Extension\", \"IsDirectory\", ");
            sql.Append("\"LastWriteUtc\", \"Name\", \"ParentRelativePath\", \"RelativePath\", \"RepositoryId\", ");
            sql.Append("\"SizeBytes\", \"SnapshotId\", \"IsDeleted\") VALUES ");

            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0)
                    sql.Append(", ");

                sql.Append('(');
                sql.AppendJoin(", ", new[]
                {
                    AddParameter(command, $"@p{i}_hash", chunk[i].ContentHashSha256),
                    AddParameter(command, $"@p{i}_created", createdAtUtc),
                    AddParameter(command, $"@p{i}_deleted", null),
                    AddParameter(command, $"@p{i}_ext", chunk[i].Extension),
                    AddParameter(command, $"@p{i}_isdir", chunk[i].IsDirectory),
                    AddParameter(command, $"@p{i}_lastwrite", chunk[i].LastWriteUtc),
                    AddParameter(command, $"@p{i}_name", chunk[i].Name),
                    AddParameter(command, $"@p{i}_parent", chunk[i].ParentRelativePath),
                    AddParameter(command, $"@p{i}_relative", chunk[i].RelativePath),
                    AddParameter(command, $"@p{i}_repo", repositoryId),
                    AddParameter(command, $"@p{i}_size", chunk[i].SizeBytes),
                    AddParameter(command, $"@p{i}_snapshot", snapshotId),
                    AddParameter(command, $"@p{i}_isdeleted", false)
                });
                sql.Append(')');
            }

            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync(ct);
        }

        log.LogDebug(
            "Snapshot entries inserted via sqlite batch path. SnapshotId {SnapshotId}. RepositoryId {RepositoryId}. Entries {Entries}. BatchSize {BatchSize}",
            snapshotId,
            repositoryId,
            entryList.Count,
            SnapshotEntryInsertBatchSize);
    }

    private async Task InsertSnapshotFileLinksAsync(
        long snapshotId,
        IReadOnlyCollection<PendingSnapshotFileLink> links,
        DateTime createdAtUtc,
        CancellationToken ct)
    {
        if (links.Count == 0)
            return;

        if (!db.Database.IsSqlite())
        {
            var rows = links.Select(x => new SnapshotFileLink
            {
                SnapshotId = snapshotId,
                FileIdentityId = x.FileIdentityId,
                FileVersionId = x.FileVersion.Id,
                CreatedAt = createdAtUtc,
                IsDeleted = false,
                DeletedAt = null
            }).ToList();

            db.AddRange(rows);
            await db.SaveChangesAsync(ct);
            return;
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var linkList = links as IList<PendingSnapshotFileLink> ?? links.ToList();

        for (var offset = 0; offset < linkList.Count; offset += SnapshotFileLinkInsertBatchSize)
        {
            var chunk = linkList.Skip(offset).Take(SnapshotFileLinkInsertBatchSize).ToList();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;

            var sql = new StringBuilder();
            sql.Append("INSERT INTO \"SnapshotFileLinks\" (");
            sql.Append("\"CreatedAt\", \"DeletedAt\", \"FileIdentityId\", \"FileVersionId\", \"IsDeleted\", \"SnapshotId\") VALUES ");

            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0)
                    sql.Append(", ");

                sql.Append('(');
                sql.AppendJoin(", ", new[]
                {
                    AddParameter(command, $"@l{i}_created", createdAtUtc),
                    AddParameter(command, $"@l{i}_deleted", null),
                    AddParameter(command, $"@l{i}_identity", chunk[i].FileIdentityId),
                    AddParameter(command, $"@l{i}_version", chunk[i].FileVersion.Id),
                    AddParameter(command, $"@l{i}_isdeleted", false),
                    AddParameter(command, $"@l{i}_snapshot", snapshotId)
                });
                sql.Append(')');
            }

            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync(ct);
        }

        log.LogDebug(
            "Snapshot file links inserted via sqlite batch path. SnapshotId {SnapshotId}. Links {Links}. BatchSize {BatchSize}",
            snapshotId,
            linkList.Count,
            SnapshotFileLinkInsertBatchSize);
    }

    private static string AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return name;
    }

    public async Task<IReadOnlyList<RepositoryScanEntryDto>> GetLatestEntriesAsync(
        int repositoryId,
        CancellationToken ct = default)
    {
        var snapshotId = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (snapshotId == 0)
            return Array.Empty<RepositoryScanEntryDto>();

        return await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == snapshotId && !e.IsDeleted)
            .OrderBy(e => e.RelativePath)
            .Select(e => new RepositoryScanEntryDto(
                e.RelativePath,
                e.ParentRelativePath,
                e.Name,
                e.IsDirectory,
                e.Extension,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FileVersionInfoDto>> GetFileVersionsAsync(
        int repositoryId,
        string relativePath,
        int take = 50,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0 || string.IsNullOrWhiteSpace(relativePath))
            return Array.Empty<FileVersionInfoDto>();

        var normalizedPath = NormalizeRelativePath(relativePath);

        var identity = await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.RepositoryId == repositoryId && i.RelativePath == normalizedPath && !i.Repository.IsDeleted, ct);

        if (identity is null)
            return Array.Empty<FileVersionInfoDto>();

        var limit = Math.Clamp(take, 1, 500);

        return await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .Where(v => v.FileIdentityId == identity.Id && !v.IsDeleted)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .Take(limit)
            .Select(v => new FileVersionInfoDto(
                v.Id,
                identity.RelativePath,
                identity.Extension,
                v.CreatedAt,
                v.SizeBytes,
                v.IsDeletionMarker,
                v.ContentHashSha256,
                v.SizeBytes == 0 || v.Blocks.Any()))
            .ToListAsync(ct);
    }

    public async Task<RepositoryPendingChangesDto> GetPendingChangesAsync(
        int repositoryId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0)
            return RepositoryPendingChangesDto.Empty;

        var latestSnapshot = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (latestSnapshot is null)
            return RepositoryPendingChangesDto.Empty;

        var currentFiles = await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == latestSnapshot.Id && !e.IsDirectory && !e.IsDeleted)
            .Select(e => new SnapshotEntryLight(
                e.RelativePath,
                e.Name,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToListAsync(ct);

        var baselineSnapshot = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        var baselineFiles = baselineSnapshot is null
            ? []
            : await db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == baselineSnapshot.Id && !e.IsDirectory && !e.IsDeleted)
                .Select(e => new SnapshotEntryLight(
                    e.RelativePath,
                    e.Name,
                    e.SizeBytes,
                    e.LastWriteUtc,
                    e.ContentHashSha256))
                .ToListAsync(ct);

        var currentStates = currentFiles
            .Select(ToRepositoryPathStateDto)
            .ToList();
        var baselineStates = baselineFiles
            .Select(ToRepositoryPathStateDto)
            .ToList();
        var limit = Math.Clamp(take, 1, 2000);
        var comparison = await snapshotComparison.CompareRepositoryPathsAsync(currentStates, baselineStates, limit, ct);
        return new RepositoryPendingChangesDto(
            baselineSnapshot?.CreatedAt,
            comparison.AddedCount,
            comparison.ModifiedCount,
            comparison.DeletedCount,
            comparison.Entries);
    }

    public async Task<IReadOnlyList<RepositorySnapshotHistoryItemDto>> GetSnapshotHistoryAsync(
        int repositoryId,
        int take = 100,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0)
            return Array.Empty<RepositorySnapshotHistoryItemDto>();

        var limit = Math.Clamp(take, 1, 500);

        var snapshots = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(limit + 1)
            .Select(s => new SnapshotLight(
                s.Id,
                s.Title,
                s.CreatedAt,
                s.Trigger))
            .ToListAsync(ct);

        if (snapshots.Count == 0)
            return Array.Empty<RepositorySnapshotHistoryItemDto>();

        var snapshotIds = snapshots.Select(s => s.SnapshotId).ToList();

        var linkRows = await db.Set<SnapshotFileLink>()
                        .IgnoreQueryFilters()
            .Where(l => snapshotIds.Contains(l.SnapshotId) && !l.IsDeleted && !l.Snapshot.IsDeleted && !l.FileVersion.IsDeleted && !l.FileIdentity.Repository.IsDeleted)
            .Select(l => new SnapshotLinkState(
                l.SnapshotId,
                l.FileIdentityId,
                l.FileVersionId,
                l.FileVersion != null && l.FileVersion.IsDeletionMarker,
                l.FileVersion != null ? l.FileVersion.SizeBytes : 0,
                l.FileVersion != null ? l.FileVersion.CreatedAt : DateTime.MinValue,
                l.FileIdentity.RelativePath,
                l.FileIdentity.Name))
            .ToListAsync(ct);

        var statesBySnapshot = linkRows
            .GroupBy(l => l.SnapshotId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<SnapshotLinkStateDto>)g.Select(ToSnapshotLinkStateDto).ToList());

        var result = new List<RepositorySnapshotHistoryItemDto>(Math.Min(limit, snapshots.Count));

        for (var i = 0; i < snapshots.Count && result.Count < limit; i++)
        {
            var current = snapshots[i];
            var previous = i + 1 < snapshots.Count ? snapshots[i + 1] : null;

            var currentStates = statesBySnapshot.GetValueOrDefault(current.SnapshotId, EmptySnapshotLinkStates);
            var previousStates = previous is null
                ? EmptySnapshotLinkStates
                : statesBySnapshot.GetValueOrDefault(previous.SnapshotId, EmptySnapshotLinkStates);

            var comparison = await snapshotComparison.CompareSnapshotLinksAsync(currentStates, previousStates, ct);

            result.Add(new RepositorySnapshotHistoryItemDto(
                current.SnapshotId,
                current.Title,
                current.CreatedAtUtc,
                current.Trigger,
                comparison.ChangedFilesCount));
        }

        return result;
    }

    public async Task<IReadOnlyList<RepositorySnapshotFileChangeDto>> GetSnapshotChangedFilesAsync(
        int repositoryId,
        long snapshotId,
        int take = 1000,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0 || snapshotId <= 0)
            return Array.Empty<RepositorySnapshotFileChangeDto>();

        var limit = Math.Clamp(take, 1, 5000);

        var versionedSnapshotIds = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId && !s.IsDeleted)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id && !l.IsDeleted))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var index = versionedSnapshotIds.FindIndex(id => id == snapshotId);
        if (index < 0)
            return Array.Empty<RepositorySnapshotFileChangeDto>();

        var previousSnapshotId = index + 1 < versionedSnapshotIds.Count
            ? versionedSnapshotIds[index + 1]
            : 0;

        var currentRows = await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => l.SnapshotId == snapshotId && !l.IsDeleted && !l.Snapshot.IsDeleted && !l.FileVersion.IsDeleted && !l.FileIdentity.Repository.IsDeleted)
            .Select(l => new SnapshotLinkState(
                l.SnapshotId,
                l.FileIdentityId,
                l.FileVersionId,
                l.FileVersion != null && l.FileVersion.IsDeletionMarker,
                l.FileVersion != null ? l.FileVersion.SizeBytes : 0,
                l.FileVersion != null ? l.FileVersion.CreatedAt : DateTime.MinValue,
                l.FileIdentity.RelativePath,
                l.FileIdentity.Name))
            .ToListAsync(ct);

        var previousRows = previousSnapshotId == 0
            ? []
            : await db.Set<SnapshotFileLink>()
                .IgnoreQueryFilters()
                .Where(l => l.SnapshotId == previousSnapshotId && !l.IsDeleted && !l.Snapshot.IsDeleted && !l.FileVersion.IsDeleted && !l.FileIdentity.Repository.IsDeleted)
                .Select(l => new SnapshotLinkState(
                    l.SnapshotId,
                    l.FileIdentityId,
                    l.FileVersionId,
                    l.FileVersion != null && l.FileVersion.IsDeletionMarker,
                    l.FileVersion != null ? l.FileVersion.SizeBytes : 0,
                    l.FileVersion != null ? l.FileVersion.CreatedAt : DateTime.MinValue,
                    l.FileIdentity.RelativePath,
                    l.FileIdentity.Name))
                .ToListAsync(ct);

        var currentStates = currentRows.Select(ToSnapshotLinkStateDto).ToList();
        var previousStates = previousRows.Select(ToSnapshotLinkStateDto).ToList();

        var comparison = await snapshotComparison.CompareSnapshotLinksAsync(currentStates, previousStates, ct);

        return comparison.Changes
            .Take(limit)
            .Select(c => new RepositorySnapshotFileChangeDto(
                snapshotId,
                c.FileIdentityId,
                c.FileVersionId,
                c.RelativePath,
                c.Name,
                c.ChangeKind,
                c.CurrentSizeBytes,
                c.PreviousSizeBytes,
                c.VersionCreatedAtUtc))
            .ToList();
    }

    public async Task<PendingFileDiffPreviewDto> GetPendingFileDiffPreviewAsync(
        int repositoryId,
        string relativePath,
        int maxLines = 3000,
        CancellationToken ct = default)
    {
        if (repositoryId <= 0 || string.IsNullOrWhiteSpace(relativePath))
            return PendingFileDiffPreviewDto.Unavailable(relativePath ?? string.Empty, "Invalid preview request.");

        var normalizedPath = NormalizeRelativePath(relativePath);
        var normalizedMaxLines = NormalizeMaxLines(maxLines);

        var repo = await db.Set<Repository>()
            .AsNoTracking()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted && !r.Directory.IsDeleted, ct);

        if (repo is null)
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Repository was not found.");

        var absolutePath = ToAbsolutePath(repo.Directory.Path, normalizedPath);

        if (!File.Exists(absolutePath))
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Current file is missing on disk.");

        if (Directory.Exists(absolutePath))
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Selected path is a directory.");

        var extension = Path.GetExtension(absolutePath);

        var identity = await db.Set<FileIdentity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.RepositoryId == repositoryId
                     && i.RelativePath == normalizedPath
                     && !i.IsDeleted,
                ct);

        if (identity is null)
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "No baseline version found for this file.");

        var baselineVersion = await db.Set<FileVersion>()
            .AsNoTracking()
            .Where(v => v.FileIdentityId == identity.Id && !v.IsDeletionMarker && !v.IsDeleted)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(ct);

        if (baselineVersion is null)
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "No previous content version found.");

        var baselineBlocks = baselineVersion.SizeBytes == 0
            ? []
            : await db.Set<FileVersionBlock>()
                .AsNoTracking()
                .Where(b => b.FileVersionId == baselineVersion.Id && !b.IsDeleted)
                .OrderBy(b => b.Sequence)
                .Select(b => new StoredFileBlockDto(
                    b.Sequence,
                    b.BlockHashBlake3,
                    b.LengthBytes,
                    b.StoredSizeBytes))
                .ToListAsync(ct);

        if (baselineVersion.SizeBytes > 0 && baselineBlocks.Count == 0)
            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Baseline version blocks are missing.");

        string? imageTempToCleanup = null;

        try
        {

            var currentDigest = await ComputeCurrentFileDigestAsync(absolutePath, ManagedPreviewChunkSize, ct);
            var binarySummary = BuildPendingBinarySummary(
                baselineVersion.SizeBytes,
                baselineVersion.ContentHashSha256,
                baselineBlocks,
                currentDigest);

            if (CanBuildTextDiff(extension))
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "pending-diff-preview");
                Directory.CreateDirectory(tempDir);

                var baselineTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.baseline.tmp");

                try
                {
                    if (baselineVersion.SizeBytes == 0)
                    {
                        await File.WriteAllTextAsync(baselineTemp, string.Empty, ct);
                    }
                    else
                    {
                        await contentStore.RestoreFileAsync(baselineBlocks, baselineTemp, true, ct);
                    }

                    var computed = await BuildDiffForPreviewAsync(
                        baselineTemp,
                        absolutePath,
                        extension,
                        normalizedMaxLines,
                        ct);

                    var summary = $"{normalizedPath}   +{computed.AddedLines} / -{computed.RemovedLines}" +
                                  (computed.IsTruncated ? "  (truncated)" : string.Empty);

                    return PendingFileDiffPreviewDto.FromText(
                        relativePath: normalizedPath,
                        message: summary,
                        addedLines: computed.AddedLines,
                        removedLines: computed.RemovedLines,
                        isTruncated: computed.IsTruncated,
                        lines: computed.Lines,
                        hunks: computed.Hunks);
                }
                finally
                {
                    TryDelete(baselineTemp);
                }
            }

            if (IsImageExtension(extension))
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "pending-image-preview");
                Directory.CreateDirectory(tempDir);

                var baselineExt = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension;
                var baselineTempImage = Path.Combine(tempDir, $"{Guid.NewGuid():N}.baseline{baselineExt}");
                imageTempToCleanup = baselineTempImage;

                if (baselineVersion.SizeBytes > 0)
                {
                    await contentStore.RestoreFileAsync(baselineBlocks, baselineTempImage, true, ct);
                }
                else
                {
                    await File.WriteAllBytesAsync(baselineTempImage, [], ct);
                }

                var byteSimilarity = await ComputeByteSimilarityAsync(baselineTempImage, absolutePath, ct);

                binarySummary = binarySummary with { ByteSimilarityRatio = byteSimilarity };

                var baselineSize = TryReadImageDimensions(baselineTempImage);
                var currentSize = TryReadImageDimensions(absolutePath);

                var overlay = await ImageDiffOverlayBuilder.TryBuildAsync(baselineTempImage, absolutePath, ct);
                var imagePreview = new PendingImageDiffPreviewDto
                {
                    BaselineImagePath = baselineTempImage,
                    IsBaselineTempFile = true,
                    CurrentImagePath = absolutePath,
                    IsCurrentTempFile = false,
                    OverlayImagePath = overlay?.OverlayImagePath,
                    IsOverlayTempFile = overlay?.IsOverlayTempFile ?? false,
                    BaselineWidth = baselineSize?.Width,
                    BaselineHeight = baselineSize?.Height,
                    CurrentWidth = currentSize?.Width,
                    CurrentHeight = currentSize?.Height,
                    HasDimensionMismatch = baselineSize.HasValue
                                           && currentSize.HasValue
                                           && (baselineSize.Value.Width != currentSize.Value.Width
                                               || baselineSize.Value.Height != currentSize.Value.Height),
                    SimilarityRatio = byteSimilarity,
                    ChangedPixelCount = overlay?.ChangedPixelCount ?? 0,
                    ChangedPixelRatio = overlay?.ChangedPixelRatio,
                    ChangedRegionCount = overlay?.ChangedRegionCount ?? 0
                };

                var imageMessage = BuildBinaryPreviewMessage(normalizedPath, binarySummary, "image", extension);
                imageTempToCleanup = null;
                return PendingFileDiffPreviewDto.FromImage(
                    relativePath: normalizedPath,
                    message: imageMessage,
                    binarySummary: binarySummary,
                    imagePreview: imagePreview);
            }

            var binaryMessage = BuildBinaryPreviewMessage(normalizedPath, binarySummary, "binary", extension);
            return PendingFileDiffPreviewDto.FromBinary(
                relativePath: normalizedPath,
                message: binaryMessage,
                binarySummary: binarySummary);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(imageTempToCleanup))
                TryDelete(imageTempToCleanup);

            log.LogWarning(
                ex,
                "Failed to build pending file diff preview. RepositoryId {RepositoryId}. Path {Path}",
                repositoryId,
                normalizedPath);

            return PendingFileDiffPreviewDto.Unavailable(normalizedPath, "Unable to build diff preview for selected file.");
        }
    }
    public async Task<PendingFileDiffPreviewDto> GetFileVersionDiffPreviewAsync(
        long leftFileVersionId,
        long rightFileVersionId,
        int maxLines = 3000,
        CancellationToken ct = default)
    {
        if (leftFileVersionId <= 0 || rightFileVersionId <= 0)
            return PendingFileDiffPreviewDto.Unavailable(string.Empty, "Both versions must be selected.");

        if (leftFileVersionId == rightFileVersionId)
            return PendingFileDiffPreviewDto.Unavailable(string.Empty, "Select two different versions.");

        var normalizedMaxLines = NormalizeMaxLines(maxLines);

        var left = await GetFileVersionRestoreDataAsync(leftFileVersionId, ct);
        var right = await GetFileVersionRestoreDataAsync(rightFileVersionId, ct);

        if (left is null || right is null)
            return PendingFileDiffPreviewDto.Unavailable(string.Empty, "One of the selected versions was not found.");

        if (!left.RelativePath.Equals(right.RelativePath, StringComparison.OrdinalIgnoreCase))
            return PendingFileDiffPreviewDto.Unavailable(left.RelativePath, "Selected versions belong to different files.");

        if (left.IsDeletionMarker || right.IsDeletionMarker)
            return PendingFileDiffPreviewDto.Unavailable(left.RelativePath, "Diff preview is unavailable for deletion versions.");

        var extension = NormalizeExtension(left.Extension) ?? NormalizeExtension(right.Extension) ?? NormalizeExtension(Path.GetExtension(left.RelativePath));
        var safeExtension = GetSafeTempExtension(extension);

        var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "version-diff-preview");
        Directory.CreateDirectory(tempDir);

        var leftTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.left{safeExtension}");
        var rightTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.right{safeExtension}");

        var keepLeftTemp = false;
        var keepRightTemp = false;

        try
        {
            await RestoreVersionToTempAsync(left, leftTemp, ct);
            await RestoreVersionToTempAsync(right, rightTemp, ct);

            var byteSimilarity = await ComputeByteSimilarityAsync(leftTemp, rightTemp, ct);

            var binarySummary = BuildVersionBinarySummary(left, right)
                with { ByteSimilarityRatio = byteSimilarity };

            if (CanBuildTextDiff(extension))
            {
                var cached = await GetStoredTextDiffAsync(left.FileVersionId, right.FileVersionId, normalizedMaxLines, ct);
                TextDiffResultDto diff;

                if (cached is not null)
                {
                    diff = cached;
                }
                else
                {
                    var computed = await BuildDiffForPreviewAsync(leftTemp, rightTemp, extension, normalizedMaxLines, ct);
                    diff = new TextDiffResultDto(
                        left.RelativePath,
                        left.FileVersionId,
                        right.FileVersionId,
                        computed.AddedLines,
                        computed.RemovedLines,
                        computed.IsTruncated,
                        computed.Lines,
                        computed.Hunks);

                    try
                    {
                        await SaveStoredTextDiffAsync(diff, normalizedMaxLines, ct);
                    }
                    catch (Exception cacheEx)
                    {
                        log.LogWarning(
                            cacheEx,
                            "Failed to persist text diff cache. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                            left.FileVersionId,
                            right.FileVersionId);
                    }
                }

                var textMessage = $"{left.RelativePath}   +{diff.AddedLines} / -{diff.RemovedLines}" +
                                  (diff.IsTruncated ? "  (truncated)" : string.Empty);

                return PendingFileDiffPreviewDto.FromText(
                    relativePath: left.RelativePath,
                    message: textMessage,
                    addedLines: diff.AddedLines,
                    removedLines: diff.RemovedLines,
                    isTruncated: diff.IsTruncated,
                    lines: diff.Lines,
                    hunks: diff.Hunks);
            }

            if (IsImageExtension(extension))
            {
                var baselineSize = TryReadImageDimensions(leftTemp);
                var currentSize = TryReadImageDimensions(rightTemp);

                var overlay = await ImageDiffOverlayBuilder.TryBuildAsync(leftTemp, rightTemp, ct);
                var imagePreview = new PendingImageDiffPreviewDto
                {
                    BaselineImagePath = leftTemp,
                    IsBaselineTempFile = true,
                    CurrentImagePath = rightTemp,
                    IsCurrentTempFile = true,
                    OverlayImagePath = overlay?.OverlayImagePath,
                    IsOverlayTempFile = overlay?.IsOverlayTempFile ?? false,
                    BaselineWidth = baselineSize?.Width,
                    BaselineHeight = baselineSize?.Height,
                    CurrentWidth = currentSize?.Width,
                    CurrentHeight = currentSize?.Height,
                    HasDimensionMismatch = baselineSize.HasValue
                                           && currentSize.HasValue
                                           && (baselineSize.Value.Width != currentSize.Value.Width
                                               || baselineSize.Value.Height != currentSize.Value.Height),
                    SimilarityRatio = byteSimilarity,
                    ChangedPixelCount = overlay?.ChangedPixelCount ?? 0,
                    ChangedPixelRatio = overlay?.ChangedPixelRatio,
                    ChangedRegionCount = overlay?.ChangedRegionCount ?? 0
                };

                var imageMessage = BuildBinaryPreviewMessage(left.RelativePath, binarySummary, "image", extension);
                keepLeftTemp = true;
                keepRightTemp = true;

                return PendingFileDiffPreviewDto.FromImage(
                    relativePath: left.RelativePath,
                    message: imageMessage,
                    binarySummary: binarySummary,
                    imagePreview: imagePreview);
            }

            var binaryMessage = BuildBinaryPreviewMessage(left.RelativePath, binarySummary, "binary", extension);
            return PendingFileDiffPreviewDto.FromBinary(
                relativePath: left.RelativePath,
                message: binaryMessage,
                binarySummary: binarySummary);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Failed to build file-version diff preview. LeftVersion {LeftVersion}. RightVersion {RightVersion}. Path {Path}",
                leftFileVersionId,
                rightFileVersionId,
                left.RelativePath);

            return PendingFileDiffPreviewDto.Unavailable(left.RelativePath, FormatVersionPreviewError(ex));
        }
        finally
        {
            if (!keepLeftTemp)
                TryDelete(leftTemp);

            if (!keepRightTemp)
                TryDelete(rightTemp);
        }
    }
    public async Task<TextDiffResultDto?> GetStoredTextDiffAsync(
        long leftFileVersionId,
        long rightFileVersionId,
        int maxLines,
        CancellationToken ct = default)
    {
        if (leftFileVersionId <= 0 || rightFileVersionId <= 0)
            return null;

        var normalizedMaxLines = NormalizeMaxLines(maxLines);

        var row = await db.Set<FileVersionTextDiff>()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => !d.IsDeleted && d.LeftFileVersionId == leftFileVersionId
                                      && d.RightFileVersionId == rightFileVersionId
                                      && d.MaxLines == normalizedMaxLines,
                ct);

        if (row is null)
            return null;

        if (row.StorageFormatVersion != CurrentTextDiffStorageFormatVersion)
        {
            await InvalidateTextDiffCacheRowAsync(row.Id, ct);
            return null;
        }

        var lineRows = await db.Set<FileVersionTextDiffLine>()
            .AsNoTracking()
            .Where(l => l.DiffId == row.Id && !l.IsDeleted)
            .OrderBy(l => l.Sequence)
            .Select(l => new TextDiffLineDto(
                l.Kind,
                l.LeftLineNumber,
                l.RightLineNumber,
                l.TextLineAtom.Text))
            .ToListAsync(ct);

        if (lineRows.Count == 0)
        {
            if (row.AddedLines == 0 && row.RemovedLines == 0)
            {
                return new TextDiffResultDto(
                    row.RelativePath,
                    row.LeftFileVersionId,
                    row.RightFileVersionId,
                    row.AddedLines,
                    row.RemovedLines,
                    row.IsTruncated,
                    lineRows,
                    Array.Empty<TextDiffHunkDto>());
            }

            await InvalidateTextDiffCacheRowAsync(row.Id, ct);
            return null;
        }

        var hunkRows = await db.Set<FileVersionTextDiffHunk>()
            .AsNoTracking()
            .Where(h => h.DiffId == row.Id && !h.IsDeleted)
            .OrderBy(h => h.Sequence)
            .Select(h => new TextDiffHunkDto(
                h.Sequence,
                h.StartLineSequence,
                h.EndLineSequence,
                h.OldStartLine,
                h.OldLineCount,
                h.NewStartLine,
                h.NewLineCount,
                h.ChangeKind))
            .ToListAsync(ct);

        if (hunkRows.Count == 0)
        {
            if (row.AddedLines == 0 && row.RemovedLines == 0)
            {
                return new TextDiffResultDto(
                    row.RelativePath,
                    row.LeftFileVersionId,
                    row.RightFileVersionId,
                    row.AddedLines,
                    row.RemovedLines,
                    row.IsTruncated,
                    lineRows,
                    Array.Empty<TextDiffHunkDto>());
            }

            await InvalidateTextDiffCacheRowAsync(row.Id, ct);
            return null;
        }

        return new TextDiffResultDto(
            row.RelativePath,
            row.LeftFileVersionId,
            row.RightFileVersionId,
            row.AddedLines,
            row.RemovedLines,
            row.IsTruncated,
            lineRows,
            hunkRows);
    }

    public async Task SaveStoredTextDiffAsync(
        TextDiffResultDto diff,
        int maxLines,
        CancellationToken ct = default)
    {
        await SaveStoredTextDiffCoreAsync(diff, maxLines, DateTime.UtcNow, ct);
    }

    private async Task SaveStoredTextDiffCoreAsync(
        TextDiffResultDto diff,
        int maxLines,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (diff.LeftFileVersionId <= 0 || diff.RightFileVersionId <= 0)
            return;

        var normalizedMaxLines = NormalizeMaxLines(maxLines);
        var normalizedLines = (diff.Lines ?? Array.Empty<TextDiffLineDto>())
            .Select(line => new TextDiffLineDto(
                NormalizeDiffKind(line.Kind),
                line.LeftLineNumber,
                line.RightLineNumber,
                line.Text ?? string.Empty))
            .ToList();

        var normalizedHunks = NormalizeHunks(
            diff.Hunks ?? Array.Empty<TextDiffHunkDto>(),
            normalizedLines,
            3);

        var linesJson = JsonSerializer.Serialize(normalizedLines, DiffJsonOptions);
        var diffKey = ComputeDiffKeySha256(diff.LeftFileVersionId, diff.RightFileVersionId, normalizedMaxLines);

        var existing = await db.Set<FileVersionTextDiff>()
            .Include(d => d.Hunks)
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => !d.IsDeleted && d.LeftFileVersionId == diff.LeftFileVersionId
                                      && d.RightFileVersionId == diff.RightFileVersionId
                                      && d.MaxLines == normalizedMaxLines,
                ct);

        if (existing is null)
        {
            existing = new FileVersionTextDiff
            {
                LeftFileVersionId = diff.LeftFileVersionId,
                RightFileVersionId = diff.RightFileVersionId,
                MaxLines = normalizedMaxLines,
                CreatedAt = nowUtc,
                IsDeleted = false,
                DeletedAt = null
            };

            db.Add(existing);
        }

        existing.DiffKeySha256 = diffKey;
        existing.RelativePath = TruncateForColumn(diff.RelativePath, 2048);
        existing.AddedLines = diff.AddedLines;
        existing.RemovedLines = diff.RemovedLines;
        existing.IsTruncated = diff.IsTruncated;
        existing.LinesJson = linesJson;
        existing.StorageFormatVersion = CurrentTextDiffStorageFormatVersion;
        existing.IsDeleted = false;
        existing.DeletedAt = null;
        existing.UpdatedAt = nowUtc;

        if (existing.Lines.Count > 0)
            db.RemoveRange(existing.Lines);

        if (existing.Hunks.Count > 0)
            db.RemoveRange(existing.Hunks);

        var createdHunks = new List<FileVersionTextDiffHunk>(normalizedHunks.Count);
        var hunkBySequence = new Dictionary<int, FileVersionTextDiffHunk>();

        if (normalizedHunks.Count > 0)
        {
            foreach (var h in normalizedHunks)
            {
                var entity = new FileVersionTextDiffHunk
                {
                    Diff = existing,
                    Sequence = h.Sequence,
                    StartLineSequence = h.StartLineSequence,
                    EndLineSequence = h.EndLineSequence,
                    OldStartLine = h.OldStartLine,
                    OldLineCount = h.OldLineCount,
                    NewStartLine = h.NewStartLine,
                    NewLineCount = h.NewLineCount,
                    ChangeKind = NormalizeHunkChangeKind(h.ChangeKind),
                    CreatedAt = nowUtc,
                    IsDeleted = false,
                    DeletedAt = null
                };

                createdHunks.Add(entity);
                hunkBySequence[h.Sequence] = entity;
            }

            db.AddRange(createdHunks);
        }

        if (normalizedLines.Count > 0)
        {
            var uniqueTexts = normalizedLines
                .Select(l => l.Text)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var textByHash = uniqueTexts
                .Select(text => new { Text = text, Hash = ComputeLineHashSha256(text) })
                .ToList();

            var hashes = textByHash.Select(x => x.Hash).Distinct(StringComparer.Ordinal).ToList();

            var existingAtoms = hashes.Count == 0
                ? []
                : await db.Set<TextLineAtom>()
                    .Where(a => hashes.Contains(a.HashSha256))
                    .ToListAsync(ct);

            var atomsByKey = existingAtoms.ToDictionary(
                a => (a.HashSha256, a.Text),
                a => a);

            var atomsByText = new Dictionary<string, TextLineAtom>(StringComparer.Ordinal);

            foreach (var item in textByHash)
            {
                if (!atomsByKey.TryGetValue((item.Hash, item.Text), out var atom))
                {
                    atom = new TextLineAtom
                    {
                        HashSha256 = item.Hash,
                        Text = item.Text,
                        CreatedAt = nowUtc
                    };

                    db.Add(atom);
                    atomsByKey[(item.Hash, item.Text)] = atom;
                }

                atomsByText[item.Text] = atom;
            }

            var lineToHunk = new Dictionary<int, (FileVersionTextDiffHunk Hunk, int InHunkSequence)>();
            foreach (var h in normalizedHunks)
            {
                if (!hunkBySequence.TryGetValue(h.Sequence, out var hunkEntity))
                    continue;

                var inSeq = 0;
                for (var seq = h.StartLineSequence; seq <= h.EndLineSequence; seq++)
                {
                    lineToHunk[seq] = (hunkEntity, inSeq);
                    inSeq++;
                }
            }

            var diffLines = new List<FileVersionTextDiffLine>(normalizedLines.Count);

            for (var i = 0; i < normalizedLines.Count; i++)
            {
                var line = normalizedLines[i];
                var hasHunk = lineToHunk.TryGetValue(i, out var hunkRef);

                diffLines.Add(new FileVersionTextDiffLine
                {
                    Diff = existing,
                    Sequence = i,
                    Kind = line.Kind,
                    LeftLineNumber = line.LeftLineNumber,
                    RightLineNumber = line.RightLineNumber,
                    Hunk = hasHunk ? hunkRef.Hunk : null,
                    InHunkSequence = hasHunk ? hunkRef.InHunkSequence : null,
                    TextLineAtom = atomsByText[line.Text],
                    CreatedAt = nowUtc,
                    IsDeleted = false,
                    DeletedAt = null
                });
            }

            db.AddRange(diffLines);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<FileVersionRestoreDto?> GetFileVersionRestoreDataAsync(
        long fileVersionId,
        CancellationToken ct = default)
    {
        var version = await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .Include(v => v.FileIdentity)
            .ThenInclude(i => i.Repository)
            .FirstOrDefaultAsync(v => v.Id == fileVersionId && !v.IsDeleted && !v.FileIdentity.Repository.IsDeleted, ct);

        if (version is null)
            return null;

        var blocks = await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => b.FileVersionId == fileVersionId && !b.IsDeleted)
            .OrderBy(b => b.Sequence)
            .Select(b => new StoredFileBlockDto(
                b.Sequence,
                b.BlockHashBlake3,
                b.LengthBytes,
                b.StoredSizeBytes))
            .ToListAsync(ct);

        return new FileVersionRestoreDto(
            version.Id,
            version.FileIdentity.RepositoryId,
            version.FileIdentity.RelativePath,
            version.FileIdentity.Extension,
            version.SizeBytes,
            version.IsDeletionMarker,
            version.ContentHashSha256,
            blocks);
    }


    private async Task PrecomputeSnapshotDiffsAsync(
        IReadOnlyCollection<PendingTextDiffPrecompute> items,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var left = await GetFileVersionRestoreDataAsync(item.LeftFileVersionId, ct);
                var right = await GetFileVersionRestoreDataAsync(item.RightFileVersion.Id, ct);

                if (left is null || right is null)
                    continue;

                if (left.IsDeletionMarker || right.IsDeletionMarker)
                    continue;

                if (!HasStoredContent(left) || !HasStoredContent(right))
                    continue;

                if (!CanBuildTextDiff(right.Extension))
                    continue;

                var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "diff-precompute");
                Directory.CreateDirectory(tempDir);

                var leftTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.left.tmp");
                var rightTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.right.tmp");

                try
                {
                    await contentStore.RestoreFileAsync(left.Blocks, leftTemp, true, ct);
                    await contentStore.RestoreFileAsync(right.Blocks, rightTemp, true, ct);

                    var extension = NormalizeExtension(right.Extension) ?? NormalizeExtension(left.Extension);
                    var computed = await BuildDiffForPreviewAsync(leftTemp, rightTemp, extension, PrecomputedDiffMaxLines, ct);

                    var diff = new TextDiffResultDto(
                        item.RelativePath,
                        left.FileVersionId,
                        right.FileVersionId,
                        computed.AddedLines,
                        computed.RemovedLines,
                        computed.IsTruncated,
                        computed.Lines,
                        computed.Hunks);

                    await SaveStoredTextDiffCoreAsync(diff, PrecomputedDiffMaxLines, DateTime.UtcNow, ct);
                }
                finally
                {
                    TryDelete(leftTemp);
                    TryDelete(rightTemp);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    ex,
                    "Failed to precompute snapshot diff. LeftVersion {LeftVersion}. RightVersion {RightVersion}. Path {Path}",
                    item.LeftFileVersionId,
                    item.RightFileVersion.Id,
                    item.RelativePath);
            }
        }
    }

    private static bool HasStoredContent(FileVersionRestoreDto data)
        => data.SizeBytes == 0 || data.Blocks.Count > 0;

    private static bool CanBuildTextDiff(string? extension)
    {
        return IsTextExtension(extension) || WordSemanticProjection.IsWordOoxmlExtension(extension);
    }

    private static bool IsTextExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        return normalized is not null && TextExtensions.Contains(normalized);
    }

    private static bool IsImageExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        return normalized is not null && ImageExtensions.Contains(normalized);
    }

    private static bool IsOfficeDocumentExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        if (normalized is null)
            return false;

        return string.Equals(normalized, ".doc", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, ".docx", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, ".rtf", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, ".odt", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;

        return normalized;
    }

    private static string GetSafeTempExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        if (string.IsNullOrWhiteSpace(normalized))
            return ".tmp";

        if (normalized.Length > 10)
            return ".tmp";

        return normalized;
    }

    private async Task RestoreVersionToTempAsync(
        FileVersionRestoreDto version,
        string tempPath,
        CancellationToken ct)
    {
        if (version.SizeBytes <= 0)
        {
            await File.WriteAllBytesAsync(tempPath, [], ct);
            return;
        }

        if (version.Blocks.Count == 0)
            throw new InvalidOperationException("Blocks are missing for selected version.");

        await contentStore.RestoreFileAsync(version.Blocks, tempPath, true, ct);
    }

    private async Task<TextDiffComputationDto> BuildDiffForPreviewAsync(
        string leftPath,
        string rightPath,
        string? extension,
        int maxLines,
        CancellationToken ct)
    {
        if (!WordSemanticProjection.IsWordOoxmlExtension(extension))
            return await diffEngine.BuildDiffAsync(leftPath, rightPath, maxLines, ct);

        var semanticTempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "word-diff-preview");
        Directory.CreateDirectory(semanticTempDir);

        var leftSemanticTemp = Path.Combine(semanticTempDir, $"{Guid.NewGuid():N}.left.txt");
        var rightSemanticTemp = Path.Combine(semanticTempDir, $"{Guid.NewGuid():N}.right.txt");

        try
        {
            var leftSemanticLines = WordSemanticProjection.ExtractSemanticLines(leftPath);
            var rightSemanticLines = WordSemanticProjection.ExtractSemanticLines(rightPath);

            await File.WriteAllLinesAsync(leftSemanticTemp, leftSemanticLines, Encoding.UTF8, ct);
            await File.WriteAllLinesAsync(rightSemanticTemp, rightSemanticLines, Encoding.UTF8, ct);

            return await diffEngine.BuildDiffAsync(leftSemanticTemp, rightSemanticTemp, maxLines, ct);
        }
        finally
        {
            TryDelete(leftSemanticTemp);
            TryDelete(rightSemanticTemp);
        }
    }
    private static string FormatVersionPreviewError(Exception ex)
    {
        var text = ex.ToString();

        if (text.Contains("native block format", StringComparison.OrdinalIgnoreCase)
            || text.Contains("native block hash", StringComparison.OrdinalIgnoreCase))
        {
            return "Preview is unavailable right now: selected versions use native block format, but native restore entrypoints are unavailable. Rebuild/update veyra_core and run Reindex data.";
        }

        if (text.Contains("Block file not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("blocks are missing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("missing", StringComparison.OrdinalIgnoreCase))
        {
            return "Preview is unavailable because some block files are missing. Run Repair data or Reindex data.";
        }

        if (text.Contains("decryption", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Artifact key", StringComparison.OrdinalIgnoreCase))
        {
            return "Preview is unavailable because encrypted blocks cannot be decrypted with current keys.";
        }

        return ex.Message;
    }

    private async Task InvalidateTextDiffCacheRowAsync(long diffId, CancellationToken ct)
    {
        if (diffId <= 0)
            return;

        var deletedAt = DateTime.UtcNow;

        try
        {
            await db.Set<FileVersionTextDiffLine>()
                .Where(l => l.DiffId == diffId && !l.IsDeleted)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(l => l.IsDeleted, _ => true)
                    .SetProperty(l => l.DeletedAt, _ => deletedAt), ct);

            await db.Set<FileVersionTextDiffHunk>()
                .Where(h => h.DiffId == diffId && !h.IsDeleted)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(h => h.IsDeleted, _ => true)
                    .SetProperty(h => h.DeletedAt, _ => deletedAt), ct);

            await db.Set<FileVersionTextDiff>()
                .Where(d => d.Id == diffId && !d.IsDeleted)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.IsDeleted, _ => true)
                    .SetProperty(d => d.DeletedAt, _ => deletedAt)
                    .SetProperty(d => d.UpdatedAt, _ => deletedAt), ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to invalidate stale text diff cache row {DiffId}", diffId);
        }
    }
    private static IReadOnlyList<TextDiffHunkDto> NormalizeHunks(
        IReadOnlyList<TextDiffHunkDto> hunks,
        IReadOnlyList<TextDiffLineDto> normalizedLines,
        int contextLines)
    {
        if (normalizedLines.Count == 0)
            return Array.Empty<TextDiffHunkDto>();

        var source = hunks.Count > 0
            ? hunks
            : TextDiffHunkBuilder.Build(normalizedLines, contextLines);

        var normalized = new List<TextDiffHunkDto>(source.Count);

        foreach (var h in source.OrderBy(x => x.Sequence))
        {
            var start = Math.Clamp(h.StartLineSequence, 0, normalizedLines.Count - 1);
            var end = Math.Clamp(h.EndLineSequence, start, normalizedLines.Count - 1);

            normalized.Add(new TextDiffHunkDto(
                Sequence: normalized.Count,
                StartLineSequence: start,
                EndLineSequence: end,
                OldStartLine: Math.Max(0, h.OldStartLine),
                OldLineCount: Math.Max(0, h.OldLineCount),
                NewStartLine: Math.Max(0, h.NewStartLine),
                NewLineCount: Math.Max(0, h.NewLineCount),
                ChangeKind: NormalizeHunkChangeKind(h.ChangeKind)));
        }

        return normalized;
    }

    private static string NormalizeHunkChangeKind(string? kind)
    {
        if (string.Equals(kind, "added", StringComparison.OrdinalIgnoreCase))
            return "added";

        if (string.Equals(kind, "removed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "deleted", StringComparison.OrdinalIgnoreCase))
            return "removed";

        return "modified";
    }
    private static string NormalizeDiffKind(string? kind)
    {
        if (string.Equals(kind, "add", StringComparison.OrdinalIgnoreCase))
            return "add";

        if (string.Equals(kind, "remove", StringComparison.OrdinalIgnoreCase))
            return "remove";

        return "equal";
    }

    private static string ComputeLineHashSha256(string text)
    {
        var payload = text ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static PendingBinaryDiffSummaryDto BuildPendingBinarySummary(
        long baselineSizeBytes,
        string? baselineHashSha256,
        IReadOnlyList<StoredFileBlockDto> baselineBlocks,
        CurrentFileDigest currentDigest)
    {
        var baselineFamily = GetDominantHashFamily(baselineBlocks.Select(b => b.BlockHashBlake3));
        var (sharedBlockCount, dedupRatio, changedBlockRatio) = ComputeBlockOverlapMetrics(
            baselineBlocks.Select(b => b.BlockHashBlake3),
            currentDigest.ChunkHashes,
            baselineFamily,
            currentDigest.HashFamily);

        return new PendingBinaryDiffSummaryDto(
            BaselineSizeBytes: baselineSizeBytes,
            CurrentSizeBytes: currentDigest.SizeBytes,
            SizeDeltaBytes: currentDigest.SizeBytes - baselineSizeBytes,
            BaselineHashSha256: baselineHashSha256 ?? string.Empty,
            CurrentHashSha256: currentDigest.Sha256,
            ChunkSizeBytes: ManagedPreviewChunkSize,
            BaselineBlockCount: baselineBlocks.Count,
            CurrentBlockCount: currentDigest.ChunkHashes.Count,
            SharedBlockCount: sharedBlockCount,
            DedupRatio: dedupRatio,
            ChangedBlockRatio: changedBlockRatio,
            ByteSimilarityRatio: null);
    }

    private static PendingBinaryDiffSummaryDto BuildVersionBinarySummary(
        FileVersionRestoreDto baselineVersion,
        FileVersionRestoreDto currentVersion)
    {
        var baselineFamily = GetDominantHashFamily(baselineVersion.Blocks.Select(b => b.BlockHashBlake3));
        var currentFamily = GetDominantHashFamily(currentVersion.Blocks.Select(b => b.BlockHashBlake3));

        var (sharedBlockCount, dedupRatio, changedBlockRatio) = ComputeBlockOverlapMetrics(
            baselineVersion.Blocks.Select(b => b.BlockHashBlake3),
            currentVersion.Blocks.Select(b => b.BlockHashBlake3),
            baselineFamily,
            currentFamily);

        return new PendingBinaryDiffSummaryDto(
            BaselineSizeBytes: baselineVersion.SizeBytes,
            CurrentSizeBytes: currentVersion.SizeBytes,
            SizeDeltaBytes: currentVersion.SizeBytes - baselineVersion.SizeBytes,
            BaselineHashSha256: baselineVersion.ContentHashSha256,
            CurrentHashSha256: currentVersion.ContentHashSha256,
            ChunkSizeBytes: ManagedPreviewChunkSize,
            BaselineBlockCount: baselineVersion.Blocks.Count,
            CurrentBlockCount: currentVersion.Blocks.Count,
            SharedBlockCount: sharedBlockCount,
            DedupRatio: dedupRatio,
            ChangedBlockRatio: changedBlockRatio,
            ByteSimilarityRatio: null);
    }

    private static (int SharedBlockCount, double? DedupRatio, double? ChangedBlockRatio) ComputeBlockOverlapMetrics(
        IEnumerable<string?> baselineHashes,
        IEnumerable<string?> currentHashes,
        BlockHashFamily baselineFamily,
        BlockHashFamily currentFamily)
    {
        var baselineList = baselineHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .ToList();

        var currentList = currentHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .ToList();

        var baselineBlockCount = baselineList.Count;
        var currentBlockCount = currentList.Count;

        if (currentBlockCount == 0)
        {
            var dedup = baselineBlockCount == 0 ? 1d : 0d;
            return (0, dedup, 1d - dedup);
        }

        if (baselineBlockCount == 0)
            return (0, 0d, 1d);

        if (!CanCompareHashFamilies(baselineFamily, currentFamily))
            return (0, null, null);

        var normalizedBaseline = baselineList
            .Select(h => NormalizeBlockHashForComparison(h, baselineFamily))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalizedCurrent = currentList
            .Select(h => NormalizeBlockHashForComparison(h, currentFamily))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .ToList();

        if (normalizedBaseline.Count == 0 || normalizedCurrent.Count == 0)
            return (0, null, null);

        var sharedBlockCount = 0;
        foreach (var hash in normalizedCurrent)
        {
            if (normalizedBaseline.Contains(hash))
                sharedBlockCount++;
        }

        var dedupRatio = (double)sharedBlockCount / normalizedCurrent.Count;
        return (sharedBlockCount, dedupRatio, 1d - dedupRatio);
    }

    private static bool CanCompareHashFamilies(BlockHashFamily baselineFamily, BlockHashFamily currentFamily)
    {
        if (baselineFamily == BlockHashFamily.Unknown || currentFamily == BlockHashFamily.Unknown)
            return false;

        return baselineFamily == currentFamily;
    }

    private static BlockHashFamily GetDominantHashFamily(IEnumerable<string?> hashes)
    {
        var sha256Count = 0;
        var nativeCount = 0;

        foreach (var hash in hashes)
        {
            switch (DetectHashFamily(hash))
            {
                case BlockHashFamily.Sha256:
                    sha256Count++;
                    break;
                case BlockHashFamily.Native:
                    nativeCount++;
                    break;
            }
        }

        if (sha256Count > 0 && nativeCount == 0)
            return BlockHashFamily.Sha256;

        if (nativeCount > 0 && sha256Count == 0)
            return BlockHashFamily.Native;

        return BlockHashFamily.Unknown;
    }

    private static BlockHashFamily DetectHashFamily(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return BlockHashFamily.Unknown;

        var normalized = hash.Trim();

        if (normalized.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("msha256:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return BlockHashFamily.Sha256;
        }

        if (normalized.StartsWith("blake3:", StringComparison.OrdinalIgnoreCase) || IsHexHash64(normalized))
            return BlockHashFamily.Native;

        return BlockHashFamily.Unknown;
    }

    private static string? NormalizeBlockHashForComparison(string? hash, BlockHashFamily family)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return null;

        var normalized = hash.Trim();

        if (family is BlockHashFamily.Sha256 or BlockHashFamily.Unknown)
        {
            if (normalized.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["sha256-".Length..];
            else if (normalized.StartsWith("msha256:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["msha256:".Length..];
            else if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["sha256:".Length..];
        }

        if (family is BlockHashFamily.Native or BlockHashFamily.Unknown)
        {
            if (normalized.StartsWith("blake3:", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["blake3:".Length..];
        }

        normalized = normalized.Trim();
        return IsHexHash64(normalized) ? normalized.ToLowerInvariant() : null;
    }

    private static bool IsHexHash64(string value)
    {
        if (value.Length != 64)
            return false;

        foreach (var ch in value)
        {
            var isHex = (ch >= '0' && ch <= '9')
                        || (ch >= 'a' && ch <= 'f')
                        || (ch >= 'A' && ch <= 'F');

            if (!isHex)
                return false;
        }

        return true;
    }

    private static async Task<CurrentFileDigest> ComputeCurrentFileDigestAsync(
        string absolutePath,
        int chunkSize,
        CancellationToken ct)
    {
        var chunkHashes = new List<string>();
        var totalBytes = 0L;
        var buffer = new byte[chunkSize];

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: chunkSize,
            useAsync: true);

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0)
                break;

            totalBytes += read;

            var payload = buffer.AsSpan(0, read);
            hasher.AppendData(payload);

            var chunkHash = SHA256.HashData(payload);
            chunkHashes.Add($"msha256:{Convert.ToHexString(chunkHash).ToLowerInvariant()}");
        }

        var fileHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        return new CurrentFileDigest(fileHash, totalBytes, chunkHashes, BlockHashFamily.Sha256);
    }

    private static async Task<double?> ComputeByteSimilarityAsync(
        string baselinePath,
        string currentPath,
        CancellationToken ct)
    {
        if (!File.Exists(baselinePath) || !File.Exists(currentPath))
            return null;

        await using var baseline = new FileStream(
            baselinePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);

        await using var current = new FileStream(
            currentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);

        var comparedLength = Math.Min(baseline.Length, current.Length);
        if (comparedLength <= 0)
            return baseline.Length == current.Length ? 1d : 0d;

        var baselineBuffer = new byte[128 * 1024];
        var currentBuffer = new byte[128 * 1024];

        long comparedBytes = 0;
        long equalBytes = 0;

        while (comparedBytes < comparedLength)
        {
            var toRead = (int)Math.Min(baselineBuffer.Length, comparedLength - comparedBytes);
            var readLeft = await baseline.ReadAsync(baselineBuffer.AsMemory(0, toRead), ct);
            var readRight = await current.ReadAsync(currentBuffer.AsMemory(0, toRead), ct);

            var read = Math.Min(readLeft, readRight);
            if (read <= 0)
                break;

            for (var i = 0; i < read; i++)
            {
                if (baselineBuffer[i] == currentBuffer[i])
                    equalBytes++;
            }

            comparedBytes += read;
        }

        if (comparedBytes <= 0)
            return null;

        return (double)equalBytes / comparedBytes;
    }

    private static (int Width, int Height)? TryReadImageDimensions(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var extension = Path.GetExtension(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                return TryReadPngDimensions(stream);

            if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
                return TryReadJpegDimensions(stream);

            if (string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
                return TryReadBmpDimensions(stream);

            if (string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase))
                return TryReadGifDimensions(stream);

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (int Width, int Height)? TryReadPngDimensions(Stream stream)
    {
        if (stream.Length < 24)
            return null;

        var buffer = new byte[24];
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            return null;

        var isPng = buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47;
        if (!isPng)
            return null;

        var width = (buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19];
        var height = (buffer[20] << 24) | (buffer[21] << 16) | (buffer[22] << 8) | buffer[23];

        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static (int Width, int Height)? TryReadJpegDimensions(Stream stream)
    {
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            return null;

        while (true)
        {
            var markerStart = stream.ReadByte();
            if (markerStart < 0)
                return null;

            if (markerStart != 0xFF)
                continue;

            var marker = stream.ReadByte();
            if (marker < 0)
                return null;

            while (marker == 0xFF)
            {
                marker = stream.ReadByte();
                if (marker < 0)
                    return null;
            }

            if (marker == 0xD9 || marker == 0xDA)
                return null;

            var lengthHigh = stream.ReadByte();
            var lengthLow = stream.ReadByte();
            if (lengthHigh < 0 || lengthLow < 0)
                return null;

            var segmentLength = (lengthHigh << 8) + lengthLow;
            if (segmentLength < 2)
                return null;

            if (IsJpegStartOfFrame(marker))
            {
                _ = stream.ReadByte(); // precision
                var hHigh = stream.ReadByte();
                var hLow = stream.ReadByte();
                var wHigh = stream.ReadByte();
                var wLow = stream.ReadByte();

                if (hHigh < 0 || hLow < 0 || wHigh < 0 || wLow < 0)
                    return null;

                var height = (hHigh << 8) + hLow;
                var width = (wHigh << 8) + wLow;
                return width > 0 && height > 0 ? (width, height) : null;
            }

            stream.Seek(segmentLength - 2, SeekOrigin.Current);
        }
    }

    private static bool IsJpegStartOfFrame(int marker)
        => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
            or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB
            or 0xCD or 0xCE or 0xCF;

    private static (int Width, int Height)? TryReadBmpDimensions(Stream stream)
    {
        if (stream.Length < 26)
            return null;

        var buffer = new byte[26];
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            return null;

        if (buffer[0] != (byte)'B' || buffer[1] != (byte)'M')
            return null;

        var width = BitConverter.ToInt32(buffer, 18);
        var height = Math.Abs(BitConverter.ToInt32(buffer, 22));

        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static (int Width, int Height)? TryReadGifDimensions(Stream stream)
    {
        if (stream.Length < 10)
            return null;

        var buffer = new byte[10];
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            return null;

        var isGif = buffer[0] == (byte)'G' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F';
        if (!isGif)
            return null;

        var width = buffer[6] | (buffer[7] << 8);
        var height = buffer[8] | (buffer[9] << 8);

        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static string BuildBinaryPreviewMessage(
        string relativePath,
        PendingBinaryDiffSummaryDto summary,
        string previewType,
        string? extension = null)
    {
        var kind = string.Equals(previewType, "image", StringComparison.OrdinalIgnoreCase)
            ? "image"
            : "binary";

        var sizeLabel = $"{FormatBytes(summary.BaselineSizeBytes)} -> {FormatBytes(summary.CurrentSizeBytes)} ({FormatSignedBytes(summary.SizeDeltaBytes)})";

        var dedupLabel = summary.DedupRatio.HasValue
            ? $"shared blocks {summary.DedupRatio.Value * 100:F1}%"
            : "shared blocks n/a";

        var changedLabel = summary.ChangedBlockRatio.HasValue
            ? $"changed blocks {summary.ChangedBlockRatio.Value * 100:F1}%"
            : "changed blocks n/a";

        var similarityLabel = summary.ByteSimilarityRatio.HasValue
            ? $", byte similarity {summary.ByteSimilarityRatio.Value * 100:F1}%"
            : string.Empty;

        var officeHint = WordSemanticProjection.IsWordOoxmlExtension(extension)
            ? "   Word semantic diff is unavailable for this pair, showing binary-only summary."
            : IsOfficeDocumentExtension(extension)
                ? "   Legacy office format is compared as binary."
                : string.Empty;

        return $"{relativePath}   {kind}   {sizeLabel}   {dedupLabel}, {changedLabel}{similarityLabel}{officeHint}";
    }

    private static string FormatSignedBytes(long value)
    {
        if (value == 0)
            return "0 B";

        var sign = value > 0 ? "+" : "-";
        var absolute = value == long.MinValue ? long.MaxValue : Math.Abs(value);
        return $"{sign}{FormatBytes(absolute)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private enum BlockHashFamily
    {
        Unknown = 0,
        Sha256 = 1,
        Native = 2
    }

    private sealed record CurrentFileDigest(
        string Sha256,
        long SizeBytes,
        IReadOnlyList<string> ChunkHashes,
        BlockHashFamily HashFamily);
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static int NormalizeMaxLines(int maxLines)
        => Math.Clamp(maxLines, 200, 20_000);

    private static string ComputeDiffKeySha256(long leftFileVersionId, long rightFileVersionId, int maxLines)
    {
        var payload = $"{leftFileVersionId}:{rightFileVersionId}:{maxLines}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string TruncateForColumn(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength];
    }

    private static readonly IReadOnlyList<SnapshotLinkStateDto> EmptySnapshotLinkStates = Array.Empty<SnapshotLinkStateDto>();
    private static string? NormalizeSnapshotTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var title = value.Trim();
        if (title.Length <= 256)
            return title;
        return title[..256];
    }
    private static SnapshotLinkStateDto ToSnapshotLinkStateDto(SnapshotLinkState state)
        => new(
            state.FileIdentityId,
            state.FileVersionId,
            state.IsDeletionMarker,
            state.SizeBytes,
            state.VersionCreatedAtUtc,
            state.RelativePath,
            state.Name);
    private static RepositoryPathStateDto ToRepositoryPathStateDto(SnapshotEntryLight state)
        => new(
            state.RelativePath,
            state.Name,
            state.SizeBytes,
            state.LastWriteUtc,
            state.ContentHashSha256);

    private static string NormalizeRelativePath(string value)
        => value.Trim().Replace('\\', '/');

    private static string ToAbsolutePath(string rootPath, string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(rootPath, rel);
    }

    private async Task<StoredFileContentDto?> TryStoreBlocksAsync(
        string absolutePath,
        bool throwOnFailure,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(absolutePath))
            {
                if (throwOnFailure)
                    throw new FileNotFoundException("File not found for block storage.", absolutePath);

                return null;
            }

            return await contentStore.StoreFileAsync(absolutePath, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to store file blocks for {Path}", absolutePath);

            if (throwOnFailure)
                throw new InvalidOperationException($"Failed to store file blocks {absolutePath}", ex);

            return null;
        }
    }

    private sealed record PendingTextDiffPrecompute(
        string RelativePath,
        long LeftFileVersionId,
        FileVersion RightFileVersion);
    private sealed record SnapshotLight(
        long SnapshotId,
        string? Title,
        DateTime CreatedAtUtc,
        string Trigger);

    private sealed record SnapshotLinkState(
        long SnapshotId,
        long FileIdentityId,
        long FileVersionId,
        bool IsDeletionMarker,
        long SizeBytes,
        DateTime VersionCreatedAtUtc,
        string RelativePath,
        string Name);
    private sealed record SnapshotEntryLight(
        string RelativePath,
        string Name,
        long SizeBytes,
        DateTime LastWriteUtc,
        string? ContentHashSha256);
}
