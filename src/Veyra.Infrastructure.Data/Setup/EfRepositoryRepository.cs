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
        var now = DateTime.UtcNow;

        var existing = await db.Set<Repository>()
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
        var entity = await db.Set<Repository>().FindAsync(new object[] { id }, ct);
        if (entity is null || entity.IsDeleted) return;

        entity.Name = name;
        entity.Description = description;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRepositoryAsync(int id, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await db.Set<Repository>()
            .Where(r => r.Id == id && !r.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IsDeleted, true)
                .SetProperty(r => r.DeletedAt, now)
                .SetProperty(r => r.UpdatedAt, now), ct);
    }

    public async Task RestoreRepositoryAsync(int id, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entity = await db.Set<Repository>().FindAsync(new object[] { id }, ct);
        if (entity is null || !entity.IsDeleted) return;

        entity.IsDeleted = false;
        entity.DeletedAt = null;
        entity.UpdatedAt = now;

        var dir = await db.Set<WatchedDirectory>().FindAsync(new object[] { entity.DirectoryId }, ct);
        if (dir is not null && dir.IsDeleted)
        {
            dir.IsDeleted = false;
            dir.DeletedAt = null;
            dir.IsEnabled = true;
            dir.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
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
        var now = DateTime.UtcNow;

        var activeDirs = await db.Set<WatchedDirectory>()
            .Where(d => !d.IsDeleted && d.IsEnabled)
            .ToListAsync(ct);

        var existingRepos = await db.Set<Repository>()
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
