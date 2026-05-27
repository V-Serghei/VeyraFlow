using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Veyra.Application.Abstractions.Storage;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Storage;

public sealed class LocalBlockStorageMetricsService(
    VeyraDbContext db,
    IConfiguration configuration)
    : ILocalBlockStorageMetricsService
{
    private const string ManagedHashPrefix = "sha256-";

    public async Task<LocalBlockStorageMetricsDto> GetMetricsAsync(CancellationToken ct = default)
    {
        var repositoryCount = await db.Repositories
            .AsNoTracking()
            .CountAsync(r => !r.IsDeleted, ct);

        var blockRows = await db.Set<FileVersionBlock>()
            .AsNoTracking()
            .Where(b => !b.IsDeleted)
            .Where(b => !b.FileVersion.IsDeleted)
            .Where(b => !b.FileVersion.FileIdentity.IsDeleted)
            .Select(b => new
            {
                b.BlockStorageKey,
                LengthBytes = (long)b.LengthBytes
            })
            .ToListAsync(ct);

        var referencedBlockCount = blockRows.Count;
        var logicalReferencedBytes = blockRows.Sum(static row => Math.Max(0L, row.LengthBytes));
        var uniqueHashes = blockRows
            .Select(static row => row.BlockStorageKey?.Trim())
            .Where(static hash => !string.IsNullOrWhiteSpace(hash))
            .Select(static hash => hash!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var root = ResolveBlockStoreRoot();
        var filesystem = ScanFilesystem(root, ct);
        var physicalStoredBytes = filesystem.ManagedBytes + filesystem.NativeBytes;

        var missingBlockCount = 0L;
        foreach (var hash in uniqueHashes)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(ResolveStoredBlockPath(root, hash)))
                missingBlockCount++;
        }

        var savedBytes = Math.Max(0L, logicalReferencedBytes - physicalStoredBytes);
        var reducedPercentFloor = logicalReferencedBytes <= 0
            ? 0L
            : Math.Max(0L, (long)Math.Floor(savedBytes * 100d / logicalReferencedBytes));

        return new LocalBlockStorageMetricsDto(
            RepositoryCount: repositoryCount,
            ReferencedBlockCount: referencedBlockCount,
            UniqueBlockCount: uniqueHashes.Count,
            MissingBlockCount: missingBlockCount,
            LogicalReferencedBytes: logicalReferencedBytes,
            PhysicalStoredBytes: physicalStoredBytes,
            SavedBytes: savedBytes,
            ReducedPercentFloor: reducedPercentFloor,
            Filesystem: filesystem);
    }

    private LocalBlockStorageFilesystemMetricsDto ScanFilesystem(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root))
        {
            return new LocalBlockStorageFilesystemMetricsDto(
                ManagedFileCount: 0,
                NativeFileCount: 0,
                OtherFileCount: 0,
                ManagedBytes: 0,
                NativeBytes: 0,
                OtherBytes: 0);
        }

        var managedRoot = Path.Combine(root, "managed");
        var nativeBlocksRoot = Path.Combine(root, "blocks");

        long managedFileCount = 0;
        long nativeFileCount = 0;
        long otherFileCount = 0;
        long managedBytes = 0;
        long nativeBytes = 0;
        long otherBytes = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            FileInfo? info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                    continue;
            }
            catch
            {
                continue;
            }

            if (path.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
            {
                managedFileCount++;
                managedBytes += Math.Max(0L, info.Length);
                continue;
            }

            if (path.StartsWith(nativeBlocksRoot, StringComparison.OrdinalIgnoreCase))
            {
                nativeFileCount++;
                nativeBytes += Math.Max(0L, info.Length);
                continue;
            }

            otherFileCount++;
            otherBytes += Math.Max(0L, info.Length);
        }

        return new LocalBlockStorageFilesystemMetricsDto(
            ManagedFileCount: managedFileCount,
            NativeFileCount: nativeFileCount,
            OtherFileCount: otherFileCount,
            ManagedBytes: managedBytes,
            NativeBytes: nativeBytes,
            OtherBytes: otherBytes);
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

        return Path.GetFullPath(root);
    }

    private static string ResolveStoredBlockPath(string root, string blockHash)
    {
        if (blockHash.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var hash = blockHash[ManagedHashPrefix.Length..].Trim().ToLowerInvariant();
            var p1 = hash.Length >= 2 ? hash[..2] : "00";
            var p2 = hash.Length >= 4 ? hash[2..4] : "00";
            return Path.Combine(root, "managed", "blocks", p1, p2, hash + ".bin");
        }

        var safe = blockHash.Trim().ToLowerInvariant()
            .Replace('/', '_')
            .Replace('\\', '_');
        var p1Native = safe.Length >= 2 ? safe[..2] : "00";
        var p2Native = safe.Length >= 4 ? safe[2..4] : "00";
        return Path.Combine(root, "blocks", p1Native, p2Native, safe + ".zst");
    }
}
