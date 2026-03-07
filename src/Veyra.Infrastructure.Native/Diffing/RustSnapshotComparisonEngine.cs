using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Application.Services;
using Veyra.Infrastructure.Native.Interop;

namespace Veyra.Infrastructure.Native.Diffing;

public sealed class RustSnapshotComparisonEngine(
    ManagedSnapshotComparisonEngine managed,
    ILogger<RustSnapshotComparisonEngine> log) : ISnapshotComparisonEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<SnapshotLinkComparisonResultDto> CompareSnapshotLinksAsync(
        IReadOnlyCollection<SnapshotLinkStateDto> current,
        IReadOnlyCollection<SnapshotLinkStateDto> previous,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var currentPayload = current.Select(ToLinkPayload).ToList();
            var previousPayload = previous.Select(ToLinkPayload).ToList();

            var currentJson = JsonSerializer.Serialize(currentPayload, JsonOptions);
            var previousJson = JsonSerializer.Serialize(previousPayload, JsonOptions);

            var json = VeyraCoreNative.CompareSnapshotLinksJson(currentJson, previousJson);
            var payload = JsonSerializer.Deserialize<NativeLinkComparisonPayload>(json, JsonOptions)
                          ?? throw new InvalidOperationException("Native snapshot comparison payload is empty.");

            var changes = payload.Changes
                .Select(c => new SnapshotLinkChangeDto(
                    c.FileIdentityId,
                    c.FileVersionId,
                    c.RelativePath ?? string.Empty,
                    c.Name ?? string.Empty,
                    c.ChangeKind ?? "modified",
                    c.CurrentSizeBytes,
                    c.PreviousSizeBytes,
                    DateTimeOffset.FromUnixTimeSeconds(c.VersionCreatedUnixSeconds).UtcDateTime))
                .ToList();

            var changedCount = payload.ChangedFilesCount > 0
                ? payload.ChangedFilesCount
                : changes.Count;

            return new SnapshotLinkComparisonResultDto(changedCount, changes);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            log.LogDebug(ex, "Native snapshot comparison entrypoint is unavailable. Falling back to managed engine.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Native snapshot comparison failed. Falling back to managed engine.");
        }

        return await managed.CompareSnapshotLinksAsync(current, previous, ct);
    }

    public async Task<RepositoryPathComparisonResultDto> CompareRepositoryPathsAsync(
        IReadOnlyCollection<RepositoryPathStateDto> current,
        IReadOnlyCollection<RepositoryPathStateDto> baseline,
        int take = 2000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var safeTake = Math.Clamp(take, 1, 5000);

        try
        {
            var currentPayload = current.Select(ToPathPayload).ToList();
            var baselinePayload = baseline.Select(ToPathPayload).ToList();

            var currentJson = JsonSerializer.Serialize(currentPayload, JsonOptions);
            var baselineJson = JsonSerializer.Serialize(baselinePayload, JsonOptions);

            var json = VeyraCoreNative.CompareRepositoryPathsJson(currentJson, baselineJson, safeTake);
            var payload = JsonSerializer.Deserialize<NativePathComparisonPayload>(json, JsonOptions)
                          ?? throw new InvalidOperationException("Native repository path comparison payload is empty.");

            var entries = payload.Changes
                .Select(c => new RepositoryPendingChangeEntryDto(
                    c.RelativePath ?? string.Empty,
                    c.Name ?? string.Empty,
                    c.ChangeKind ?? "modified",
                    c.CurrentSizeBytes,
                    c.BaselineSizeBytes,
                    DateTimeOffset.FromUnixTimeSeconds(c.CurrentLastWriteUnixSeconds).UtcDateTime,
                    c.BaselineLastWriteUnixSeconds.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(c.BaselineLastWriteUnixSeconds.Value).UtcDateTime
                        : null))
                .ToList();

            var changedCount = payload.ChangedFilesCount > 0
                ? payload.ChangedFilesCount
                : payload.AddedCount + payload.ModifiedCount + payload.DeletedCount;

            return new RepositoryPathComparisonResultDto(
                payload.AddedCount,
                payload.ModifiedCount,
                payload.DeletedCount,
                changedCount,
                entries);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            log.LogDebug(ex, "Native repository path comparison entrypoint is unavailable. Falling back to managed engine.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Native repository path comparison failed. Falling back to managed engine.");
        }

        return await managed.CompareRepositoryPathsAsync(current, baseline, safeTake, ct);
    }

    private static NativeLinkStatePayload ToLinkPayload(SnapshotLinkStateDto state)
    {
        var utc = state.VersionCreatedAtUtc.Kind == DateTimeKind.Utc
            ? state.VersionCreatedAtUtc
            : DateTime.SpecifyKind(state.VersionCreatedAtUtc, DateTimeKind.Utc);

        return new NativeLinkStatePayload
        {
            FileIdentityId = state.FileIdentityId,
            FileVersionId = state.FileVersionId,
            IsDeletionMarker = state.IsDeletionMarker,
            SizeBytes = state.SizeBytes,
            VersionCreatedUnixSeconds = new DateTimeOffset(utc).ToUnixTimeSeconds(),
            RelativePath = state.RelativePath,
            Name = state.Name
        };
    }

    private static NativePathStatePayload ToPathPayload(RepositoryPathStateDto state)
    {
        var utc = state.LastWriteUtc.Kind == DateTimeKind.Utc
            ? state.LastWriteUtc
            : DateTime.SpecifyKind(state.LastWriteUtc, DateTimeKind.Utc);

        return new NativePathStatePayload
        {
            RelativePath = state.RelativePath,
            Name = state.Name,
            SizeBytes = state.SizeBytes,
            LastWriteUnixSeconds = new DateTimeOffset(utc).ToUnixTimeSeconds(),
            ContentHashSha256 = state.ContentHashSha256
        };
    }

    private static bool IsNativeUnavailable(Exception ex)
    {
        if (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
            return true;

        if (ex is InvalidOperationException ioe)
        {
            return ioe.Message.Contains("entry point", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Unable to load DLL", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Native snapshot comparison", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Native repository path comparison", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private sealed record NativeLinkStatePayload
    {
        [JsonPropertyName("file_identity_id")]
        public long FileIdentityId { get; init; }

        [JsonPropertyName("file_version_id")]
        public long FileVersionId { get; init; }

        [JsonPropertyName("is_deletion_marker")]
        public bool IsDeletionMarker { get; init; }

        [JsonPropertyName("size_bytes")]
        public long SizeBytes { get; init; }

        [JsonPropertyName("version_created_unix_seconds")]
        public long VersionCreatedUnixSeconds { get; init; }

        [JsonPropertyName("relative_path")]
        public string RelativePath { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    private sealed record NativeLinkComparisonPayload
    {
        [JsonPropertyName("changed_files_count")]
        public int ChangedFilesCount { get; init; }

        [JsonPropertyName("changes")]
        public List<NativeLinkChangePayload> Changes { get; init; } = [];
    }

    private sealed record NativeLinkChangePayload
    {
        [JsonPropertyName("file_identity_id")]
        public long FileIdentityId { get; init; }

        [JsonPropertyName("file_version_id")]
        public long FileVersionId { get; init; }

        [JsonPropertyName("relative_path")]
        public string? RelativePath { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("change_kind")]
        public string? ChangeKind { get; init; }

        [JsonPropertyName("current_size_bytes")]
        public long CurrentSizeBytes { get; init; }

        [JsonPropertyName("previous_size_bytes")]
        public long PreviousSizeBytes { get; init; }

        [JsonPropertyName("version_created_unix_seconds")]
        public long VersionCreatedUnixSeconds { get; init; }
    }

    private sealed record NativePathStatePayload
    {
        [JsonPropertyName("relative_path")]
        public string RelativePath { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("size_bytes")]
        public long SizeBytes { get; init; }

        [JsonPropertyName("last_write_unix_seconds")]
        public long LastWriteUnixSeconds { get; init; }

        [JsonPropertyName("content_hash_sha256")]
        public string? ContentHashSha256 { get; init; }
    }

    private sealed record NativePathComparisonPayload
    {
        [JsonPropertyName("added_count")]
        public int AddedCount { get; init; }

        [JsonPropertyName("modified_count")]
        public int ModifiedCount { get; init; }

        [JsonPropertyName("deleted_count")]
        public int DeletedCount { get; init; }

        [JsonPropertyName("changed_files_count")]
        public int ChangedFilesCount { get; init; }

        [JsonPropertyName("changes")]
        public List<NativePathChangePayload> Changes { get; init; } = [];
    }

    private sealed record NativePathChangePayload
    {
        [JsonPropertyName("relative_path")]
        public string? RelativePath { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("change_kind")]
        public string? ChangeKind { get; init; }

        [JsonPropertyName("current_size_bytes")]
        public long CurrentSizeBytes { get; init; }

        [JsonPropertyName("baseline_size_bytes")]
        public long BaselineSizeBytes { get; init; }

        [JsonPropertyName("current_last_write_unix_seconds")]
        public long CurrentLastWriteUnixSeconds { get; init; }

        [JsonPropertyName("baseline_last_write_unix_seconds")]
        public long? BaselineLastWriteUnixSeconds { get; init; }
    }
}
