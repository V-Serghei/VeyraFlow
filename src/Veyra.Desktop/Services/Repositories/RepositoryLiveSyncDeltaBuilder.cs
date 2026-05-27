using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Application.Common.Repository;
using Veyra.Application.DTOs;
using Veyra.Desktop.Services.Sync;

namespace Veyra.Desktop.Services.Repositories;

public sealed class RepositoryLiveSyncDeltaBuilder : IRepositoryLiveSyncDeltaBuilder
{
    public Task<RepositoryLiveSyncDeltaBuildResult> BuildAsync(
        string repositoryRootPath,
        IReadOnlyCollection<string> linkedFormats,
        IReadOnlyCollection<string> excludedPatterns,
        IReadOnlyList<RepositoryScanEntryDto> currentEntries,
        IReadOnlyList<RepositoryFsEventLeaseItem> events,
        CancellationToken ct = default)
    {
        return Task.Run(
            () => BuildCore(repositoryRootPath, linkedFormats, excludedPatterns, currentEntries, events, ct),
            ct);
    }

    private static RepositoryLiveSyncDeltaBuildResult BuildCore(
        string repositoryRootPath,
        IReadOnlyCollection<string> linkedFormats,
        IReadOnlyCollection<string> excludedPatterns,
        IReadOnlyList<RepositoryScanEntryDto> currentEntries,
        IReadOnlyList<RepositoryFsEventLeaseItem> events,
        CancellationToken ct)
    {
        var normalizedRootPath = NormalizePath(repositoryRootPath);
        if (string.IsNullOrWhiteSpace(normalizedRootPath) || !Directory.Exists(normalizedRootPath))
            return RepositoryLiveSyncDeltaBuildResult.Fallback("root_missing");

        if (events.Count == 0)
            return RepositoryLiveSyncDeltaBuildResult.NoChanges(currentEntries);

        if (events.Any(static item => string.Equals(item.EventKind, "error", StringComparison.OrdinalIgnoreCase)))
            return RepositoryLiveSyncDeltaBuildResult.Fallback("watcher_error");

        var currentByPath = currentEntries
            .DistinctBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);

