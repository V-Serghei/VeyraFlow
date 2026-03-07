use std::collections::HashMap;

use serde::{Deserialize, Serialize};

#[derive(Deserialize, Clone)]
struct SnapshotLinkStateInput {
    file_identity_id: i64,
    file_version_id: i64,
    is_deletion_marker: bool,
    size_bytes: i64,
    version_created_unix_seconds: i64,
    relative_path: String,
    name: String,
}

#[derive(Serialize)]
struct SnapshotLinkChangeOutput {
    file_identity_id: i64,
    file_version_id: i64,
    relative_path: String,
    name: String,
    change_kind: String,
    current_size_bytes: i64,
    previous_size_bytes: i64,
    version_created_unix_seconds: i64,
}

#[derive(Serialize)]
struct SnapshotLinkComparisonOutput {
    changed_files_count: u32,
    changes: Vec<SnapshotLinkChangeOutput>,
}

#[derive(Deserialize, Clone)]
struct RepositoryPathStateInput {
    relative_path: String,
    name: String,
    size_bytes: i64,
    last_write_unix_seconds: i64,
    content_hash_sha256: Option<String>,
}

#[derive(Serialize)]
struct RepositoryPathChangeOutput {
    relative_path: String,
    name: String,
    change_kind: String,
    current_size_bytes: i64,
    baseline_size_bytes: i64,
    current_last_write_unix_seconds: i64,
    baseline_last_write_unix_seconds: Option<i64>,
}

#[derive(Serialize)]
struct RepositoryPathComparisonOutput {
    added_count: u32,
    modified_count: u32,
    deleted_count: u32,
    changed_files_count: u32,
    changes: Vec<RepositoryPathChangeOutput>,
}

fn snapshot_change_rank(change_kind: &str) -> u8 {
    match change_kind {
        "added" => 0,
        "modified" => 1,
        "deleted" => 2,
        _ => 9,
    }
}

fn normalize_hash(value: Option<&str>) -> &str {
    value.unwrap_or("")
}

fn normalize_path_key(value: &str) -> String {
    value.to_ascii_lowercase()
}

fn resolve_snapshot_change_kind(
    current: &SnapshotLinkStateInput,
    previous: Option<&SnapshotLinkStateInput>,
) -> &'static str {
    if current.is_deletion_marker {
        return "deleted";
    }

    match previous {
        None => "added",
        Some(prev) if prev.is_deletion_marker => "added",
        Some(_) => "modified",
    }
}

pub fn compare_snapshot_links_json(
    current_json: &str,
    previous_json: &str,
) -> Result<Vec<u8>, String> {
    let current_states: Vec<SnapshotLinkStateInput> = serde_json::from_str(current_json)
        .map_err(|e| format!("parse current snapshot links json: {e}"))?;

    let previous_states: Vec<SnapshotLinkStateInput> = serde_json::from_str(previous_json)
        .map_err(|e| format!("parse previous snapshot links json: {e}"))?;

    let mut previous_by_identity: HashMap<i64, &SnapshotLinkStateInput> =
        HashMap::with_capacity(previous_states.len());
    for state in &previous_states {
        previous_by_identity
            .entry(state.file_identity_id)
            .or_insert(state);
    }

    let mut changes = Vec::new();
    for current in current_states {
        let previous = previous_by_identity.get(&current.file_identity_id).copied();

        if matches!(previous, Some(prev) if prev.file_version_id == current.file_version_id) {
            continue;
        }

        let change_kind = resolve_snapshot_change_kind(&current, previous).to_string();
        let previous_size_bytes = previous.map_or(0, |p| p.size_bytes);

        changes.push(SnapshotLinkChangeOutput {
            file_identity_id: current.file_identity_id,
            file_version_id: current.file_version_id,
            relative_path: current.relative_path,
            name: current.name,
            change_kind,
            current_size_bytes: current.size_bytes,
            previous_size_bytes,
            version_created_unix_seconds: current.version_created_unix_seconds,
        });
    }

    changes.sort_by(|left, right| {
        snapshot_change_rank(&left.change_kind)
            .cmp(&snapshot_change_rank(&right.change_kind))
            .then_with(|| {
                left.relative_path
                    .to_ascii_lowercase()
                    .cmp(&right.relative_path.to_ascii_lowercase())
            })
    });

    let payload = SnapshotLinkComparisonOutput {
        changed_files_count: changes.len() as u32,
        changes,
    };

    serde_json::to_vec(&payload).map_err(|e| format!("serialize snapshot link comparison: {e}"))
}

