use std::collections::{HashMap, HashSet};

use serde::{Deserialize, Serialize};

const TICKS_PER_HOUR: i64 = 36_000_000_000;
const TICKS_PER_DAY: i64 = 864_000_000_000;

#[derive(Debug, Deserialize)]
struct RetentionPlanRequest {
    snapshots: Vec<RetentionSnapshotState>,
    max_age_days: Option<i32>,
    max_snapshots: Option<i32>,
    max_total_size_bytes: Option<i64>,
    trigger_filter: Vec<String>,
    allow_manual_snapshot_cleanup: bool,
    automatic_compaction_enabled: bool,
    automatic_compaction_window_hours: Option<i32>,
    now_ticks: i64,
}

#[derive(Clone, Debug, Deserialize)]
struct RetentionSnapshotState {
    id: i64,
    created_at_ticks: i64,
    created_at_utc_ticks: i64,
    total_file_bytes: i64,
    trigger: String,
    is_protected: bool,
    is_manual: bool,
    is_automatic: bool,
    is_automatic_match: bool,
    is_working: bool,
}

#[derive(Debug, Serialize)]
struct RetentionPlanResult {
    snapshot_ids_to_delete: Vec<i64>,
    automatic_snapshots_compacted: i32,
}

pub fn plan_retention_snapshots_json(request_json: &str) -> Result<Vec<u8>, String> {
    let request: RetentionPlanRequest = serde_json::from_str(request_json)
        .map_err(|e| format!("invalid retention plan request json: {e}"))?;
    let result = plan_retention_snapshots(&request);
    serde_json::to_vec(&result).map_err(|e| format!("failed to serialize retention plan: {e}"))
}

fn plan_retention_snapshots(request: &RetentionPlanRequest) -> RetentionPlanResult {
    let eligible: Vec<_> = request
        .snapshots
        .iter()
        .filter(|s| !s.is_protected)
        .filter(|s| {
            matches_trigger_filter(
                s,
                &request.trigger_filter,
                request.allow_manual_snapshot_cleanup,
            )
        })
        .collect();

    let mut to_delete = HashSet::new();

    if let Some(days) = request.max_age_days.filter(|v| *v > 0) {
        let cutoff = request.now_ticks - i64::from(days) * TICKS_PER_DAY;
        for snapshot in &eligible {
            if snapshot.created_at_ticks < cutoff {
                to_delete.insert(snapshot.id);
            }
        }
    }

    let automatic_snapshots_compacted = apply_automatic_compaction_candidates(
        &request.snapshots,
        &mut to_delete,
        &request.trigger_filter,
        request.allow_manual_snapshot_cleanup,
        request.automatic_compaction_enabled,
        request.automatic_compaction_window_hours,
    );

    if let Some(max_snapshots) = request.max_snapshots.filter(|v| *v > 0) {
        let mut sorted: Vec<_> = eligible
            .iter()
            .copied()
            .filter(|s| !to_delete.contains(&s.id))
            .collect();
        sorted.sort_by(|a, b| {
            b.created_at_ticks
                .cmp(&a.created_at_ticks)
                .then_with(|| b.id.cmp(&a.id))
        });

        let keep: HashSet<i64> = sorted
            .iter()
            .take(max_snapshots as usize)
            .map(|s| s.id)
            .collect();

        for snapshot in sorted {
            if !keep.contains(&snapshot.id) {
                to_delete.insert(snapshot.id);
            }
        }
    }

    if let Some(max_total_size_bytes) = request.max_total_size_bytes.filter(|v| *v > 0) {
        let mut total: i128 = request
            .snapshots
            .iter()
            .filter(|s| !to_delete.contains(&s.id))
            .map(|s| i128::from(s.total_file_bytes))
            .sum();

        if total > i128::from(max_total_size_bytes) {
            let mut removable: Vec<_> = request
                .snapshots
                .iter()
                .filter(|s| !to_delete.contains(&s.id))
                .filter(|s| {
                    matches_trigger_filter(
                        s,
                        &request.trigger_filter,
                        request.allow_manual_snapshot_cleanup,
                    )
                })
                .collect();
            removable.sort_by(|a, b| {
                a.created_at_ticks
                    .cmp(&b.created_at_ticks)
                    .then_with(|| a.id.cmp(&b.id))
            });

            for snapshot in removable {
                if total <= i128::from(max_total_size_bytes) {
                    break;
                }

                let remaining_count = request.snapshots.len().saturating_sub(to_delete.len());
                if remaining_count <= 1 {
                    break;
                }

                if to_delete.insert(snapshot.id) {
                    total -= i128::from(snapshot.total_file_bytes);
                }
            }
        }
    }

    let mut snapshot_ids_to_delete: Vec<_> = to_delete.into_iter().collect();
    snapshot_ids_to_delete.sort_unstable();

    RetentionPlanResult {
        snapshot_ids_to_delete,
        automatic_snapshots_compacted,
    }
}

fn matches_trigger_filter(
    snapshot: &RetentionSnapshotState,
    trigger_filter: &[String],
    allow_manual_snapshot_cleanup: bool,
) -> bool {
    let normalized_trigger = snapshot.trigger.trim();
    if normalized_trigger.is_empty() {
        return false;
    }

    if !allow_manual_snapshot_cleanup && snapshot.is_manual {
        return false;
    }

    if trigger_filter.is_empty() {
        return true;
    }

    let trigger_lower = normalized_trigger.to_lowercase();

    for token in trigger_filter {
        let token = token.trim();
        if token.is_empty() {
            continue;
        }

        let token_lower = token.to_lowercase();

        if token_lower == "all" {
            return true;
        }

        if token_lower == trigger_lower {
            return true;
        }

        if let Some(prefix) = token.strip_suffix('*') {
            if trigger_lower.starts_with(&prefix.to_lowercase()) {
                return true;
            }
        }

        if token_lower == "automatic" || token_lower == "auto" {
            if snapshot.is_automatic_match {
                return true;
            }

            continue;
        }

        if token_lower == "manual" {
            if allow_manual_snapshot_cleanup && snapshot.is_manual {
                return true;
            }

            continue;
        }

        if token_lower == "working" {
            if snapshot.is_working {
                return true;
            }

            continue;
        }

        if token_lower == "scheduled" && trigger_lower.starts_with("scheduled_") {
            return true;
        }
    }

    false
}

