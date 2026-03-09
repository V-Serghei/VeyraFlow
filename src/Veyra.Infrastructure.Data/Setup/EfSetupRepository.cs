using Microsoft.EntityFrameworkCore;
using Veyra.Application.Abstractions.Setup;
using Veyra.Domain.Entities.Watched;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfSetupRepository(VeyraDbContext dbContext) : ISetupRepository
{
    public async Task SaveInitialSetupAsync(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> extensions, CancellationToken ct = default)
    {
        await using var tx = await dbContext.Database.BeginTransactionAsync(ct);

        await ReplaceWatchedDirectoriesAsync(paths, ct);
        await ReplaceTrackedExtensionsAsync(extensions, ct);

        await LinkAllActiveDirectoriesToAllActiveFormatsAsync(ct);

        await tx.CommitAsync(ct);
    }

    private async Task LinkAllActiveDirectoriesToAllActiveFormatsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var dirs = await dbContext.Set<WatchedDirectory>()
            .Where(x => !x.IsDeleted && x.IsEnabled)
            .Select(x => new { x.Id })
            .ToListAsync(ct);

        var fmts = await dbContext.Set<D_WatchedFormat>()
            .Where(x => !x.IsDeleted && x.IsEnabled)
            .Select(x => new { x.Id })
            .ToListAsync(ct);

        var dirIds = dirs.Select(x => x.Id).ToList();
        var fmtIds = fmts.Select(x => x.Id).ToList();

        if (dirIds.Count == 0 || fmtIds.Count == 0)
            return;

        var existing = await dbContext.Set<WatchedDirectoryFormat>()
            .IgnoreQueryFilters()
            .Where(x => dirIds.Contains(x.DirectoryId) && fmtIds.Contains(x.FormatId))
            .ToListAsync(ct);

        var map = existing.ToDictionary(x => (x.DirectoryId, x.FormatId));

        foreach (var d in dirIds)
        {
            foreach (var f in fmtIds)
            {
                var key = (d, f);
                if (map.TryGetValue(key, out var link))
                {
                    link.IsDeleted = false;
                    link.DeletedAt = null;
                    link.UpdatedAt = now;
                }
                else
                {
                    dbContext.Add(new WatchedDirectoryFormat
                    {
                        DirectoryId = d,
                        FormatId = f,
                        CreatedAt = now,
                        UpdatedAt = now,
                        IsDeleted = false,
                        DeletedAt = null
                    });
                }
            }
        }

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task ReplaceWatchedDirectoriesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        var normalized = NormalizePathsForStore(paths);
        var now = DateTime.UtcNow;

        var existing = await dbContext.Set<WatchedDirectory>()
            .IgnoreQueryFilters()
            .ToListAsync(ct);
        var map = existing.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var p in normalized)
        {
            if (map.TryGetValue(p, out var e))
            {
                e.Path = p;
                e.IsDeleted = false;
                e.DeletedAt = null;
                e.IsEnabled = true;
                e.UpdatedAt = now;
                e.ErrorMessage = null;
            }
            else
            {
                dbContext.Add(new WatchedDirectory
                {
                    Path = p,
                    IsEnabled = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false,
                    DeletedAt = null,
                    ErrorMessage = null
                });
            }
        }

        var keep = normalized.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing)
        {
            if (!keep.Contains(e.Path) && !e.IsDeleted)
            {
                e.IsDeleted = true;
                e.DeletedAt = now;
                e.IsEnabled = false;
                e.UpdatedAt = now;
            }
        }

        await dbContext.SaveChangesAsync(ct);

        var deletedDirIds = existing.Where(x => x.IsDeleted).Select(x => x.Id).ToList();
        if (deletedDirIds.Count > 0)
        {
            await dbContext.Set<WatchedDirectoryFormat>()
                .Where(x => !x.IsDeleted && deletedDirIds.Contains(x.DirectoryId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, now)
                    .SetProperty(x => x.UpdatedAt, now), ct);
        }
    }

    public async Task DeleteWatchedDirectoriesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        var normalized = NormalizePathsForLookup(paths);
        if (normalized.Count == 0) return;

        var now = DateTime.UtcNow;

        var ids = await dbContext.Set<WatchedDirectory>()
            .Where(x => !x.IsDeleted && normalized.Contains(x.Path))
            .Select(x => x.Id)
            .ToListAsync(ct);

        if (ids.Count == 0) return;

        await dbContext.Set<WatchedDirectory>()
            .Where(x => ids.Contains(x.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.IsEnabled, false)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.ErrorMessage, (string?)null), ct);

        await dbContext.Set<WatchedDirectoryFormat>()
            .Where(x => !x.IsDeleted && ids.Contains(x.DirectoryId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct);
    }

    public async Task ReplaceTrackedExtensionsAsync(IReadOnlyCollection<string> extensions, CancellationToken ct = default)
    {
        var normalized = NormalizeExtensionsForStore(extensions);
        var now = DateTime.UtcNow;

        var existing = await dbContext.Set<D_WatchedFormat>()
            .IgnoreQueryFilters()
            .ToListAsync(ct);
        var map = existing.ToDictionary(x => x.Pattern, StringComparer.OrdinalIgnoreCase);

        foreach (var p in normalized)
        {
            if (map.TryGetValue(p, out var e))
            {
                e.Pattern = p;
                e.IsDeleted = false;
                e.DeletedAt = null;
                e.IsEnabled = true;
                e.UpdatedAt = now;
            }
            else
            {
                dbContext.Add(new D_WatchedFormat
                {
                    Pattern = p,
                    IsEnabled = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false,
                    DeletedAt = null
                });
            }
        }

        var keep = normalized.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing)
        {
            if (!keep.Contains(e.Pattern) && !e.IsDeleted)
            {
                e.IsDeleted = true;
                e.DeletedAt = now;
                e.IsEnabled = false;
                e.UpdatedAt = now;
            }
        }

        await dbContext.SaveChangesAsync(ct);

        var deletedFmtIds = existing.Where(x => x.IsDeleted).Select(x => x.Id).ToList();
        if (deletedFmtIds.Count > 0)
        {
            await dbContext.Set<WatchedDirectoryFormat>()
                .Where(x => !x.IsDeleted && deletedFmtIds.Contains(x.FormatId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, now)
                    .SetProperty(x => x.UpdatedAt, now), ct);
        }
    }

    public async Task DeleteTrackedExtensionsAsync(IReadOnlyCollection<string> extensions, CancellationToken ct = default)
    {
        var normalized = NormalizeExtensionsForLookup(extensions);
        if (normalized.Count == 0) return;

        var now = DateTime.UtcNow;

        var ids = await dbContext.Set<D_WatchedFormat>()
            .Where(x => !x.IsDeleted && normalized.Contains(x.Pattern))
            .Select(x => x.Id)
            .ToListAsync(ct);

        if (ids.Count == 0) return;

        await dbContext.Set<D_WatchedFormat>()
            .Where(x => ids.Contains(x.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.IsEnabled, false)
                .SetProperty(x => x.UpdatedAt, now), ct);

        await dbContext.Set<WatchedDirectoryFormat>()
            .Where(x => !x.IsDeleted && ids.Contains(x.FormatId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct);
    }

    public async Task AddWatchedDirectoryAsync(string path, CancellationToken ct = default)
    {
        var normalized = NormalizePathsForStore(new[] { path });
        if (normalized.Count == 0) return;

        var p = normalized[0];
        var now = DateTime.UtcNow;

        var existing = await dbContext.Set<WatchedDirectory>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Path == p, ct);

        if (existing is not null)
        {
            existing.IsDeleted = false;
            existing.DeletedAt = null;
            existing.IsEnabled = true;
            existing.UpdatedAt = now;
            existing.ErrorMessage = null;
        }
        else
        {
            dbContext.Add(new WatchedDirectory
            {
                Path = p,
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false,
                DeletedAt = null,
                ErrorMessage = null
            });
        }

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task UpdateWatchedDirectoryAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        var normalizedOld = NormalizePathsForLookup(new[] { oldPath });
        var normalizedNew = NormalizePathsForStore(new[] { newPath });
        if (normalizedOld.Count == 0 || normalizedNew.Count == 0) return;

        var now = DateTime.UtcNow;

        var entity = await dbContext.Set<WatchedDirectory>()
            .FirstOrDefaultAsync(x => !x.IsDeleted && x.Path == normalizedOld[0], ct);

        if (entity is null) return;

        entity.Path = normalizedNew[0];
        entity.UpdatedAt = now;

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetWatchedDirectoriesAsync(CancellationToken ct = default)
    {
        return await dbContext.Set<WatchedDirectory>()
            .Where(x => !x.IsDeleted && x.IsEnabled)
            .OrderBy(x => x.Path)
            .Select(x => x.Path)
            .ToListAsync(ct);
    }

    public async Task AddTrackedExtensionAsync(string extension, CancellationToken ct = default)
    {
        var normalized = NormalizeExtensionsForStore(new[] { extension });
        if (normalized.Count == 0) return;

        var p = normalized[0];
        var now = DateTime.UtcNow;

        var existing = await dbContext.Set<D_WatchedFormat>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Pattern == p, ct);

        if (existing is not null)
        {
            existing.IsDeleted = false;
            existing.DeletedAt = null;
            existing.IsEnabled = true;
            existing.UpdatedAt = now;
        }
        else
        {
            dbContext.Add(new D_WatchedFormat
            {
                Pattern = p,
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false,
                DeletedAt = null
            });
        }

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task UpdateTrackedExtensionAsync(string oldPattern, string newPattern, CancellationToken ct = default)
    {
        var normalizedOld = NormalizeExtensionsForLookup(new[] { oldPattern });
        var normalizedNew = NormalizeExtensionsForStore(new[] { newPattern });
        if (normalizedOld.Count == 0 || normalizedNew.Count == 0) return;

        var now = DateTime.UtcNow;

        var entity = await dbContext.Set<D_WatchedFormat>()
            .FirstOrDefaultAsync(x => !x.IsDeleted && x.Pattern == normalizedOld[0], ct);

        if (entity is null) return;

        entity.Pattern = normalizedNew[0];
        entity.UpdatedAt = now;

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetTrackedExtensionsAsync(CancellationToken ct = default)
    {
        return await dbContext.Set<D_WatchedFormat>()
            .Where(x => !x.IsDeleted && x.IsEnabled)
            .OrderBy(x => x.Pattern)
            .Select(x => x.Pattern)
            .ToListAsync(ct);
    }

    // ── Directory ↔ Format links ──────────────────────────────────

    public async Task LinkDirectoryToFormatsAsync(string directoryPath, IReadOnlyCollection<string> formatPatterns, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePathsForLookup(new[] { directoryPath });
        var normalizedExts = NormalizeExtensionsForLookup(formatPatterns);
        if (normalizedPath.Count == 0 || normalizedExts.Count == 0) return;

        var now = DateTime.UtcNow;
        var path = normalizedPath[0];

        var dir = await dbContext.Set<WatchedDirectory>()
            .FirstOrDefaultAsync(x => !x.IsDeleted && x.Path == path, ct);
        if (dir is null) return;

        var fmtIds = await dbContext.Set<D_WatchedFormat>()
            .Where(x => !x.IsDeleted && normalizedExts.Contains(x.Pattern))
            .Select(x => x.Id)
            .ToListAsync(ct);
        if (fmtIds.Count == 0) return;

        var existingLinks = await dbContext.Set<WatchedDirectoryFormat>()
            .IgnoreQueryFilters()
            .Where(x => x.DirectoryId == dir.Id && fmtIds.Contains(x.FormatId))
            .ToListAsync(ct);

        var map = existingLinks.ToDictionary(x => x.FormatId);

        foreach (var fId in fmtIds)
        {
            if (map.TryGetValue(fId, out var link))
            {
                link.IsDeleted = false;
                link.DeletedAt = null;
                link.UpdatedAt = now;
            }
            else
            {
                dbContext.Add(new WatchedDirectoryFormat
                {
                    DirectoryId = dir.Id,
                    FormatId = fId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false,
                    DeletedAt = null
                });
            }
        }

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task UnlinkDirectoryFromFormatsAsync(string directoryPath, IReadOnlyCollection<string> formatPatterns, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePathsForLookup(new[] { directoryPath });
        var normalizedExts = NormalizeExtensionsForLookup(formatPatterns);
        if (normalizedPath.Count == 0 || normalizedExts.Count == 0) return;

        var now = DateTime.UtcNow;
        var path = normalizedPath[0];

        var dirId = await dbContext.Set<WatchedDirectory>()
            .Where(x => !x.IsDeleted && x.Path == path)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (dirId == 0) return;

        var fmtIds = await dbContext.Set<D_WatchedFormat>()
            .Where(x => !x.IsDeleted && normalizedExts.Contains(x.Pattern))
            .Select(x => x.Id)
            .ToListAsync(ct);
        if (fmtIds.Count == 0) return;

        await dbContext.Set<WatchedDirectoryFormat>()
            .Where(x => !x.IsDeleted && x.DirectoryId == dirId && fmtIds.Contains(x.FormatId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsDeleted, true)
                .SetProperty(x => x.DeletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct);
    }

    public async Task<IReadOnlyList<string>> GetFormatsForDirectoryAsync(string directoryPath, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePathsForLookup(new[] { directoryPath });
        if (normalizedPath.Count == 0) return Array.Empty<string>();

        var path = normalizedPath[0];

        return await dbContext.Set<WatchedDirectoryFormat>()
            .Where(x => !x.IsDeleted && x.Directory.Path == path && !x.Directory.IsDeleted)
            .Select(x => x.Format.Pattern)
            .OrderBy(x => x)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetDirectoriesForFormatAsync(string formatPattern, CancellationToken ct = default)
    {
        var normalizedExt = NormalizeExtensionsForLookup(new[] { formatPattern });
        if (normalizedExt.Count == 0) return Array.Empty<string>();

        var pattern = normalizedExt[0];

        return await dbContext.Set<WatchedDirectoryFormat>()
            .Where(x => !x.IsDeleted && x.Format.Pattern == pattern && !x.Format.IsDeleted)
            .Select(x => x.Directory.Path)
            .OrderBy(x => x)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(string DirectoryPath, string FormatPattern)>> GetAllDirectoryFormatLinksAsync(CancellationToken ct = default)
    {
        var rows = await dbContext.Set<WatchedDirectoryFormat>()
            .Where(x => !x.IsDeleted && !x.Directory.IsDeleted && !x.Format.IsDeleted)
            .Select(x => new { x.Directory.Path, x.Format.Pattern })
            .OrderBy(x => x.Path).ThenBy(x => x.Pattern)
            .ToListAsync(ct);

        return rows.Select(x => (x.Path, x.Pattern)).ToList();
    }

    private static List<string> NormalizePathsForStore(IEnumerable<string> paths)
    {
        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Replace('/', '\\'))
            .Select(TryFullPath)
            .Where(p => p is not null && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static List<string> NormalizePathsForLookup(IEnumerable<string> paths)
    {
        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Replace('/', '\\'))
            .Select(TryFullPath)
            .Where(p => p is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static string? TryFullPath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return null; }
    }

    private static List<string> NormalizeExtensionsForStore(IEnumerable<string> extensions)
    {
        return extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Select(e => e.StartsWith(".") ? e : "." + e)
            .Select(e => e.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> NormalizeExtensionsForLookup(IEnumerable<string> extensions)
    {
        return extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Select(e => e.StartsWith(".") ? e : "." + e)
            .Select(e => e.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