pub fn compare_repository_paths_json(
    current_json: &str,
    baseline_json: &str,
    take: usize,
) -> Result<Vec<u8>, String> {
    let current_states: Vec<RepositoryPathStateInput> = serde_json::from_str(current_json)
        .map_err(|e| format!("parse current repository paths json: {e}"))?;

    let baseline_states: Vec<RepositoryPathStateInput> = serde_json::from_str(baseline_json)
        .map_err(|e| format!("parse baseline repository paths json: {e}"))?;

    let mut current_by_path = HashMap::with_capacity(current_states.len());
    for state in &current_states {
        current_by_path.insert(normalize_path_key(&state.relative_path), state);
    }

    let mut baseline_by_path = HashMap::with_capacity(baseline_states.len());
    for state in &baseline_states {
        baseline_by_path.insert(normalize_path_key(&state.relative_path), state);
    }

    let mut added_count = 0u32;
    let mut modified_count = 0u32;
    let mut deleted_count = 0u32;

    let mut changes = Vec::new();

    for current in &current_states {
        let key = normalize_path_key(&current.relative_path);
        let previous = baseline_by_path.get(&key).copied();

        match previous {
            None => {
                added_count += 1;
                changes.push(RepositoryPathChangeOutput {
                    relative_path: current.relative_path.clone(),
                    name: current.name.clone(),
                    change_kind: "added".to_string(),
                    current_size_bytes: current.size_bytes,
                    baseline_size_bytes: 0,
                    current_last_write_unix_seconds: current.last_write_unix_seconds,
                    baseline_last_write_unix_seconds: None,
                });
            }
            Some(prev) => {
                let current_hash = normalize_hash(current.content_hash_sha256.as_deref());
                let prev_hash = normalize_hash(prev.content_hash_sha256.as_deref());

                if current.size_bytes == prev.size_bytes
                    && current_hash.eq_ignore_ascii_case(prev_hash)
                {
                    continue;
                }

                modified_count += 1;
                changes.push(RepositoryPathChangeOutput {
                    relative_path: current.relative_path.clone(),
                    name: current.name.clone(),
                    change_kind: "modified".to_string(),
                    current_size_bytes: current.size_bytes,
                    baseline_size_bytes: prev.size_bytes,
                    current_last_write_unix_seconds: current.last_write_unix_seconds,
                    baseline_last_write_unix_seconds: Some(prev.last_write_unix_seconds),
                });
            }
        }
    }

    for previous in &baseline_states {
        let key = normalize_path_key(&previous.relative_path);
        if current_by_path.contains_key(&key) {
            continue;
        }

        deleted_count += 1;
        changes.push(RepositoryPathChangeOutput {
            relative_path: previous.relative_path.clone(),
            name: previous.name.clone(),
            change_kind: "deleted".to_string(),
            current_size_bytes: 0,
            baseline_size_bytes: previous.size_bytes,
            current_last_write_unix_seconds: previous.last_write_unix_seconds,
            baseline_last_write_unix_seconds: Some(previous.last_write_unix_seconds),
        });
    }

    changes.sort_by(|left, right| {
        snapshot_change_rank(&left.change_kind)
            .cmp(&snapshot_change_rank(&right.change_kind))
            .then_with(|| {
                left.relative_path
                    .to_ascii_lowercase()
                    .cmp(&right.relative_path.to_ascii_lowercase())
            })
    });

    let changed_files_count = (added_count + modified_count + deleted_count) as u32;
    let limit = take.max(1);
    let changes = if changes.len() > limit {
        changes.into_iter().take(limit).collect()
    } else {
        changes
    };

    let payload = RepositoryPathComparisonOutput {
        added_count,
        modified_count,
        deleted_count,
        changed_files_count,
        changes,
    };

    serde_json::to_vec(&payload).map_err(|e| format!("serialize repository path comparison: {e}"))
}

#[derive(Deserialize, Clone)]
struct RepositoryVersionPlanningInput {
    relative_path: String,
    has_current: bool,
    current_size_bytes: i64,
    current_content_hash_sha256: Option<String>,
    has_previous: bool,
    previous_size_bytes: i64,
    previous_content_hash_sha256: Option<String>,
    has_latest_version: bool,
    latest_is_deletion_marker: bool,
    latest_size_bytes: i64,
    latest_has_blocks: bool,
}

#[derive(Serialize)]
struct RepositoryVersionPlanOutput {
    relative_path: String,
    change_kind: String,
    should_create_new_version: bool,
    should_mark_identity_deleted: bool,
}

#[derive(Serialize)]
struct RepositoryVersionPlanningOutput {
    changed_files_count: u32,
    new_versions_count: u32,
    entries: Vec<RepositoryVersionPlanOutput>,
}

