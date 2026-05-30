using System.IO.Compression;
using System.Reflection;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.PendingChanges;

namespace Veyra.Infrastructure.Data.Preview;

public static class ArchiveDiffPreviewBuilder
{
    private static readonly PropertyInfo? ZipEntryCrc32Property = typeof(ZipArchiveEntry).GetProperty("Crc32");

    public static Task<PendingArchiveDiffPreviewDto?> TryBuildZipAsync(
        string baselinePath,
        string currentPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baselinePath)
            || string.IsNullOrWhiteSpace(currentPath)
            || !File.Exists(baselinePath)
            || !File.Exists(currentPath))
        {
            return Task.FromResult<PendingArchiveDiffPreviewDto?>(null);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var baselineEntries = ReadZipManifest(baselinePath, ct);
            var currentEntries = ReadZipManifest(currentPath, ct);

            var keys = baselineEntries.Keys
                .Concat(currentEntries.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x, StringComparer.Ordinal)
                .ToArray();

            var entryDiffs = new List<PendingArchiveEntryDiffDto>(keys.Length);
            var addedCount = 0;
            var removedCount = 0;
            var changedCount = 0;
            var unchangedCount = 0;

            foreach (var key in keys)
            {
                ct.ThrowIfCancellationRequested();

                baselineEntries.TryGetValue(key, out var baseline);
                currentEntries.TryGetValue(key, out var current);

                var changeKind = ResolveChangeKind(baseline, current);
                switch (changeKind)
                {
                    case PendingArchiveEntryChangeKind.Added:
                        addedCount++;
                        break;
                    case PendingArchiveEntryChangeKind.Removed:
                        removedCount++;
                        break;
                    case PendingArchiveEntryChangeKind.Changed:
                        changedCount++;
                        break;
                    default:
                        unchangedCount++;
                        break;
                }

                entryDiffs.Add(new PendingArchiveEntryDiffDto
                {
                    EntryPath = key,
                    ChangeKind = changeKind,
                    IsDirectory = baseline?.IsDirectory ?? current?.IsDirectory ?? false,
                    BaselineSizeBytes = baseline?.SizeBytes,
                    CurrentSizeBytes = current?.SizeBytes,
                    BaselineCompressedSizeBytes = baseline?.CompressedSizeBytes,
                    CurrentCompressedSizeBytes = current?.CompressedSizeBytes,
                    BaselineCrc32 = baseline?.Crc32,
                    CurrentCrc32 = current?.Crc32,
                    BaselineModifiedUtc = baseline?.LastWriteUtc,
                    CurrentModifiedUtc = current?.LastWriteUtc
                });
            }

            return Task.FromResult<PendingArchiveDiffPreviewDto?>(new PendingArchiveDiffPreviewDto
            {
                ArchiveFormat = "zip",
                BaselineEntryCount = baselineEntries.Count,
                CurrentEntryCount = currentEntries.Count,
                AddedEntryCount = addedCount,
                RemovedEntryCount = removedCount,
                ChangedEntryCount = changedCount,
                UnchangedEntryCount = unchangedCount,
                Entries = entryDiffs
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<PendingArchiveDiffPreviewDto?>(null);
        }
    }

    private static Dictionary<string, ZipManifestEntry> ReadZipManifest(string archivePath, CancellationToken ct)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var manifest = new Dictionary<string, ZipManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var entryPath = NormalizeEntryPath(entry.FullName);
            if (string.IsNullOrWhiteSpace(entryPath))
                continue;

            var isDirectory = entryPath.EndsWith("/", StringComparison.Ordinal);
            manifest[entryPath] = new ZipManifestEntry(
                entryPath,
                isDirectory,
                isDirectory ? 0 : entry.Length,
                isDirectory ? 0 : entry.CompressedLength,
                TryGetZipCrc32(entry),
                NormalizeLastWriteUtc(entry.LastWriteTime));
        }

        return manifest;
    }

    private static PendingArchiveEntryChangeKind ResolveChangeKind(ZipManifestEntry? baseline, ZipManifestEntry? current)
    {
        if (baseline is null && current is null)
            return PendingArchiveEntryChangeKind.Unchanged;

        if (baseline is null)
            return PendingArchiveEntryChangeKind.Added;

        if (current is null)
            return PendingArchiveEntryChangeKind.Removed;

        if (baseline.IsDirectory != current.IsDirectory)
            return PendingArchiveEntryChangeKind.Changed;

        if (baseline.IsDirectory && current.IsDirectory)
            return PendingArchiveEntryChangeKind.Unchanged;

        if (baseline.Crc32.HasValue && current.Crc32.HasValue)
        {
            return baseline.Crc32.Value == current.Crc32.Value
                   && baseline.SizeBytes == current.SizeBytes
                ? PendingArchiveEntryChangeKind.Unchanged
                : PendingArchiveEntryChangeKind.Changed;
        }

        return baseline.SizeBytes == current.SizeBytes
               && baseline.CompressedSizeBytes == current.CompressedSizeBytes
               && Nullable.Equals(baseline.LastWriteUtc, current.LastWriteUtc)
            ? PendingArchiveEntryChangeKind.Unchanged
            : PendingArchiveEntryChangeKind.Changed;
    }

    private static string NormalizeEntryPath(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return string.Empty;

        var normalized = fullName.Replace('\\', '/').Trim();
        while (normalized.StartsWith("/", StringComparison.Ordinal))
            normalized = normalized[1..];

        return normalized;
    }

    private static uint? TryGetZipCrc32(ZipArchiveEntry entry)
    {
        var value = ZipEntryCrc32Property?.GetValue(entry);
        return value switch
        {
            uint uintValue => uintValue,
            int intValue when intValue >= 0 => (uint)intValue,
            long longValue when longValue >= 0 && longValue <= uint.MaxValue => (uint)longValue,
            _ => null
        };
    }

    private static DateTimeOffset? NormalizeLastWriteUtc(DateTimeOffset value)
    {
        if (value == default || value.Year < 1980)
            return null;

        return value.ToUniversalTime();
    }

    private sealed record ZipManifestEntry(
        string Path,
        bool IsDirectory,
        long SizeBytes,
        long CompressedSizeBytes,
        uint? Crc32,
        DateTimeOffset? LastWriteUtc);
}
