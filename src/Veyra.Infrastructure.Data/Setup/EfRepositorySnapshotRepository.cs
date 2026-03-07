using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
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

    private static readonly JsonSerializerOptions DiffJsonOptions = new();

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yml", ".yaml", ".ini", ".toml", ".log",
        ".cs", ".js", ".ts", ".java", ".py", ".rs", ".go", ".c", ".cpp", ".h", ".hpp",
        ".html", ".css", ".sql", ".xaml", ".axaml"
    };

    public async Task SaveSnapshotAsync(
        int repositoryId,
        string trigger,
        DateTime scannedAtUtc,
        IReadOnlyCollection<RepositoryScanEntryDto> entries,
        bool saveFileVersions = true,
        string? snapshotTitle = null,
        CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var repo = await db.Set<Repository>()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (repo is null)
            return;

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
            .Where(s => s.RepositoryId == repositoryId);

        if (saveFileVersions)
        {
            previousSnapshotsQuery = previousSnapshotsQuery
                .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id));
        }

        var previousSnapshotId = await previousSnapshotsQuery
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        var previousFilesByPath = previousSnapshotId == 0
            ? new Dictionary<string, (string? Hash, long SizeBytes, string Name, DateTime LastWriteUtc)>(StringComparer.OrdinalIgnoreCase)
            : await db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == previousSnapshotId && !e.IsDirectory)
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
                repo.FileCount = fileEntries;
                repo.TotalSizeBytes = totalFileBytes;
                repo.LastScannedAt = scannedAtUtc;
                repo.UpdatedAt = scannedAtUtc;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                log.LogInformation(
                    "Snapshot skipped (no changes). RepositoryId {RepositoryId}. Files {Files}. Trigger {Trigger}",
                    repositoryId,
                    fileEntries,
                    safeTrigger);
                return;
            }
        }
        var snapshot = new RepositorySnapshot
        {
            RepositoryId = repositoryId,
            Trigger = safeTrigger,
            Title = NormalizeSnapshotTitle(snapshotTitle),
            CreatedAt = scannedAtUtc,
            TotalEntries = totalEntries,
            FileEntries = fileEntries,
            DirectoryEntries = dirEntries,
            TotalFileBytes = totalFileBytes
        };

        db.Add(snapshot);
        await db.SaveChangesAsync(ct);

        if (totalEntries > 0)
        {
            var rows = entries.Select(e => new RepositorySnapshotEntry
            {
                SnapshotId = snapshot.Id,
                RepositoryId = repositoryId,
                RelativePath = e.RelativePath,
                ParentRelativePath = e.ParentRelativePath,
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Extension = e.Extension,
                SizeBytes = e.SizeBytes,
                LastWriteUtc = e.LastWriteUtc,
                ContentHashSha256 = e.ContentHashSha256,
                CreatedAt = scannedAtUtc
            }).ToList();

            db.AddRange(rows);
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
                "Repository sync saved without file versions. RepositoryId {RepositoryId}. Entries {Entries}. Files {Files}. Trigger {Trigger}",
                repositoryId,
                totalEntries,
                fileEntries,
                safeTrigger);

            return;
        }
        var allPaths = currentFilesByPath.Keys
            .Concat(previousFilesByPath.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var identitiesByPath = allPaths.Count == 0
            ? new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase)
            : await db.Set<FileIdentity>()
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
                IsDeleted = false,
                CreatedAt = scannedAtUtc,
                UpdatedAt = scannedAtUtc
            };

            db.Add(identity);
            identitiesByPath[path] = identity;
        }

        await db.SaveChangesAsync(ct);

        var identityIds = identitiesByPath.Values.Select(i => i.Id).Distinct().ToList();

        var latestVersionsByIdentityId = new Dictionary<long, FileVersion>();
        if (identityIds.Count > 0)
        {
            var existingVersions = await db.Set<FileVersion>()
                .Where(v => identityIds.Contains(v.FileIdentityId))
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
                    .Where(b => latestVersionIds.Contains(b.FileVersionId))
                    .Select(b => b.FileVersionId)
                    .Distinct()
                    .ToListAsync(ct))
                .ToHashSet();

        var newVersions = new List<FileVersion>();
        var links = new List<SnapshotFileLink>();
        var pendingBlocks = new Dictionary<FileVersion, IReadOnlyList<StoredFileBlockDto>>();
        var pendingPrecomputedDiffs = new List<PendingTextDiffPrecompute>();

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var identity = identitiesByPath[path];
            currentFilesByPath.TryGetValue(path, out var current);
            var hadPrevious = previousFilesByPath.TryGetValue(path, out var previous);

            FileVersion selectedVersion;

            if (current is not null)
            {
                identity.Name = current.Name;
                identity.Extension = current.Extension;
                identity.IsDeleted = false;
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

                var shouldCreateNewVersion = changed
                                             || !hasLatest
                                             || selectedVersion.IsDeletionMarker
                                             || !latestHasBlocks;

                if (shouldCreateNewVersion)
                {
                    var newVersion = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = current.ContentHashSha256 ?? "unknown",
                        SizeBytes = current.SizeBytes,
                        LastWriteUtc = current.LastWriteUtc,
                        IsDeletionMarker = false,
                        CreatedAt = scannedAtUtc
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
                        && IsTextExtension(current.Extension))
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
                identity.IsDeleted = true;
                identity.UpdatedAt = scannedAtUtc;

                if (!latestVersionsByIdentityId.TryGetValue(identity.Id, out selectedVersion!)
                    || !selectedVersion.IsDeletionMarker)
                {
                    selectedVersion = new FileVersion
                    {
                        FileIdentityId = identity.Id,
                        ContentHashSha256 = previous.Hash ?? "deleted",
                        SizeBytes = previous.SizeBytes,
                        LastWriteUtc = scannedAtUtc,
                        IsDeletionMarker = true,
                        CreatedAt = scannedAtUtc
                    };

                    newVersions.Add(selectedVersion);
                    latestVersionsByIdentityId[identity.Id] = selectedVersion;
                }
            }

            var link = new SnapshotFileLink
            {
                SnapshotId = snapshot.Id,
                FileIdentityId = identity.Id,
                CreatedAt = scannedAtUtc
            };

            if (selectedVersion.Id > 0)
                link.FileVersionId = selectedVersion.Id;
            else
                link.FileVersion = selectedVersion;

            links.Add(link);
        }

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
                    CreatedAt = scannedAtUtc
                });
            }
        }

        if (newVersions.Count > 0)
            db.AddRange(newVersions);

        if (links.Count > 0)
            db.AddRange(links);

        repo.FileCount = fileEntries;
        repo.VersionCount += 1;
        repo.TotalSizeBytes = totalFileBytes;
        repo.LastScannedAt = scannedAtUtc;
        repo.UpdatedAt = scannedAtUtc;

        await db.SaveChangesAsync(ct);

        if (pendingPrecomputedDiffs.Count > 0)
            await PrecomputeSnapshotDiffsAsync(pendingPrecomputedDiffs, ct);

        await tx.CommitAsync(ct);

        var totalBlockRefs = pendingBlocks.Values.Sum(v => v.Count);

        log.LogInformation(
            "Snapshot saved for repository {RepositoryId}. Entries {Entries}. Files {Files}. FileIdentities {FileIdentities}. NewVersions {NewVersions}. Links {Links}. BlockRefs {BlockRefs}. Trigger {Trigger}",
            repositoryId,
            totalEntries,
            fileEntries,
            identitiesByPath.Count,
            newVersions.Count,
            links.Count,
            totalBlockRefs,
            safeTrigger);
    }

    public async Task<IReadOnlyList<RepositoryScanEntryDto>> GetLatestEntriesAsync(
        int repositoryId,
        CancellationToken ct = default)
    {
        var snapshotId = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (snapshotId == 0)
            return Array.Empty<RepositoryScanEntryDto>();

        return await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == snapshotId)
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
            .FirstOrDefaultAsync(i => i.RepositoryId == repositoryId && i.RelativePath == normalizedPath, ct);

        if (identity is null)
            return Array.Empty<FileVersionInfoDto>();

        var limit = Math.Clamp(take, 1, 500);

        return await db.Set<FileVersion>()
            .Where(v => v.FileIdentityId == identity.Id)
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
            .Where(s => s.RepositoryId == repositoryId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (latestSnapshot is null)
            return RepositoryPendingChangesDto.Empty;

        var currentFiles = await db.Set<RepositorySnapshotEntry>()
            .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == latestSnapshot.Id && !e.IsDirectory)
            .Select(e => new SnapshotEntryLight(
                e.RelativePath,
                e.Name,
                e.SizeBytes,
                e.LastWriteUtc,
                e.ContentHashSha256))
            .ToListAsync(ct);

        var baselineSnapshot = await db.Set<RepositorySnapshot>()
            .Where(s => s.RepositoryId == repositoryId)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        var baselineFiles = baselineSnapshot is null
            ? []
            : await db.Set<RepositorySnapshotEntry>()
                .Where(e => e.RepositoryId == repositoryId && e.SnapshotId == baselineSnapshot.Id && !e.IsDirectory)
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
            .Where(s => s.RepositoryId == repositoryId)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id))
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
            .Where(l => snapshotIds.Contains(l.SnapshotId))
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
            .Where(s => s.RepositoryId == repositoryId)
            .Where(s => db.Set<SnapshotFileLink>().Any(l => l.SnapshotId == s.Id))
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
            .Where(l => l.SnapshotId == snapshotId)
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
                .Where(l => l.SnapshotId == previousSnapshotId)
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
            .FirstOrDefaultAsync(d => d.LeftFileVersionId == leftFileVersionId
                                      && d.RightFileVersionId == rightFileVersionId
                                      && d.MaxLines == normalizedMaxLines,
                ct);

        if (row is null)
            return null;

        var lineRows = await db.Set<FileVersionTextDiffLine>()
            .AsNoTracking()
            .Where(l => l.DiffId == row.Id)
            .OrderBy(l => l.Sequence)
            .Select(l => new TextDiffLineDto(
                l.Kind,
                l.LeftLineNumber,
                l.RightLineNumber,
                l.TextLineAtom.Text))
            .ToListAsync(ct);

        if (lineRows.Count > 0)
        {
            return new TextDiffResultDto(
                row.RelativePath,
                row.LeftFileVersionId,
                row.RightFileVersionId,
                row.AddedLines,
                row.RemovedLines,
                row.IsTruncated,
                lineRows);
        }

        try
        {
            var lines = JsonSerializer.Deserialize<List<TextDiffLineDto>>(row.LinesJson, DiffJsonOptions)
                        ?? [];

            return new TextDiffResultDto(
                row.RelativePath,
                row.LeftFileVersionId,
                row.RightFileVersionId,
                row.AddedLines,
                row.RemovedLines,
                row.IsTruncated,
                lines);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Failed to deserialize cached text diff. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                leftFileVersionId,
                rightFileVersionId);

            return null;
        }
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

        var linesJson = JsonSerializer.Serialize(normalizedLines, DiffJsonOptions);
        var diffKey = ComputeDiffKeySha256(diff.LeftFileVersionId, diff.RightFileVersionId, normalizedMaxLines);

        var existing = await db.Set<FileVersionTextDiff>()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.LeftFileVersionId == diff.LeftFileVersionId
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
                CreatedAt = nowUtc
            };

            db.Add(existing);
        }

        existing.DiffKeySha256 = diffKey;
        existing.RelativePath = TruncateForColumn(diff.RelativePath, 2048);
        existing.AddedLines = diff.AddedLines;
        existing.RemovedLines = diff.RemovedLines;
        existing.IsTruncated = diff.IsTruncated;
        existing.LinesJson = linesJson;
        existing.UpdatedAt = nowUtc;

        if (existing.Lines.Count > 0)
            db.RemoveRange(existing.Lines);

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

            var diffLines = new List<FileVersionTextDiffLine>(normalizedLines.Count);

            for (var i = 0; i < normalizedLines.Count; i++)
            {
                var line = normalizedLines[i];

                diffLines.Add(new FileVersionTextDiffLine
                {
                    Diff = existing,
                    Sequence = i,
                    Kind = line.Kind,
                    LeftLineNumber = line.LeftLineNumber,
                    RightLineNumber = line.RightLineNumber,
                    TextLineAtom = atomsByText[line.Text],
                    CreatedAt = nowUtc
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
            .Include(v => v.FileIdentity)
            .FirstOrDefaultAsync(v => v.Id == fileVersionId, ct);

        if (version is null)
            return null;

        var blocks = await db.Set<FileVersionBlock>()
            .Where(b => b.FileVersionId == fileVersionId)
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

                if (!IsTextExtension(right.Extension))
                    continue;

                var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "diff-precompute");
                Directory.CreateDirectory(tempDir);

                var leftTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.left.tmp");
                var rightTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.right.tmp");

                try
                {
                    await contentStore.RestoreFileAsync(left.Blocks, leftTemp, true, ct);
                    await contentStore.RestoreFileAsync(right.Blocks, rightTemp, true, ct);

                    var computed = await diffEngine.BuildDiffAsync(leftTemp, rightTemp, PrecomputedDiffMaxLines, ct);

                    var diff = new TextDiffResultDto(
                        item.RelativePath,
                        left.FileVersionId,
                        right.FileVersionId,
                        computed.AddedLines,
                        computed.RemovedLines,
                        computed.IsTruncated,
                        computed.Lines);

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

    private static bool IsTextExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;

        return TextExtensions.Contains(normalized);
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
            state.ContentHashSha256);private static string NormalizeRelativePath(string value)
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