        var trackedExtensions = NormalizeTrackedExtensions(linkedFormats);
        var upserts = new Dictionary<string, RepositoryScanEntryDto>(StringComparer.OrdinalIgnoreCase);
        var removalRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedEventPaths = events
            .Select(static item => NormalizePath(item.FullPath))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => CountPathDepth(path))
            .ToList();

        foreach (var fullPath in normalizedEventPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryGetRelativePath(normalizedRootPath, fullPath, out var relativePath, out var refersToRoot))
                return RepositoryLiveSyncDeltaBuildResult.Fallback("path_outside_root");

            if (refersToRoot || string.IsNullOrWhiteSpace(relativePath))
                return RepositoryLiveSyncDeltaBuildResult.Fallback("root_level_change");

            if (File.Exists(fullPath))
            {
                removalRoots.Add(relativePath);

                if (!ShouldTrackFile(relativePath, trackedExtensions, excludedPatterns))
                {
                    RefreshParentDirectories(normalizedRootPath, relativePath, excludedPatterns, upserts);
                    continue;
                }

                var currentEntry = currentByPath.GetValueOrDefault(relativePath);
                upserts[relativePath] = BuildFileEntry(normalizedRootPath, fullPath, relativePath, currentEntry);
                RefreshParentDirectories(normalizedRootPath, relativePath, excludedPatterns, upserts);
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                removalRoots.Add(relativePath);

                if (!IsExcluded(relativePath, excludedPatterns))
                {
                    foreach (var entry in EnumerateDirectorySubtreeEntries(
                                 normalizedRootPath,
                                 fullPath,
                                 trackedExtensions,
                                 excludedPatterns,
                                 currentByPath,
                                 ct))
                    {
                        upserts[entry.RelativePath] = entry;
                    }
                }

                RefreshParentDirectories(normalizedRootPath, relativePath, excludedPatterns, upserts);
                continue;
            }

            removalRoots.Add(relativePath);
            RefreshParentDirectories(normalizedRootPath, relativePath, excludedPatterns, upserts);
        }

        var normalizedRemovalRoots = CompressRemovalRoots(removalRoots);
        var mergedEntries = MergeEntries(currentByPath, upserts, normalizedRemovalRoots);

        if (EntriesEquivalent(currentEntries, mergedEntries))
            return RepositoryLiveSyncDeltaBuildResult.NoChanges(mergedEntries);

        return new RepositoryLiveSyncDeltaBuildResult(
            CanApplyIncrementally: true,
            HasMeaningfulChanges: true,
            UpdatedEntries: mergedEntries,
            UpsertEntries: upserts.Values
                .OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            RemovedPaths: normalizedRemovalRoots);
    }

    private static IReadOnlyList<RepositoryScanEntryDto> MergeEntries(
        IReadOnlyDictionary<string, RepositoryScanEntryDto> currentByPath,
        IReadOnlyDictionary<string, RepositoryScanEntryDto> upserts,
        IReadOnlyCollection<string> removalRoots)
    {
        var merged = currentByPath.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var removalRoot in removalRoots)
        {
            var affectedPaths = merged.Keys
                .Where(path => IsPathOrDescendant(path, removalRoot))
                .ToList();

            foreach (var affectedPath in affectedPaths)
                merged.Remove(affectedPath);
        }

        foreach (var upsert in upserts.Values)
            merged[upsert.RelativePath] = upsert;

        return merged.Values
            .OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<RepositoryScanEntryDto> EnumerateDirectorySubtreeEntries(
        string repositoryRootPath,
        string directoryFullPath,
        IReadOnlySet<string> trackedExtensions,
        IReadOnlyCollection<string> excludedPatterns,
        IReadOnlyDictionary<string, RepositoryScanEntryDto> currentByPath,
        CancellationToken ct)
    {
        var rootRelativePath = NormalizeRelativePath(Path.GetRelativePath(repositoryRootPath, directoryFullPath));
        if (string.IsNullOrWhiteSpace(rootRelativePath))
            yield break;

        var pendingDirectories = new Stack<DirectoryInfo>();
        pendingDirectories.Push(new DirectoryInfo(directoryFullPath));

        while (pendingDirectories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();
            var currentRelativePath = NormalizeRelativePath(Path.GetRelativePath(repositoryRootPath, currentDirectory.FullName));

            if (string.IsNullOrWhiteSpace(currentRelativePath) || IsExcluded(currentRelativePath, excludedPatterns))
                continue;

            yield return new RepositoryScanEntryDto(
                currentRelativePath,
                GetParentRelativePath(currentRelativePath),
                currentDirectory.Name,
                IsDirectory: true,
                Extension: null,
                SizeBytes: 0,
                LastWriteUtc: SafeGetLastWriteUtc(currentDirectory),
                ContentHashSha256: null);

            foreach (var childDirectory in SafeEnumerateDirectories(currentDirectory))
                pendingDirectories.Push(childDirectory);

            foreach (var childFile in SafeEnumerateFiles(currentDirectory))
            {
                ct.ThrowIfCancellationRequested();

                var childRelativePath = NormalizeRelativePath(Path.GetRelativePath(repositoryRootPath, childFile.FullName));
                if (string.IsNullOrWhiteSpace(childRelativePath) || IsExcluded(childRelativePath, excludedPatterns))
                    continue;

                var normalizedExtension = NormalizeExtension(childFile.Extension);
                if (trackedExtensions.Count > 0
                    && (string.IsNullOrWhiteSpace(normalizedExtension) || !trackedExtensions.Contains(normalizedExtension)))
                {
                    continue;
                }

                currentByPath.TryGetValue(childRelativePath, out var currentEntry);
                yield return BuildFileEntry(repositoryRootPath, childFile.FullName, childRelativePath, currentEntry);
            }
        }
    }

    private static void RefreshParentDirectories(
        string repositoryRootPath,
        string relativePath,
        IReadOnlyCollection<string> excludedPatterns,
        IDictionary<string, RepositoryScanEntryDto> upserts)
    {
        var current = GetParentRelativePath(relativePath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (!IsExcluded(current, excludedPatterns))
            {
                var fullPath = Path.Combine(
                    repositoryRootPath,
                    current.Replace('/', Path.DirectorySeparatorChar));

                if (Directory.Exists(fullPath))
                {
                    var info = new DirectoryInfo(fullPath);
                    upserts[current] = new RepositoryScanEntryDto(
                        current,
                        GetParentRelativePath(current),
                        info.Name,
                        IsDirectory: true,
                        Extension: null,
                        SizeBytes: 0,
                        LastWriteUtc: SafeGetLastWriteUtc(info),
                        ContentHashSha256: null);
                }
            }

            current = GetParentRelativePath(current);
        }
    }

    private static RepositoryScanEntryDto BuildFileEntry(
        string repositoryRootPath,
        string fullPath,
        string relativePath,
        RepositoryScanEntryDto? currentEntry)
    {
        var info = new FileInfo(fullPath);
        var extension = NormalizeExtension(info.Extension);
        var lastWriteUtc = info.LastWriteTimeUtc;

        var hash = currentEntry is not null
                   && !currentEntry.IsDirectory
                   && currentEntry.SizeBytes == info.Length
                   && currentEntry.LastWriteUtc == lastWriteUtc
                       ? currentEntry.ContentHashSha256
                       : ComputeSha256(fullPath);

        return new RepositoryScanEntryDto(
            relativePath,
            GetParentRelativePath(relativePath),
            Path.GetFileName(fullPath),
            IsDirectory: false,
            Extension: extension,
            SizeBytes: info.Length,
            LastWriteUtc: lastWriteUtc,
            ContentHashSha256: hash);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            useAsync: false);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool EntriesEquivalent(
        IReadOnlyList<RepositoryScanEntryDto> left,
        IReadOnlyList<RepositoryScanEntryDto> right)
    {
        if (left.Count != right.Count)
            return false;

        var rightByPath = right.ToDictionary(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var leftEntry in left)
        {
            if (!rightByPath.TryGetValue(leftEntry.RelativePath, out var rightEntry))
                return false;

            if (!EntryEquivalent(leftEntry, rightEntry))
                return false;
        }

        return true;
    }

    private static bool EntryEquivalent(RepositoryScanEntryDto left, RepositoryScanEntryDto right)
    {
        return left.IsDirectory == right.IsDirectory
               && left.SizeBytes == right.SizeBytes
               && left.LastWriteUtc == right.LastWriteUtc
               && string.Equals(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.ParentRelativePath, right.ParentRelativePath, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
               && string.Equals(left.Extension, right.Extension, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.ContentHashSha256, right.ContentHashSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> CompressRemovalRoots(IEnumerable<string> removalRoots)
    {
        var ordered = removalRoots
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => CountPathDepth(path))
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<string>(ordered.Count);
        foreach (var root in ordered)
        {
            if (result.Any(existing => IsPathOrDescendant(root, existing)))
                continue;

            result.Add(root);
        }

        return result;
    }

    private static bool ShouldTrackFile(
        string relativePath,
        IReadOnlySet<string> trackedExtensions,
        IReadOnlyCollection<string> excludedPatterns)
    {
        if (IsExcluded(relativePath, excludedPatterns))
            return false;

        if (trackedExtensions.Count == 0)
            return true;

        var extension = NormalizeExtension(Path.GetExtension(relativePath));
        return !string.IsNullOrWhiteSpace(extension) && trackedExtensions.Contains(extension);
    }

    private static IReadOnlySet<string> NormalizeTrackedExtensions(IReadOnlyCollection<string> linkedFormats)
    {
        return linkedFormats
            .Where(static format => !string.IsNullOrWhiteSpace(format))
            .Select(static format => format.Trim())
            .Select(NormalizeExtension)
            .Where(static extension => !string.IsNullOrWhiteSpace(extension))
            .Select(static extension => extension!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetRelativePath(
        string repositoryRootPath,
        string fullPath,
        out string relativePath,
        out bool refersToRoot)
    {
        relativePath = string.Empty;
        refersToRoot = false;

        var normalizedRoot = NormalizePath(repositoryRootPath);
        var normalizedFullPath = NormalizePath(fullPath);
        if (string.IsNullOrWhiteSpace(normalizedRoot) || string.IsNullOrWhiteSpace(normalizedFullPath))
            return false;

        if (!normalizedFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(normalizedFullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            refersToRoot = true;
            return true;
        }

        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!normalizedFullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return false;

        relativePath = NormalizeRelativePath(Path.GetRelativePath(normalizedRoot, normalizedFullPath));
        return !string.IsNullOrWhiteSpace(relativePath);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeRelativePath(string relativePath)
        => relativePath.Replace('\\', '/').Trim('/');

    private static string? GetParentRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var normalized = NormalizeRelativePath(relativePath);
        var idx = normalized.LastIndexOf('/');
        return idx <= 0 ? null : normalized[..idx];
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        var normalized = extension.Trim().ToLowerInvariant();
        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }

    private static bool IsPathOrDescendant(string candidatePath, string ancestorPath)
    {
        if (string.Equals(candidatePath, ancestorPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return candidatePath.StartsWith(ancestorPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountPathDepth(string path)
        => path.Count(static ch => ch is '/' or '\\');

    private static DateTime SafeGetLastWriteUtc(FileSystemInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc;
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateDirectories();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsExcluded(string? relativePath, IReadOnlyCollection<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalizedPath = NormalizeRelativePath(relativePath);
        if (RepositoryInternalPathFilter.ShouldIgnoreForSnapshotRestore(normalizedPath))
            return true;

        if (patterns.Count == 0)
            return false;

        var fileName = Path.GetFileName(normalizedPath);

        foreach (var rawPattern in patterns)
        {
            var normalizedPattern = string.IsNullOrWhiteSpace(rawPattern)
                ? string.Empty
                : NormalizeRelativePath(rawPattern);
            if (string.IsNullOrWhiteSpace(normalizedPattern))
                continue;

            if (normalizedPattern.Contains('*') || normalizedPattern.Contains('?'))
            {
                if (WildcardMatch(normalizedPath, normalizedPattern) || WildcardMatch(fileName, normalizedPattern))
                    return true;

                continue;
            }

            if (normalizedPath.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedPattern + "/", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
