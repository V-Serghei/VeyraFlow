using System.Text.Json;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Services;
using Veyra.Infrastructure.Native.Execution;
using Veyra.Infrastructure.Native.Interop;
using Veyra.Infrastructure.Native.Runtime;

namespace Veyra.Infrastructure.Native.Retention;

public sealed class RustRepositoryRetentionPlanner(
    ManagedRepositoryRetentionPlanner managed,
    INativeExecutionScheduler scheduler,
    ILogger<RustRepositoryRetentionPlanner> log)
    : IRepositoryRetentionPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private bool _nativeRetentionPlanningAvailable = NativeRuntimeHealth.Probe().SupportsRetentionPlanning;

    public async Task<RepositoryRetentionPlanResult> PlanAsync(
        RepositoryRetentionPlanRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_nativeRetentionPlanningAvailable)
        {
            try
            {
                return await scheduler.RunAsync(() =>
                {
                    var payload = ToPayload(request);
                    var requestJson = JsonSerializer.Serialize(payload, JsonOptions);
                    var resultJson = VeyraCoreNative.PlanRetentionSnapshotsJson(requestJson);
                    var result = JsonSerializer.Deserialize<NativeRetentionPlanPayload>(resultJson, JsonOptions)
                                 ?? throw new InvalidOperationException("Native retention planner payload is empty.");

                    NativeFeatureUsageTracker.MarkNativeHit(NativeFeatureUsageTracker.RetentionPlanning);
                    return new RepositoryRetentionPlanResult(
                        result.SnapshotIdsToDelete,
                        result.AutomaticSnapshotsCompacted);
                }, ct);
            }
            catch (Exception ex) when (IsNativeUnavailable(ex))
            {
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.RetentionPlanning);
                DisableNativeRetentionPlanning(ex);
            }
            catch (Exception ex)
            {
                NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.RetentionPlanning);
                log.LogWarning(ex, "Native repository retention planner failed. Falling back to managed planner.");
            }
        }

        return await managed.PlanAsync(request, ct);
    }

    private void DisableNativeRetentionPlanning(Exception ex)
    {
        var switched = false;

        lock (_gate)
        {
            if (_nativeRetentionPlanningAvailable)
            {
                _nativeRetentionPlanningAvailable = false;
                switched = true;
            }
        }

        if (switched)
            log.LogWarning(ex, "Native repository retention planner entrypoint became unavailable. Falling back to managed planner.");
        else
            log.LogDebug(ex, "Native repository retention planner is unavailable. Managed planner remains active.");
    }

    private static NativeRetentionPlanRequestPayload ToPayload(RepositoryRetentionPlanRequest request)
        => new()
        {
            Snapshots = request.Snapshots.Select(ToPayload).ToList(),
            MaxAgeDays = request.MaxAgeDays,
            MaxSnapshots = request.MaxSnapshots,
            MaxTotalSizeBytes = request.MaxTotalSizeBytes,
            TriggerFilter = request.TriggerFilter.ToList(),
            AllowManualSnapshotCleanup = request.AllowManualSnapshotCleanup,
            AutomaticCompactionEnabled = request.AutomaticCompactionEnabled,
            AutomaticCompactionWindowHours = request.AutomaticCompactionWindowHours,
            NowTicks = request.NowUtc.Ticks
        };

    private static NativeRetentionSnapshotStatePayload ToPayload(RepositoryRetentionSnapshotPlanState state)
        => new()
        {
            Id = state.Id,
            CreatedAtTicks = state.CreatedAt.Ticks,
            CreatedAtUtcTicks = ToUtcTicks(state.CreatedAt),
            TotalFileBytes = state.TotalFileBytes,
            Trigger = state.Trigger,
            IsProtected = state.IsProtected,
            IsManual = state.IsManual,
            IsAutomatic = state.IsAutomatic,
            IsAutomaticMatch = state.IsAutomaticMatch,
            IsWorking = state.IsWorking
        };

    private static long ToUtcTicks(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value.Ticks
            : value.ToUniversalTime().Ticks;

    private static bool IsNativeUnavailable(Exception ex)
    {
        if (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
            return true;

        if (ex is InvalidOperationException ioe)
        {
            return ioe.Message.Contains("entry point", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Unable to load DLL", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Native repository retention planner", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