pub fn plan_repository_versions_json(states_json: &str) -> Result<Vec<u8>, String> {
    let states: Vec<RepositoryVersionPlanningInput> = serde_json::from_str(states_json)
        .map_err(|e| format!("parse repository version planning json: {e}"))?;

    let mut entries = Vec::with_capacity(states.len());

    for state in states {
        if state.has_current {
            let current_hash = normalize_hash(state.current_content_hash_sha256.as_deref());
            let previous_hash = normalize_hash(state.previous_content_hash_sha256.as_deref());

            let changed = !state.has_previous
                || state.current_size_bytes != state.previous_size_bytes
                || !current_hash.eq_ignore_ascii_case(previous_hash);

            let change_kind = if !state.has_previous {
                "added"
            } else if changed {
                "modified"
            } else {
                "unchanged"
            }
            .to_string();

            let should_create_new_version = changed
                || !state.has_latest_version
                || state.latest_is_deletion_marker
                || !state.latest_has_blocks
                || (state.has_latest_version
                    && state.latest_size_bytes == 0
                    && state.current_size_bytes > 0
                    && !state.latest_has_blocks);

            entries.push(RepositoryVersionPlanOutput {
                relative_path: state.relative_path,
                change_kind,
                should_create_new_version,
                should_mark_identity_deleted: false,
            });

            continue;
        }

        let change_kind = if state.has_previous || state.has_latest_version {
            "deleted"
        } else {
            "unchanged"
        }
        .to_string();

        let should_create_new_version =
            !state.has_latest_version || !state.latest_is_deletion_marker;

        entries.push(RepositoryVersionPlanOutput {
            relative_path: state.relative_path,
            change_kind,
            should_create_new_version,
            should_mark_identity_deleted: true,
        });
    }

    entries.sort_by(|left, right| {
        left.relative_path
            .to_ascii_lowercase()
            .cmp(&right.relative_path.to_ascii_lowercase())
    });

    let changed_files_count = entries
        .iter()
        .filter(|x| !x.change_kind.eq_ignore_ascii_case("unchanged"))
        .count() as u32;

    let new_versions_count = entries
        .iter()
        .filter(|x| x.should_create_new_version)
        .count() as u32;

    let payload = RepositoryVersionPlanningOutput {
        changed_files_count,
        new_versions_count,
        entries,
    };

    serde_json::to_vec(&payload).map_err(|e| format!("serialize repository version planning: {e}"))
}
#[cfg(test)]
mod tests {
    use serde_json::Value;

    use super::*;

    #[test]
    fn compare_snapshot_links_marks_modified() {
        let current_json = r#"[{"file_identity_id":1,"file_version_id":2,"is_deletion_marker":false,"size_bytes":10,"version_created_unix_seconds":100,"relative_path":"a.txt","name":"a.txt"}]"#;
        let previous_json = r#"[{"file_identity_id":1,"file_version_id":1,"is_deletion_marker":false,"size_bytes":9,"version_created_unix_seconds":90,"relative_path":"a.txt","name":"a.txt"}]"#;

        let payload =
            compare_snapshot_links_json(current_json, previous_json).expect("comparison payload");
        let parsed: Value = serde_json::from_slice(&payload).expect("valid json");

        assert_eq!(parsed["changed_files_count"].as_u64(), Some(1));
        assert_eq!(
            parsed["changes"][0]["change_kind"].as_str(),
            Some("modified")
        );
        assert_eq!(
            parsed["changes"][0]["previous_size_bytes"].as_i64(),
            Some(9)
        );
    }

    #[test]
    fn compare_repository_paths_counts_changes() {
        let current_json = r#"[
            {"relative_path":"a.txt","name":"a.txt","size_bytes":12,"last_write_unix_seconds":200,"content_hash_sha256":"abc"},
            {"relative_path":"new.txt","name":"new.txt","size_bytes":1,"last_write_unix_seconds":210,"content_hash_sha256":"zzz"}
        ]"#;
        let baseline_json = r#"[
            {"relative_path":"a.txt","name":"a.txt","size_bytes":10,"last_write_unix_seconds":100,"content_hash_sha256":"def"},
            {"relative_path":"gone.txt","name":"gone.txt","size_bytes":4,"last_write_unix_seconds":90,"content_hash_sha256":"ggg"}
        ]"#;

        let payload = compare_repository_paths_json(current_json, baseline_json, 50)
            .expect("comparison payload");
        let parsed: Value = serde_json::from_slice(&payload).expect("valid json");

        assert_eq!(parsed["added_count"].as_u64(), Some(1));
        assert_eq!(parsed["modified_count"].as_u64(), Some(1));
        assert_eq!(parsed["deleted_count"].as_u64(), Some(1));
        assert_eq!(parsed["changed_files_count"].as_u64(), Some(3));
    }
}
