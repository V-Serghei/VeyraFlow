using System;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.Services.Repositories;

public static class RepositoryLiveSyncStatusPresenter
{
    public static string FormatStateText(RepositoryLiveSyncStatusSnapshot? snapshot)
    {
        if (snapshot is null)
            return Loc.T("explorer.monitoring_state_stopped");

        if (snapshot.IsPaused)
            return Loc.T("explorer.monitoring_state_paused");

        if (snapshot.IsActive)
            return Loc.T("explorer.monitoring_state_active");

        return string.IsNullOrWhiteSpace(snapshot.RootPath) || snapshot.IsPathAvailable
            ? Loc.T("explorer.monitoring_state_stopped")
            : Loc.T("explorer.monitoring_state_unavailable");
    }

    public static string FormatModeText(RepositoryLiveSyncStatusSnapshot? snapshot)
        => snapshot?.SaveFileVersions == true
            ? Loc.T("explorer.monitoring_mode_versions")
            : Loc.T("explorer.monitoring_mode_index_only");

    public static string FormatQueueText(RepositoryLiveSyncStatusSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.QueueTotalCount <= 0)
            return Loc.T("explorer.monitoring_queue_idle");

        return Loc.F(
            "explorer.monitoring_queue_counts",
            snapshot.QueuePendingCount,
            snapshot.QueueRunningCount,
            snapshot.QueueTotalCount,
            snapshot.QueueMaxRetryCount);
    }

    public static string FormatLastProcessedText(RepositoryLiveSyncStatusSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.LastProcessedUtc == DateTime.MinValue)
            return Loc.T("explorer.monitoring_last_processed_never");

        return Loc.F(
            "explorer.monitoring_last_processed_format",
            snapshot.LastProcessedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
    }

    public static string FormatLastResultText(RepositoryLiveSyncStatusSnapshot? snapshot)
    {
        var operation = snapshot?.LastOperation ?? RepositoryLiveSyncOperationSnapshot.None;
        return operation.Kind switch
        {
            RepositoryLiveSyncOperationKind.IncrementalApplied when operation.SaveFileVersions
                => Loc.F(
                    "explorer.monitoring_last_result_incremental_versions",
                    operation.PathCount,
                    operation.UpdatedCount,
                    operation.RemovedCount),
            RepositoryLiveSyncOperationKind.IncrementalApplied
                => Loc.F(
                    "explorer.monitoring_last_result_incremental_index",
                    operation.PathCount,
                    operation.UpdatedCount,
                    operation.RemovedCount),
            RepositoryLiveSyncOperationKind.IncrementalNoChanges when operation.SaveFileVersions
                => Loc.F("explorer.monitoring_last_result_incremental_versions_no_changes", operation.PathCount),
            RepositoryLiveSyncOperationKind.IncrementalNoChanges
                => Loc.F("explorer.monitoring_last_result_incremental_index_no_changes", operation.PathCount),
            RepositoryLiveSyncOperationKind.IncrementalFailed when operation.SaveFileVersions
                => Loc.F("explorer.monitoring_last_result_incremental_versions_failed", operation.PathCount),
            RepositoryLiveSyncOperationKind.IncrementalFailed
                => Loc.F("explorer.monitoring_last_result_incremental_index_failed", operation.PathCount),
            RepositoryLiveSyncOperationKind.FallbackRequested
                => Loc.F(
                    "explorer.monitoring_last_result_fallback_started",
                    LocalizeFallbackReason(operation.Reason),
                    operation.PathCount),
            RepositoryLiveSyncOperationKind.FallbackCompleted
                => Loc.F(
                    "explorer.monitoring_last_result_fallback_completed",
                    LocalizeFallbackReason(operation.Reason),
                    operation.PathCount),
            RepositoryLiveSyncOperationKind.FallbackFailed
                => Loc.F(
                    "explorer.monitoring_last_result_fallback_failed",
                    LocalizeFallbackReason(operation.Reason),
                    operation.PathCount),
            _ => Loc.T("explorer.monitoring_last_result_none")
        };
    }

    public static string FormatSummaryText(RepositoryLiveSyncStatusSnapshot? snapshot)
        => string.IsNullOrWhiteSpace(snapshot?.LastIssue)
            ? FormatLastResultText(snapshot)
            : snapshot!.LastIssue!;

    public static string FormatDetailText(RepositoryLiveSyncStatusSnapshot? snapshot)
        => snapshot is not null && snapshot.QueueTotalCount > 0
            ? FormatQueueText(snapshot)
            : FormatLastProcessedText(snapshot);

    public static string FormatTrayActivityText(string repositoryName, RepositoryLiveSyncStatusSnapshot snapshot)
    {
        if (snapshot.LastOperation.Kind == RepositoryLiveSyncOperationKind.FallbackRequested && snapshot.IsScanRunning)
            return Loc.F("tray.activity_live_sync_fallback", repositoryName);

        return snapshot.SaveFileVersions
            ? Loc.F("tray.activity_live_sync_versions", repositoryName)
            : Loc.F("tray.activity_live_sync_index", repositoryName);
    }

    public static bool IsBusy(RepositoryLiveSyncStatusSnapshot? snapshot)
        => snapshot is not null
           && (snapshot.IsScanRunning
               || snapshot.QueueTotalCount > 0
               || snapshot.LastOperation.Kind == RepositoryLiveSyncOperationKind.FallbackRequested);

    public static int GetActivityPriority(RepositoryLiveSyncStatusSnapshot? snapshot)
    {
        if (snapshot is null)
            return 0;

        if (snapshot.LastOperation.Kind == RepositoryLiveSyncOperationKind.FallbackRequested && snapshot.IsScanRunning)
            return 3;

        if (snapshot.IsScanRunning)
            return 2;

        if (snapshot.QueueTotalCount > 0)
            return 1;

        return 0;
    }

    public static (string AccentColor, string BackgroundColor, string Glyph) DescribeVisualState(RepositoryLiveSyncStatusSnapshot? snapshot)
    {
        if (snapshot is null)
            return ("#94A3B8", "#1694A3B8", "\uE9CE");

        if (!snapshot.IsPathAvailable && !string.IsNullOrWhiteSpace(snapshot.RootPath))
            return ("#F59E0B", "#1AF59E0B", "\uEA39");

        if (!string.IsNullOrWhiteSpace(snapshot.LastIssue)
            || snapshot.LastOperation.Kind == RepositoryLiveSyncOperationKind.FallbackFailed)
        {
            return ("#F97316", "#1AF97316", "\uEA39");
        }

        if (snapshot.LastOperation.Kind == RepositoryLiveSyncOperationKind.FallbackRequested || snapshot.IsScanRunning)
            return ("#38BDF8", "#1638BDF8", "\uE8A7");

        if (snapshot.IsPaused)
            return ("#FBBF24", "#1AFBBF24", "\uE769");

        if (snapshot.IsActive)
            return ("#4ADE80", "#164ADE80", "\uE895");

        return ("#94A3B8", "#1694A3B8", "\uE9CE");
    }

    public static string LocalizeFallbackReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Loc.T("explorer.monitoring_fallback_reason_unknown");

        return reason.Trim() switch
        {
            "root_missing" => Loc.T("explorer.monitoring_fallback_reason_root_missing"),
            "watcher_error" => Loc.T("explorer.monitoring_fallback_reason_watcher_error"),
            "path_outside_root" => Loc.T("explorer.monitoring_fallback_reason_path_outside_root"),
            "root_level_change" => Loc.T("explorer.monitoring_fallback_reason_root_level_change"),
            "snapshot_delta_unavailable" => Loc.T("explorer.monitoring_fallback_reason_snapshot_delta_unavailable"),
            _ => UserFacingMessageLocalizer.TryLocalize(reason)?.Trim()
                 ?? reason.Trim()
        };
    }
}
