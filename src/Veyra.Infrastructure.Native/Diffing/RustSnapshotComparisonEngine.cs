using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.PendingChanges;
using Veyra.Application.DTOs.Repository.Comparison;
using Veyra.Application.Services;
using Veyra.Infrastructure.Native.Execution;
using Veyra.Infrastructure.Native.Interop;
using Veyra.Infrastructure.Native.Runtime;

namespace Veyra.Infrastructure.Native.Diffing;

public sealed class RustSnapshotComparisonEngine : ISnapshotComparisonEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ManagedSnapshotComparisonEngine _managed;
    private readonly INativeExecutionScheduler _scheduler;
    private readonly ILogger<RustSnapshotComparisonEngine> _log;
    private readonly object _gate = new();

    private bool _nativeSnapshotComparisonAvailable;
    private bool _nativeRepositoryPathComparisonAvailable;
    private bool _nativeVersionPlannerAvailable;

    public RustSnapshotComparisonEngine(
        ManagedSnapshotComparisonEngine managed,
        INativeExecutionScheduler scheduler,
        ILogger<RustSnapshotComparisonEngine> log)
    {
        _managed = managed;
        _scheduler = scheduler;
        _log = log;

        var native = NativeRuntimeHealth.Probe();
        _nativeSnapshotComparisonAvailable = native.SupportsSnapshotComparison;
        _nativeRepositoryPathComparisonAvailable = native.SupportsRepositoryPathComparison;
        _nativeVersionPlannerAvailable = native.SupportsVersionPlanning;
    }

    public async Task<SnapshotLinkComparisonResultDto> CompareSnapshotLinksAsync(
        IReadOnlyCollection<SnapshotLinkStateDto> current,
        IReadOnlyCollection<SnapshotLinkStateDto> previous,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_nativeSnapshotComparisonAvailable)
        {
            try
            {
                return await _scheduler.RunAsync(() =>
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

                    var result = new SnapshotLinkComparisonResultDto(changedCount, changes);
                    NativeFeatureUsageTracker.MarkNativeHit(NativeFeatureUsageTracker.SnapshotComparison);
                    return result;
                }, ct);
            }
            catch (Exception ex) when (IsNativeUnavailable(ex))
            {
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.SnapshotComparison);
                DisableNativeSnapshotComparison(ex);
            }
            catch (Exception ex)
            {
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.SnapshotComparison);
                _log.LogWarning(ex, "Native snapshot comparison failed. Falling back to managed engine.");
            }
        }

        return await _managed.CompareSnapshotLinksAsync(current, previous, ct);
    }

    public async Task<RepositoryPathComparisonResultDto> CompareRepositoryPathsAsync(
        IReadOnlyCollection<RepositoryPathStateDto> current,
        IReadOnlyCollection<RepositoryPathStateDto> baseline,
        int take = 2000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var safeTake = Math.Clamp(take, 1, 5000);

        if (_nativeRepositoryPathComparisonAvailable)
        {
            try
            {
                return await _scheduler.RunAsync(() =>
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

                    var result = new RepositoryPathComparisonResultDto(
                        payload.AddedCount,
                        payload.ModifiedCount,
                        payload.DeletedCount,
                        changedCount,
                        entries);
                    NativeFeatureUsageTracker.MarkNativeHit(NativeFeatureUsageTracker.RepositoryPathComparison);
                    return result;
                }, ct);
            }
            catch (Exception ex) when (IsNativeUnavailable(ex))
            {
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.RepositoryPathComparison);
                DisableNativeRepositoryPathComparison(ex);
            }
            catch (Exception ex)
            {
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.RepositoryPathComparison);
                _log.LogWarning(ex, "Native repository path comparison failed. Falling back to managed engine.");
            }
        }

        return await _managed.CompareRepositoryPathsAsync(current, baseline, safeTake, ct);
    }

    public async Task<RepositoryVersionPlanningResultDto> PlanRepositoryVersionsAsync(
        IReadOnlyCollection<RepositoryVersionPlanningFileStateDto> states,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_nativeVersionPlannerAvailable)
        {
            try
            {
                return await _scheduler.RunAsync(() =>
                {
                    var payload = states.Select(s => new NativeVersionPlanningStatePayload
                    {
                        RelativePath = s.RelativePath,
                        HasCurrent = s.HasCurrent,
                        CurrentSizeBytes = s.CurrentSizeBytes,
                        CurrentContentHashSha256 = s.CurrentContentHashSha256,
                        HasPrevious = s.HasPrevious,
                        PreviousSizeBytes = s.PreviousSizeBytes,
                        PreviousContentHashSha256 = s.PreviousContentHashSha256,
                        HasLatestVersion = s.HasLatestVersion,
                        LatestIsDeletionMarker = s.LatestIsDeletionMarker,
                        LatestSizeBytes = s.LatestSizeBytes,
                        LatestHasBlocks = s.LatestHasBlocks
                    }).ToList();

                    var statesJson = JsonSerializer.Serialize(payload, JsonOptions);
                    var json = VeyraCoreNative.PlanRepositoryVersionsJson(statesJson);
                    var planned = JsonSerializer.Deserialize<NativeVersionPlanningPayload>(json, JsonOptions)
                                  ?? throw new InvalidOperationException("Native repository version planner payload is empty.");

                    var entries = planned.Entries
                        .Select(e => new RepositoryVersionPlanEntryDto(
                            e.RelativePath ?? string.Empty,
                            e.ChangeKind ?? "unchanged",
                            e.ShouldCreateNewVersion,
                            e.ShouldMarkIdentityDeleted))
                        .ToList();

                    var changedFilesCount = planned.ChangedFilesCount > 0
                        ? planned.ChangedFilesCount
                        : entries.Count(e => !string.Equals(e.ChangeKind, "unchanged", StringComparison.OrdinalIgnoreCase));

                    var newVersionsCount = planned.NewVersionsCount > 0
                        ? planned.NewVersionsCount
                        : entries.Count(e => e.ShouldCreateNewVersion);

                    var result = new RepositoryVersionPlanningResultDto(
                        changedFilesCount,
                        newVersionsCount,
                        entries);
                    NativeFeatureUsageTracker.MarkNativeHit(NativeFeatureUsageTracker.VersionPlanning);
                    return result;
                }, ct);
            }
            catch (Exception ex) when (IsNativeUnavailable(ex))
            {
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.VersionPlanning);
                DisableNativeVersionPlanner(ex);
            }
            catch (Exception ex)
            {
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.VersionPlanning);
                _log.LogWarning(ex, "Native repository version planner failed. Falling back to managed engine.");
            }
        }

        return await _managed.PlanRepositoryVersionsAsync(states, ct);
    }

    private void DisableNativeSnapshotComparison(Exception ex)
    {
        var switched = false;

        lock (_gate)
        {
            if (_nativeSnapshotComparisonAvailable)
            {
                _nativeSnapshotComparisonAvailable = false;
                switched = true;
            }
        }

        if (switched)
            _log.LogWarning(ex, "Native snapshot comparison entrypoint became unavailable. Falling back to managed engine.");
        else
            _log.LogDebug(ex, "Native snapshot comparison is unavailable. Managed engine remains active.");
    }

    private void DisableNativeRepositoryPathComparison(Exception ex)
    {
        var switched = false;

        lock (_gate)
        {
            if (_nativeRepositoryPathComparisonAvailable)
            {
                _nativeRepositoryPathComparisonAvailable = false;
                switched = true;
            }
        }

        if (switched)
            _log.LogWarning(ex, "Native repository path comparison entrypoint became unavailable. Falling back to managed engine.");
        else
            _log.LogDebug(ex, "Native repository path comparison is unavailable. Managed engine remains active.");
    }

    private void DisableNativeVersionPlanner(Exception ex)
    {
        var switched = false;

        lock (_gate)
        {
            if (_nativeVersionPlannerAvailable)
            {
                _nativeVersionPlannerAvailable = false;
                switched = true;
            }
        }

        if (switched)
            _log.LogWarning(ex, "Native repository version planner entrypoint became unavailable. Falling back to managed engine.");
        else
            _log.LogDebug(ex, "Native repository version planner is unavailable. Managed engine remains active.");
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
                   || ioe.Message.Contains("Native repository path comparison", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Native repository version planner", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

}