fn apply_automatic_compaction_candidates(
    snapshots: &[RetentionSnapshotState],
    to_delete: &mut HashSet<i64>,
    trigger_filter: &[String],
    allow_manual_snapshot_cleanup: bool,
    automatic_compaction_enabled: bool,
    automatic_compaction_window_hours: Option<i32>,
) -> i32 {
    let Some(window_hours) = automatic_compaction_window_hours.filter(|v| *v > 0) else {
        return 0;
    };

    if !automatic_compaction_enabled {
        return 0;
    }

    let ticks_per_bucket = i64::from(window_hours) * TICKS_PER_HOUR;
    if ticks_per_bucket <= 0 {
        return 0;
    }

    let mut buckets: HashMap<i64, Vec<&RetentionSnapshotState>> = HashMap::new();
    for snapshot in snapshots {
        if to_delete.contains(&snapshot.id) || !snapshot.is_automatic {
            continue;
        }

        if !matches_trigger_filter(snapshot, trigger_filter, allow_manual_snapshot_cleanup) {
            continue;
        }

        buckets
            .entry(snapshot.created_at_utc_ticks / ticks_per_bucket)
            .or_default()
            .push(snapshot);
    }

    let mut compacted = 0;
    for bucket in buckets.values_mut() {
        bucket.sort_by(|a, b| {
            b.created_at_ticks
                .cmp(&a.created_at_ticks)
                .then_with(|| b.id.cmp(&a.id))
        });

        for snapshot in bucket.iter().skip(1) {
            if to_delete.insert(snapshot.id) {
                compacted += 1;
            }
        }
    }

    compacted
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn max_snapshots_keeps_newest_eligible_snapshot() {
        let request = RetentionPlanRequest {
            snapshots: vec![
                snapshot(1, 100, 10, "auto_snapshot"),
                snapshot(2, 200, 10, "auto_snapshot"),
                snapshot(3, 300, 10, "auto_snapshot"),
            ],
            max_age_days: None,
            max_snapshots: Some(1),
            max_total_size_bytes: None,
            trigger_filter: vec!["automatic".to_string()],
            allow_manual_snapshot_cleanup: false,
            automatic_compaction_enabled: false,
            automatic_compaction_window_hours: None,
            now_ticks: 400,
        };

        let result = plan_retention_snapshots(&request);

        assert_eq!(result.snapshot_ids_to_delete, vec![1, 2]);
        assert_eq!(result.automatic_snapshots_compacted, 0);
    }

    #[test]
    fn protected_snapshot_is_not_deleted_by_age_or_count_policy() {
        let mut protected = snapshot(1, 100, 10, "auto_snapshot");
        protected.is_protected = true;

        let request = RetentionPlanRequest {
            snapshots: vec![protected, snapshot(2, 200, 10, "auto_snapshot")],
            max_age_days: Some(1),
            max_snapshots: Some(1),
            max_total_size_bytes: None,
            trigger_filter: vec!["automatic".to_string()],
            allow_manual_snapshot_cleanup: false,
            automatic_compaction_enabled: false,
            automatic_compaction_window_hours: None,
            now_ticks: 100 + (2 * TICKS_PER_DAY),
        };

        let result = plan_retention_snapshots(&request);

        assert!(result.snapshot_ids_to_delete.is_empty());
    }

    #[test]
    fn automatic_compaction_keeps_newest_snapshot_per_bucket() {
        let request = RetentionPlanRequest {
            snapshots: vec![
                snapshot(1, 10, 10, "auto_snapshot"),
                snapshot(2, 20, 10, "auto_snapshot"),
                snapshot(3, TICKS_PER_HOUR + 10, 10, "auto_snapshot"),
            ],
            max_age_days: None,
            max_snapshots: None,
            max_total_size_bytes: None,
            trigger_filter: vec!["automatic".to_string()],
            allow_manual_snapshot_cleanup: false,
            automatic_compaction_enabled: true,
            automatic_compaction_window_hours: Some(1),
            now_ticks: TICKS_PER_HOUR * 2,
        };

        let result = plan_retention_snapshots(&request);

        assert_eq!(result.snapshot_ids_to_delete, vec![1]);
        assert_eq!(result.automatic_snapshots_compacted, 1);
    }

    fn snapshot(
        id: i64,
        created_at_ticks: i64,
        total_file_bytes: i64,
        trigger: &str,
    ) -> RetentionSnapshotState {
        let is_working = trigger.starts_with("scheduled_sync")
            || trigger.starts_with("sync_index")
            || trigger == "sync_live_watcher";
        let is_automatic = trigger.starts_with("auto_snapshot")
            || trigger.starts_with("scheduled_snapshot")
            || trigger.starts_with("initial_snapshot");

        RetentionSnapshotState {
            id,
            created_at_ticks,
            created_at_utc_ticks: created_at_ticks,
            total_file_bytes,
            trigger: trigger.to_string(),
            is_protected: false,
            is_manual: !is_automatic && !is_working,
            is_automatic,
            is_automatic_match: is_automatic || is_working,
            is_working,
        }
    }
}
