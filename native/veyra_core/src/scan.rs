use std::collections::HashSet;
use std::fs;
use std::path::Path;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::Serialize;

use crate::hash::sha256_file_hex_with_limit;

#[derive(Serialize)]
pub struct ScanEntry {
    pub relative_path: String,
    pub parent_relative_path: Option<String>,
    pub name: String,
    pub is_directory: bool,
    pub extension: Option<String>,
    pub size_bytes: u64,
    pub last_write_unix_seconds: i64,
    pub content_hash_sha256: Option<String>,
}

pub fn normalize_extension(raw: &str) -> String {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return String::new();
    }

    let lower = trimmed.to_lowercase();
    if lower.starts_with('.') {
        lower
    } else {
        format!(".{lower}")
    }
}

pub fn parse_extension_filters(raw: Option<&str>) -> HashSet<String> {
    let Some(raw) = raw else {
        return HashSet::new();
    };

    raw.split(',')
        .map(normalize_extension)
        .filter(|v| !v.is_empty())
        .collect()
}

fn file_extension(path: &Path) -> Option<String> {
    let ext = path.extension()?.to_string_lossy();
    let normalized = normalize_extension(&ext);
    if normalized.is_empty() {
        None
    } else {
        Some(normalized)
    }
}

fn file_name(path: &Path) -> String {
    path.file_name()
        .map(|v| v.to_string_lossy().into_owned())
        .unwrap_or_default()
}

fn relative_path(root: &Path, path: &Path) -> Option<String> {
    let rel = path.strip_prefix(root).ok()?;
    Some(rel.to_string_lossy().replace('\\', "/"))
}

fn parent_relative_path(relative: &str) -> Option<String> {
    let (parent, _) = relative.rsplit_once('/')?;
    if parent.is_empty() {
        None
    } else {
        Some(parent.to_string())
    }
}

fn system_time_to_unix_seconds(value: SystemTime) -> i64 {
    value
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

fn metadata_last_write_unix_seconds(meta: &fs::Metadata) -> i64 {
    meta.modified()
        .map(system_time_to_unix_seconds)
        .unwrap_or(0)
}

struct IopsState {
    started_at: Instant,
    operations: u64,
}

fn maybe_throttle_iops(max_file_ops_per_sec: u64, state: &mut IopsState) {
    if max_file_ops_per_sec == 0 {
        return;
    }

    state.operations += 1;

    let expected = Duration::from_secs_f64(state.operations as f64 / max_file_ops_per_sec as f64);
    let elapsed = state.started_at.elapsed();

    if expected > elapsed {
        std::thread::sleep(expected - elapsed);
    }
}

fn collect_entries(
    root: &Path,
    current: &Path,
    extension_filters: &HashSet<String>,
    max_read_bytes_per_sec: u64,
    max_file_ops_per_sec: u64,
    iops_state: &mut IopsState,
    entries: &mut Vec<ScanEntry>,
) -> Result<(), String> {
    let read_dir = fs::read_dir(current).map_err(|e| format!("read_dir {}: {e}", current.display()))?;

    for item in read_dir {
        let Ok(entry) = item else {
            continue;
        };

        let path = entry.path();
        let Some(relative) = relative_path(root, &path) else {
            continue;
        };

        let Ok(meta) = entry.metadata() else {
            continue;
        };

        if meta.is_dir() {
            entries.push(ScanEntry {
                relative_path: relative.clone(),
                parent_relative_path: parent_relative_path(&relative),
                name: file_name(&path),
                is_directory: true,
                extension: None,
                size_bytes: 0,
                last_write_unix_seconds: metadata_last_write_unix_seconds(&meta),
                content_hash_sha256: None,
            });

            collect_entries(
                root,
                &path,
                extension_filters,
                max_read_bytes_per_sec,
                max_file_ops_per_sec,
                iops_state,
                entries,
            )?;

            continue;
        }

        if !meta.is_file() {
            continue;
        }

        let extension = file_extension(&path);
        if !extension_filters.is_empty() {
            let Some(ext) = extension.as_ref() else {
                continue;
            };

            if !extension_filters.contains(ext) {
                continue;
            }
        }

        maybe_throttle_iops(max_file_ops_per_sec, iops_state);

        let hash = sha256_file_hex_with_limit(&path, max_read_bytes_per_sec)?;

        entries.push(ScanEntry {
            relative_path: relative.clone(),
            parent_relative_path: parent_relative_path(&relative),
            name: file_name(&path),
            is_directory: false,
            extension,
            size_bytes: meta.len(),
            last_write_unix_seconds: metadata_last_write_unix_seconds(&meta),
            content_hash_sha256: Some(hash),
        });
    }

    Ok(())
}

pub fn run_directory_scan(
    root_path: &Path,
    extension_filters: &HashSet<String>,
    max_read_bytes_per_sec: u64,
    max_file_ops_per_sec: u64,
) -> Result<Vec<u8>, String> {
    if !root_path.exists() {
        return Err(format!("path not found: {}", root_path.display()));
    }

    if !root_path.is_dir() {
        return Err(format!("path is not a directory: {}", root_path.display()));
    }

    let mut entries = Vec::new();
    let mut iops_state = IopsState {
        started_at: Instant::now(),
        operations: 0,
    };

    collect_entries(
        root_path,
        root_path,
        extension_filters,
        max_read_bytes_per_sec,
        max_file_ops_per_sec,
        &mut iops_state,
        &mut entries,
    )?;

    entries.sort_by(|a, b| a.relative_path.cmp(&b.relative_path));

    serde_json::to_vec(&entries).map_err(|e| format!("serialize scan result: {e}"))
}
