using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Infrastructure.Native.Interop;

namespace Veyra.Infrastructure.Native.Scanning;

public sealed class RustRepositoryScanner(
    IRepositoryRepository repositories,
    IRepositorySnapshotRepository snapshots,
    ILogger<RustRepositoryScanner> log)
    : IRepositoryScanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task ScanRepositoryAsync(int repositoryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var repo = await repositories.GetRepositoryByIdAsync(repositoryId, ct);
        if (repo is null || repo.IsDeleted)
            return;

        if (!Directory.Exists(repo.DirectoryPath))
        {
            log.LogWarning("Skipping scan for repository {RepositoryId}. Directory not found {Path}", repositoryId, repo.DirectoryPath);
            return;
        }

        List<RepositoryScanEntryDto> entries;
        string trigger;

        try
        {
            var json = VeyraCoreNative.ScanDirectoryJson(repo.DirectoryPath, repo.LinkedFormats);
            var nativeEntries = JsonSerializer.Deserialize<List<NativeScanEntry>>(json, JsonOptions) ?? [];

            entries = nativeEntries
                .Select(ToEntry)
                .Where(e => !string.IsNullOrWhiteSpace(e.RelativePath))
                .ToList();

            trigger = "initial_scan_rust";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            log.LogWarning(ex,
                "Rust scanner unavailable for repository {RepositoryId}. Falling back to managed scan.",
                repositoryId);

            entries = await BuildManagedEntriesAsync(repo.DirectoryPath, repo.LinkedFormats, ct);
            trigger = "initial_scan_managed_fallback";
        }

        await snapshots.SaveSnapshotAsync(repositoryId, trigger, DateTime.UtcNow, entries, ct);

        log.LogInformation(
            "Repository {RepositoryId} scanned. Entries {EntryCount}. Trigger {Trigger}",
            repositoryId,
            entries.Count,
            trigger);
    }

    public async Task ScanAllRepositoriesAsync(CancellationToken ct = default)
    {
        var repos = await repositories.GetAllRepositoriesAsync(ct);
        foreach (var repo in repos)
        {
            ct.ThrowIfCancellationRequested();
            await ScanRepositoryAsync(repo.Id, ct);
        }
    }

    private static RepositoryScanEntryDto ToEntry(NativeScanEntry src)
    {
        var unix = src.LastWriteUnixSeconds < 0 ? 0 : src.LastWriteUnixSeconds;
        var lastWrite = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        return new RepositoryScanEntryDto(
            src.RelativePath,
            src.ParentRelativePath,
            src.Name,
            src.IsDirectory,
            src.Extension,
            src.SizeBytes,
            lastWrite,
            src.ContentHashSha256);
    }

    private static async Task<List<RepositoryScanEntryDto>> BuildManagedEntriesAsync(
        string rootPath,
        IReadOnlyCollection<string> linkedFormats,
        CancellationToken ct)
    {
        var normalizedExt = linkedFormats
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToLowerInvariant())
            .Select(v => v.StartsWith('.') ? v : "." + v)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var root = new DirectoryInfo(rootPath);
        var entries = new List<RepositoryScanEntryDto>();
        await TraverseDirectoryAsync(root, rootPath, normalizedExt, entries, ct);

        return entries
            .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task TraverseDirectoryAsync(
        DirectoryInfo current,
        string rootPath,
        HashSet<string> extFilter,
        List<RepositoryScanEntryDto> entries,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<DirectoryInfo> dirs;
        try
        {
            dirs = current.EnumerateDirectories();
        }
        catch
        {
            return;
        }

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();

            var relative = ToRelativePath(rootPath, dir.FullName);
            var parentRelative = GetParentRelativePath(relative);

            entries.Add(new RepositoryScanEntryDto(
                relative,
                parentRelative,
                dir.Name,
                true,
                null,
                0,
                dir.LastWriteTimeUtc,
                null));

            await TraverseDirectoryAsync(dir, rootPath, extFilter, entries, ct);
        }

        IEnumerable<FileInfo> files;
        try
        {
            files = current.EnumerateFiles();
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var ext = NormalizeExtension(file.Extension);
            if (extFilter.Count > 0 && (ext is null || !extFilter.Contains(ext)))
                continue;

            string? hash;
            try
            {
                hash = await ComputeSha256Async(file.FullName, ct);
            }
            catch
            {
                continue;
            }

            var relative = ToRelativePath(rootPath, file.FullName);
            var parentRelative = GetParentRelativePath(relative);

            entries.Add(new RepositoryScanEntryDto(
                relative,
                parentRelative,
                file.Name,
                false,
                ext,
                file.Length,
                file.LastWriteTimeUtc,
                hash));
        }
    }

    private static string ToRelativePath(string rootPath, string fullPath)
    {
        var rel = Path.GetRelativePath(rootPath, fullPath)
            .Replace('\\', '/')
            .Trim();

        return rel;
    }

    private static string? GetParentRelativePath(string relativePath)
    {
        var idx = relativePath.LastIndexOf('/');
        if (idx <= 0)
            return null;

        return relativePath[..idx];
    }

    private static string? NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToLowerInvariant();
        return v.StartsWith('.') ? v : "." + v;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record NativeScanEntry
    {
        [JsonPropertyName("relative_path")]
        public string RelativePath { get; init; } = string.Empty;

        [JsonPropertyName("parent_relative_path")]
        public string? ParentRelativePath { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("is_directory")]
        public bool IsDirectory { get; init; }

        [JsonPropertyName("extension")]
        public string? Extension { get; init; }

        [JsonPropertyName("size_bytes")]
        public long SizeBytes { get; init; }

        [JsonPropertyName("last_write_unix_seconds")]
        public long LastWriteUnixSeconds { get; init; }

        [JsonPropertyName("content_hash_sha256")]
        public string? ContentHashSha256 { get; init; }
    }
}
