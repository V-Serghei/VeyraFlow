using Microsoft.EntityFrameworkCore;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositoryRepository(VeyraDbContext db) : IRepositoryRepository
{
    public async Task<int> CreateRepositoryAsync(string name, string? description, int directoryId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var now = DateTime.UtcNow;

        var existing = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.DirectoryId == directoryId, ct);

        if (existing is not null)
        {
            existing.IsDeleted = false;
            existing.DeletedAt = null;
            existing.Name = name;
            existing.Description = description;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return existing.Id;
        }

        var entity = new Repository
        {
            Name = name,
            Description = description,
            DirectoryId = directoryId,
            FileCount = 0,
            VersionCount = 0,
            TotalSizeBytes = 0,
            LastScannedAt = null,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
            DeletedAt = null
        };

        db.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task UpdateRepositoryAsync(int id, string name, string? description, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var entity = await db.Set<Repository>()
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, ct);

        if (entity is null)
            return;

        entity.Name = name;
        entity.Description = description;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRepositoryAsync(int id, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var now = DateTime.UtcNow;
        var repo = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Where(r => r.Id == id && !r.IsDeleted)
            .Select(r => new { r.Id, r.DirectoryId })
            .FirstOrDefaultAsync(ct);

        if (repo is null)
            return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Where(r => r.Id == repo.Id && !r.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsDeleted, true)
                .SetProperty(r => r.DeletedAt, now)
                .SetProperty(r => r.UpdatedAt, now), ct);

        await db.Set<WatchedDirectory>()
            .IgnoreQueryFilters()
            .Where(d => d.Id == repo.DirectoryId && !d.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.IsDeleted, true)
                .SetProperty(d => d.DeletedAt, now)
                .SetProperty(d => d.IsEnabled, false)
                .SetProperty(d => d.UpdatedAt, now), ct);

        await db.Set<WatchedDirectoryFormat>()
            .IgnoreQueryFilters()
            .Where(l => l.DirectoryId == repo.DirectoryId && !l.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.IsDeleted, true)
                .SetProperty(l => l.DeletedAt, now)
                .SetProperty(l => l.UpdatedAt, now), ct);

        await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .Where(i => i.RepositoryId == repo.Id && !i.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.IsDeleted, true)
                .SetProperty(i => i.DeletedAt, now)
                .SetProperty(i => i.UpdatedAt, now), ct);

        await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .Where(v => !v.IsDeleted && v.FileIdentity.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.IsDeleted, true)
                .SetProperty(v => v.DeletedAt, now), ct);

        await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => !b.IsDeleted && b.FileVersion.FileIdentity.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.IsDeleted, true)
                .SetProperty(b => b.DeletedAt, now), ct);

        await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .Where(s => s.RepositoryId == repo.Id && !s.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await db.Set<RepositorySnapshotEntry>()
            .IgnoreQueryFilters()
            .Where(e => e.RepositoryId == repo.Id && !e.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => !l.IsDeleted && l.Snapshot.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await db.Set<FileVersionTextDiff>()
            .IgnoreQueryFilters()
            .Where(d => !d.IsDeleted
                        && (db.Set<FileVersion>().IgnoreQueryFilters().Any(v => v.Id == d.LeftFileVersionId && v.FileIdentity.RepositoryId == repo.Id)
                            || db.Set<FileVersion>().IgnoreQueryFilters().Any(v => v.Id == d.RightFileVersionId && v.FileIdentity.RepositoryId == repo.Id)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct);

        await db.Set<FileVersionTextDiffHunk>()
            .IgnoreQueryFilters()
            .Where(h => !h.IsDeleted && h.Diff.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await db.Set<FileVersionTextDiffLine>()
            .IgnoreQueryFilters()
            .Where(l => !l.IsDeleted && l.Diff.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now), ct);

        await tx.CommitAsync(ct);
    }

    public async Task RestoreRepositoryAsync(int id, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var now = DateTime.UtcNow;
        var repo = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Where(r => r.Id == id && r.IsDeleted)
            .Select(r => new { r.Id, r.DirectoryId })
            .FirstOrDefaultAsync(ct);

        if (repo is null)
            return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Set<Repository>()
            .IgnoreQueryFilters()
            .Where(r => r.Id == repo.Id && r.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsDeleted, false)
                .SetProperty(r => r.DeletedAt, (DateTime?)null)
                .SetProperty(r => r.UpdatedAt, now), ct);

        await db.Set<WatchedDirectory>()
            .IgnoreQueryFilters()
            .Where(d => d.Id == repo.DirectoryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.IsDeleted, false)
                .SetProperty(d => d.DeletedAt, (DateTime?)null)
                .SetProperty(d => d.IsEnabled, true)
                .SetProperty(d => d.UpdatedAt, now), ct);

        await db.Set<WatchedDirectoryFormat>()
            .IgnoreQueryFilters()
            .Where(l => l.DirectoryId == repo.DirectoryId && l.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.IsDeleted, false)
                .SetProperty(l => l.DeletedAt, (DateTime?)null)
                .SetProperty(l => l.UpdatedAt, now), ct);

        await db.Set<FileIdentity>()
            .IgnoreQueryFilters()
            .Where(i => i.RepositoryId == repo.Id && i.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.IsDeleted, false)
                .SetProperty(i => i.DeletedAt, (DateTime?)null)
                .SetProperty(i => i.UpdatedAt, now), ct);

        await db.Set<FileVersion>()
            .IgnoreQueryFilters()
            .Where(v => v.IsDeleted && v.FileIdentity.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.IsDeleted, false)
                .SetProperty(v => v.DeletedAt, (DateTime?)null), ct);

        await db.Set<FileVersionBlock>()
            .IgnoreQueryFilters()
            .Where(b => b.IsDeleted && b.FileVersion.FileIdentity.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.IsDeleted, false)
                .SetProperty(b => b.DeletedAt, (DateTime?)null), ct);

        await db.Set<RepositorySnapshot>()
            .IgnoreQueryFilters()
            .Where(s => s.RepositoryId == repo.Id && s.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await db.Set<RepositorySnapshotEntry>()
            .IgnoreQueryFilters()
            .Where(e => e.RepositoryId == repo.Id && e.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await db.Set<SnapshotFileLink>()
            .IgnoreQueryFilters()
            .Where(l => l.IsDeleted && l.Snapshot.RepositoryId == repo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await db.Set<FileVersionTextDiff>()
            .IgnoreQueryFilters()
            .Where(d => d.IsDeleted
                        && (db.Set<FileVersion>().IgnoreQueryFilters().Any(v => v.Id == d.LeftFileVersionId && v.FileIdentity.RepositoryId == repo.Id)
                            || db.Set<FileVersion>().IgnoreQueryFilters().Any(v => v.Id == d.RightFileVersionId && v.FileIdentity.RepositoryId == repo.Id)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null)
                .SetProperty(x => x.UpdatedAt, now), ct);

        await db.Set<FileVersionTextDiffHunk>()
            .IgnoreQueryFilters()
            .Where(h => h.IsDeleted && !h.Diff.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await db.Set<FileVersionTextDiffLine>()
            .IgnoreQueryFilters()
            .Where(l => l.IsDeleted && !l.Diff.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, false)
                .SetProperty(x => x.DeletedAt, (DateTime?)null), ct);

        await tx.CommitAsync(ct);
    }

    public async Task<RepositoryDto?> GetRepositoryByIdAsync(int id, CancellationToken ct = default)
    {
        var repo = await db.Set<Repository>()
            .Include(r => r.Directory)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (repo is null) return null;

        var formats = await db.Set<WatchedDirectoryFormat>()
            .Where(l => !l.IsDeleted && l.DirectoryId == repo.DirectoryId && !l.Format.IsDeleted)
            .Select(l => l.Format.Pattern)
            .OrderBy(p => p)
            .ToListAsync(ct);

        return new RepositoryDto(
            repo.Id,
            repo.Name,
            repo.Description,
            repo.DirectoryId,
            repo.Directory.Path,
            formats,
            repo.IsDeleted,
            repo.FileCount,
            repo.VersionCount,
            repo.TotalSizeBytes,
            repo.LastScannedAt);
    }

    public async Task<IReadOnlyList<RepositoryDto>> GetAllRepositoriesAsync(CancellationToken ct = default)
    {
        var repos = await db.Set<Repository>()
            .Include(r => r.Directory)
            .Where(r => !r.IsDeleted && !r.Directory.IsDeleted)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        var dirIds = repos.Select(r => r.DirectoryId).ToList();

        var links = await db.Set<WatchedDirectoryFormat>()
            .Where(l => !l.IsDeleted && dirIds.Contains(l.DirectoryId) && !l.Format.IsDeleted)
            .Select(l => new { l.DirectoryId, l.Format.Pattern })
            .ToListAsync(ct);

        var formatsByDir = links
            .GroupBy(l => l.DirectoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Pattern).OrderBy(p => p).ToList());

        return repos.Select(r => new RepositoryDto(
            r.Id,
            r.Name,
            r.Description,
            r.DirectoryId,
            r.Directory.Path,
            formatsByDir.GetValueOrDefault(r.DirectoryId, Array.Empty<string>()),
            r.IsDeleted,
            r.FileCount,
            r.VersionCount,
            r.TotalSizeBytes,
            r.LastScannedAt)).ToList();
    }

    public async Task EnsureRepositoriesForAllDirectoriesAsync(CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();

        var now = DateTime.UtcNow;

        var activeDirs = await db.Set<WatchedDirectory>()
            .Where(d => !d.IsDeleted && d.IsEnabled)
            .ToListAsync(ct);

        var existingRepos = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        var repoByDirId = existingRepos.ToDictionary(r => r.DirectoryId);

        foreach (var dir in activeDirs)
        {
            if (repoByDirId.TryGetValue(dir.Id, out var existing))
            {
                if (existing.IsDeleted)
                {
                    existing.IsDeleted = false;
                    existing.DeletedAt = null;
                    existing.UpdatedAt = now;
                }
            }
            else
            {
                var folderName = Path.GetFileName(dir.Path.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(folderName))
                    folderName = dir.Path;

                db.Add(new Repository
                {
                    Name = folderName,
                    DirectoryId = dir.Id,
                    FileCount = 0,
                    VersionCount = 0,
                    TotalSizeBytes = 0,
                    LastScannedAt = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false,
                    DeletedAt = null
                });
            }
        }

        foreach (var repo in existingRepos)
        {
            if (!repo.IsDeleted && activeDirs.All(d => d.Id != repo.DirectoryId))
            {
                repo.IsDeleted = true;
                repo.DeletedAt = now;
                repo.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
