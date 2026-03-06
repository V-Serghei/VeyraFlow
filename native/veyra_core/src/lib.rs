use std::cell::RefCell;
use std::collections::HashSet;
use std::ffi::{CStr, CString};
use std::fs::{self, File};
use std::io::{Read, Write};
use std::os::raw::{c_char, c_int};
use std::path::{Path, PathBuf};
use std::ptr;
use std::slice;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

thread_local! {
    static LAST_ERROR: RefCell<Vec<u8>> = RefCell::new(Vec::new());
}

fn clear_last_error() {
    LAST_ERROR.with(|c| c.borrow_mut().clear());
}

fn set_last_error(msg: &str) {
    LAST_ERROR.with(|c| *c.borrow_mut() = msg.as_bytes().to_vec());
}

fn set_error_from<E: std::fmt::Display>(prefix: &str, err: E) {
    set_last_error(&format!("{prefix}: {err}"));
}

fn get_last_error() -> Vec<u8> {
    LAST_ERROR.with(|c| c.borrow().clone())
}

fn write_bytes(out: *mut u8, out_len: u64, data: &[u8], written: *mut u64) -> i32 {
    if written.is_null() {
        set_last_error("written is null");
        return -1;
    }

    unsafe {
        *written = data.len() as u64;
    }

    if out.is_null() || out_len == 0 {
        return 0;
    }

    if out_len < data.len() as u64 {
        set_last_error("output buffer too small");
        return -1;
    }

    unsafe {
        ptr::copy_nonoverlapping(data.as_ptr(), out, data.len());
    }

    0
}

fn copy_bytes_to_out(data: &[u8], out: *mut u8, out_len: c_int) -> c_int {
    if out.is_null() || out_len < 0 {
        return -1;
    }

    let to_copy = data.len().min(out_len as usize);
    unsafe {
        ptr::copy_nonoverlapping(data.as_ptr(), out, to_copy);
    }

    to_copy as c_int
}

fn ptr_to_str<'a>(ptr: *const c_char) -> Option<&'a str> {
    if ptr.is_null() {
        return None;
    }

    unsafe { CStr::from_ptr(ptr) }.to_str().ok()
}

fn normalize_extension(raw: &str) -> String {
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

fn parse_extension_filters(ptr: *const c_char) -> HashSet<String> {
    let Some(raw) = ptr_to_str(ptr) else {
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

fn bytes_to_hex(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        out.push_str(&format!("{b:02x}"));
    }
    out
}
fn blake3_file_hex(path: &Path) -> Result<String, String> {
    let mut file = File::open(path).map_err(|e| format!("open {}: {e}", path.display()))?;
    let mut hasher = blake3::Hasher::new();
    let mut buf = [0u8; 65536];
    loop {
        let n = file.read(&mut buf).map_err(|e| format!("read: {e}"))?;
        if n == 0 { break; }
        hasher.update(&buf[..n]);
    }
    Ok(hasher.finalize().to_hex().to_string())
}
fn sha256_file_hex(path: &Path) -> Result<String, String> {
    sha256_file_hex_with_limit(path, 0)
}

fn sha256_file_hex_with_limit(path: &Path, max_read_bytes_per_sec: u64) -> Result<String, String> {
    let mut file = File::open(path).map_err(|e| format!("open {}: {e}", path.display()))?;
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 1024 * 1024];

    let throttled = max_read_bytes_per_sec > 0;
    let start = Instant::now();
    let mut total_read = 0_u64;

    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|e| format!("read {}: {e}", path.display()))?;

        if read == 0 {
            break;
        }

        hasher.update(&buffer[..read]);

        if throttled {
            total_read += read as u64;

            let expected = Duration::from_secs_f64(total_read as f64 / max_read_bytes_per_sec as f64);
            let elapsed = start.elapsed();
            if expected > elapsed {
                std::thread::sleep(expected - elapsed);
            }
        }
    }

    let digest = hasher.finalize();
    Ok(bytes_to_hex(&digest))
}

#[derive(Serialize)]
struct StoredBlockRef {
    sequence: u32,
    block_hash_blake3: String,
    length_bytes: u32,
    stored_size_bytes: u64,
}

#[derive(Serialize)]
struct StoreFileBlocksResult {
    file_size_bytes: u64,
    stored_size_bytes: u64,
    block_count: u32,
    deduped_blocks: u32,
    new_blocks: u32,
    blocks: Vec<StoredBlockRef>,
}

#[derive(Deserialize)]
struct RestoreBlockRef {
    block_hash_blake3: String,
    length_bytes: u32,
}

fn block_path_for_hash(store_root: &Path, hash: &str) -> PathBuf {
    let p1 = hash.get(0..2).unwrap_or("00");
    let p2 = hash.get(2..4).unwrap_or("00");

    store_root
        .join("blocks")
        .join(p1)
        .join(p2)
        .join(format!("{hash}.zst"))
}

fn persist_block(store_root: &Path, hash: &str, bytes: &[u8]) -> Result<(u64, bool), String> {
    let block_path = block_path_for_hash(store_root, hash);

    if block_path.exists() {
        let sz = fs::metadata(&block_path)
            .map_err(|e| format!("metadata {}: {e}", block_path.display()))?
            .len();
        return Ok((sz, false));
    }

    let parent = block_path
        .parent()
        .ok_or_else(|| format!("no parent for {}", block_path.display()))?;

    fs::create_dir_all(parent)
        .map_err(|e| format!("create_dir_all {}: {e}", parent.display()))?;

    if block_path.exists() {
        let sz = fs::metadata(&block_path)
            .map_err(|e| format!("metadata {}: {e}", block_path.display()))?
            .len();
        return Ok((sz, false));
    }

    let compressed = zstd::encode_all(bytes, 3)
        .map_err(|e| format!("compress block {hash}: {e}"))?;

    let temp_path = block_path.with_extension(format!("{}.tmp", std::process::id()));
    if temp_path.exists() {
        let _ = fs::remove_file(&temp_path);
    }

    {
        let mut out = fs::OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&temp_path)
            .map_err(|e| format!("create temp block {}: {e}", temp_path.display()))?;

        out.write_all(&compressed)
            .map_err(|e| format!("write temp block {}: {e}", temp_path.display()))?;
    }

    if let Err(e) = fs::rename(&temp_path, &block_path) {
        if block_path.exists() {
            let _ = fs::remove_file(&temp_path);
        } else {
            return Err(format!(
                "rename temp block {} -> {}: {e}",
                temp_path.display(),
                block_path.display()
            ));
        }
    }

    let stored_size = fs::metadata(&block_path)
        .map_err(|e| format!("metadata {}: {e}", block_path.display()))?
        .len();

    Ok((stored_size, true))
}

fn store_file_blocks(file_path: &Path, store_root: &Path, chunk_size: usize) -> Result<Vec<u8>, String> {
    if !file_path.exists() {
        return Err(format!("file not found: {}", file_path.display()));
    }

    if !file_path.is_file() {
        return Err(format!("path is not a file: {}", file_path.display()));
    }

    if chunk_size == 0 {
        return Err("chunk size must be positive".to_string());
    }

    let mut file = File::open(file_path)
        .map_err(|e| format!("open {}: {e}", file_path.display()))?;

    let mut buffer = vec![0_u8; chunk_size];
    let mut blocks = Vec::new();

    let mut sequence = 0_u32;
    let mut total_file_size = 0_u64;
    let mut total_stored_size = 0_u64;
    let mut deduped_blocks = 0_u32;
    let mut new_blocks = 0_u32;

    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|e| format!("read {}: {e}", file_path.display()))?;

        if read == 0 {
            break;
        }

        let chunk = &buffer[..read];
        let hash = blake3::hash(chunk).to_hex().to_string();
        let (stored_size, is_new) = persist_block(store_root, &hash, chunk)?;

        total_file_size += read as u64;
        total_stored_size += stored_size;

        if is_new {
            new_blocks += 1;
        } else {
            deduped_blocks += 1;
        }

        blocks.push(StoredBlockRef {
            sequence,
            block_hash_blake3: hash,
            length_bytes: read as u32,
            stored_size_bytes: stored_size,
        });

        sequence += 1;
    }

    let payload = StoreFileBlocksResult {
        file_size_bytes: total_file_size,
        stored_size_bytes: total_stored_size,
        block_count: blocks.len() as u32,
        deduped_blocks,
        new_blocks,
        blocks,
    };

    serde_json::to_vec(&payload).map_err(|e| format!("serialize block-store result: {e}"))
}

fn restore_file_from_blocks(
    store_root: &Path,
    blocks_json: &str,
    target_path: &Path,
    overwrite_existing: bool,
) -> Result<i64, String> {
    let blocks: Vec<RestoreBlockRef> = serde_json::from_str(blocks_json)
        .map_err(|e| format!("parse blocks json: {e}"))?;

    if blocks.is_empty() {
        return Err("blocks list is empty".to_string());
    }

    if target_path.exists() && !overwrite_existing {
        return Err(format!("target already exists: {}", target_path.display()));
    }

    if let Some(parent) = target_path.parent() {
        fs::create_dir_all(parent)
            .map_err(|e| format!("create target directory {}: {e}", parent.display()))?;
    }

    let mut options = fs::OpenOptions::new();
    options.write(true).create(true);
    if overwrite_existing {
        options.truncate(true);
    } else {
        options.create_new(true);
    }

    let mut out = options
        .open(target_path)
        .map_err(|e| format!("open target {}: {e}", target_path.display()))?;

    let mut written = 0_i64;

    for block in blocks {
        let block_path = block_path_for_hash(store_root, &block.block_hash_blake3);

        let compressed = fs::read(&block_path)
            .map_err(|e| format!("read block {}: {e}", block_path.display()))?;

        let decompressed = zstd::decode_all(compressed.as_slice())
            .map_err(|e| format!("decompress block {}: {e}", block_path.display()))?;

        let len = block.length_bytes as usize;
        if decompressed.len() < len {
            return Err(format!(
                "block {} is shorter than expected {} < {}",
                block.block_hash_blake3,
                decompressed.len(),
                len
            ));
        }

        out.write_all(&decompressed[..len])
            .map_err(|e| format!("write target {}: {e}", target_path.display()))?;

        written += len as i64;
    }

    Ok(written)
}
#[derive(Serialize)]
struct ScanEntry {
    relative_path: String,
    parent_relative_path: Option<String>,
    name: String,
    is_directory: bool,
    extension: Option<String>,
    size_bytes: u64,
    last_write_unix_seconds: i64,
    content_hash_sha256: Option<String>,
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
    let read_dir = fs::read_dir(current)
        .map_err(|e| format!("read_dir {}: {e}", current.display()))?;

    for item in read_dir {
        let Ok(entry) = item else {
            continue;
        };

        let path = entry.path();
        let Ok(meta) = entry.metadata() else {
            continue;
        };

        let Some(relative) = relative_path(root, &path) else {
            continue;
        };

        if relative.is_empty() {
            continue;
        }

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

fn run_directory_scan(
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
#[no_mangle]
pub extern "C" fn veyra_last_error_utf8(out: *mut u8, out_len: u64, written: *mut u64) -> i32 {
    let data = get_last_error();
    write_bytes(out, out_len, &data, written)
}

#[no_mangle]
pub extern "C" fn veyra_blake3_hash(
    data: *const u8,
    data_len: c_int,
    output: *mut u8,
    output_len: c_int,
) -> c_int {
    clear_last_error();

    if data.is_null() || output.is_null() || data_len < 0 || output_len < 0 {
        set_last_error("invalid argument for veyra_blake3_hash");
        return -1;
    }

    let input = unsafe { slice::from_raw_parts(data, data_len as usize) };
    let hash = blake3::hash(input);
    let hex = hash.to_hex();

    copy_bytes_to_out(hex.as_bytes(), output, output_len)
}

#[no_mangle]
pub extern "C" fn veyra_blake3_hash_file(
    path_ptr: *const c_char,
    output: *mut u8,
    output_len: c_int,
) -> c_int {
    clear_last_error();

    if output.is_null() || output_len < 0 {
        set_last_error("invalid output buffer for veyra_blake3_hash_file");
        return -1;
    }

    let Some(path_str) = ptr_to_str(path_ptr) else {
        set_last_error("path is null or invalid utf-8");
        return -1;
    };

    let path = Path::new(path_str);
    let hex = match blake3_file_hex(path) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    copy_bytes_to_out(hex.as_bytes(), output, output_len)
}

#[no_mangle]
pub extern "C" fn veyra_sha256_hash_file_utf8(
    path_ptr: *const c_char,
    output: *mut u8,
    output_len: c_int,
) -> c_int {
    clear_last_error();

    if output.is_null() || output_len < 0 {
        set_last_error("invalid output buffer for veyra_sha256_hash_file_utf8");
        return -1;
    }

    let Some(path_str) = ptr_to_str(path_ptr) else {
        set_last_error("path is null or invalid utf-8");
        return -1;
    };

    let path = Path::new(path_str);
    let hex = match sha256_file_hex(path) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    copy_bytes_to_out(hex.as_bytes(), output, output_len)
}

#[no_mangle]
pub extern "C" fn veyra_scan_directory_utf8(
    root_path_ptr: *const c_char,
    extensions_csv_ptr: *const c_char,
    out: *mut u8,
    out_len: u64,
    written: *mut u64,
) -> i32 {
    clear_last_error();

    let Some(root_path_str) = ptr_to_str(root_path_ptr) else {
        set_last_error("root path is null or invalid utf-8");
        return -1;
    };

    let filters = parse_extension_filters(extensions_csv_ptr);
    let root = Path::new(root_path_str);

    let data = match run_directory_scan(root, &filters, 0, 0) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    write_bytes(out, out_len, &data, written)
}

#[no_mangle]
pub extern "C" fn veyra_scan_directory_limited_utf8(
    root_path_ptr: *const c_char,
    extensions_csv_ptr: *const c_char,
    max_read_bytes_per_sec: u64,
    out: *mut u8,
    out_len: u64,
    written: *mut u64,
) -> i32 {
    clear_last_error();

    let Some(root_path_str) = ptr_to_str(root_path_ptr) else {
        set_last_error("root path is null or invalid utf-8");
        return -1;
    };

    let filters = parse_extension_filters(extensions_csv_ptr);
    let root = Path::new(root_path_str);

    let data = match run_directory_scan(root, &filters, max_read_bytes_per_sec, 0) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    write_bytes(out, out_len, &data, written)
}

#[no_mangle]
pub extern "C" fn veyra_scan_directory_limited_v2_utf8(
    root_path_ptr: *const c_char,
    extensions_csv_ptr: *const c_char,
    max_read_bytes_per_sec: u64,
    max_file_ops_per_sec: u64,
    out: *mut u8,
    out_len: u64,
    written: *mut u64,
) -> i32 {
    clear_last_error();

    let Some(root_path_str) = ptr_to_str(root_path_ptr) else {
        set_last_error("root path is null or invalid utf-8");
        return -1;
    };

    let filters = parse_extension_filters(extensions_csv_ptr);
    let root = Path::new(root_path_str);

    let data = match run_directory_scan(root, &filters, max_read_bytes_per_sec, max_file_ops_per_sec) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    write_bytes(out, out_len, &data, written)
}

#[no_mangle]
pub extern "C" fn veyra_store_file_blocks_utf8(
    file_path_ptr: *const c_char,
    store_root_ptr: *const c_char,
    chunk_size: u32,
    out: *mut u8,
    out_len: u64,
    written: *mut u64,
) -> i32 {
    clear_last_error();

    let Some(file_path_str) = ptr_to_str(file_path_ptr) else {
        set_last_error("file path is null or invalid utf-8");
        return -1;
    };

    let Some(store_root_str) = ptr_to_str(store_root_ptr) else {
        set_last_error("store root is null or invalid utf-8");
        return -1;
    };

    let effective_chunk = if chunk_size == 0 { 65536 } else { chunk_size as usize }.clamp(4096, 4 * 1024 * 1024);

    let file_path = Path::new(file_path_str);
    let store_root = Path::new(store_root_str);

    let data = match store_file_blocks(file_path, store_root, effective_chunk) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    write_bytes(out, out_len, &data, written)
}

#[no_mangle]
pub extern "C" fn veyra_restore_file_blocks_utf8(
    store_root_ptr: *const c_char,
    blocks_json_ptr: *const c_char,
    target_path_ptr: *const c_char,
    overwrite_existing: c_int,
) -> i64 {
    clear_last_error();

    let Some(store_root_str) = ptr_to_str(store_root_ptr) else {
        set_last_error("store root is null or invalid utf-8");
        return -1;
    };

    let Some(blocks_json_str) = ptr_to_str(blocks_json_ptr) else {
        set_last_error("blocks json is null or invalid utf-8");
        return -1;
    };

    let Some(target_path_str) = ptr_to_str(target_path_ptr) else {
        set_last_error("target path is null or invalid utf-8");
        return -1;
    };

    let store_root = Path::new(store_root_str);
    let target_path = Path::new(target_path_str);

    match restore_file_from_blocks(store_root, blocks_json_str, target_path, overwrite_existing != 0) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            -1
        }
    }
}

#[no_mangle]
pub extern "C" fn veyra_zstd_compress(
    data: *const u8,
    data_len: c_int,
    output: *mut u8,
    output_len: c_int,
    level: c_int,
) -> c_int {
    clear_last_error();

    if data.is_null() || output.is_null() || data_len < 0 || output_len < 0 {
        set_last_error("invalid argument for veyra_zstd_compress");
        return -1;
    }

    let input = unsafe { slice::from_raw_parts(data, data_len as usize) };

    let compressed = match zstd::encode_all(input, level) {
        Ok(v) => v,
        Err(e) => {
            set_error_from("zstd compress", e);
            return -1;
        }
    };

    if compressed.len() > output_len as usize {
        set_last_error("output buffer too small");
        return -1;
    }

    unsafe {
        ptr::copy_nonoverlapping(compressed.as_ptr(), output, compressed.len());
    }

    compressed.len() as c_int
}

#[no_mangle]
pub extern "C" fn veyra_zstd_decompress(
    data: *const u8,
    data_len: c_int,
    output: *mut u8,
    output_len: c_int,
) -> c_int {
    clear_last_error();

    if data.is_null() || output.is_null() || data_len < 0 || output_len < 0 {
        set_last_error("invalid argument for veyra_zstd_decompress");
        return -1;
    }

    let input = unsafe { slice::from_raw_parts(data, data_len as usize) };

    let decompressed = match zstd::decode_all(input) {
        Ok(v) => v,
        Err(e) => {
            set_error_from("zstd decompress", e);
            return -1;
        }
    };

    if decompressed.len() > output_len as usize {
        set_last_error("output buffer too small");
        return -1;
    }

    unsafe {
        ptr::copy_nonoverlapping(decompressed.as_ptr(), output, decompressed.len());
    }

    decompressed.len() as c_int
}

#[no_mangle]
pub extern "C" fn veyra_zstd_compress_file(
    src_path_ptr: *const c_char,
    dst_path_ptr: *const c_char,
    level: c_int,
) -> i64 {
    clear_last_error();

    let Some(src_path) = ptr_to_str(src_path_ptr) else {
        set_last_error("source path is null or invalid utf-8");
        return -1;
    };

    let Some(dst_path) = ptr_to_str(dst_path_ptr) else {
        set_last_error("destination path is null or invalid utf-8");
        return -1;
    };

    let input = match std::fs::read(src_path) {
        Ok(v) => v,
        Err(e) => {
            set_error_from("read source file", e);
            return -1;
        }
    };

    let compressed = match zstd::encode_all(input.as_slice(), level) {
        Ok(v) => v,
        Err(e) => {
            set_error_from("zstd compress", e);
            return -1;
        }
    };

    let mut out_file = match File::create(dst_path) {
        Ok(v) => v,
        Err(e) => {
            set_error_from("create destination file", e);
            return -1;
        }
    };

    match out_file.write_all(&compressed) {
        Ok(_) => compressed.len() as i64,
        Err(e) => {
            set_error_from("write destination file", e);
            -1
        }
    }
}

#[no_mangle]
pub extern "C" fn veyra_zstd_decompress_file(
    src_path_ptr: *const c_char,
    dst_path_ptr: *const c_char,
) -> i64 {
    clear_last_error();

    let Some(src_path) = ptr_to_str(src_path_ptr) else {
        set_last_error("source path is null or invalid utf-8");
        return -1;
    };

    let Some(dst_path) = ptr_to_str(dst_path_ptr) else {
        set_last_error("destination path is null or invalid utf-8");
        return -1;
    };

    let input = match std::fs::read(src_path) {
        Ok(v) => v,
        Err(e) => {
            set_error_from("read source file", e);
            return -1;
        }
    };

    let decompressed = match zstd::decode_all(input.as_slice()) {
        Ok(v) => v,
        Err(e) => {
            set_error_from("zstd decompress", e);
            return -1;
        }
    };

    let mut out_file = match File::create(dst_path) {
        Ok(v) => v,
        Err(e) => {
            set_error_from("create destination file", e);
            return -1;
        }
    };

    match out_file.write_all(&decompressed) {
        Ok(_) => decompressed.len() as i64,
        Err(e) => {
            set_error_from("write destination file", e);
            -1
        }
    }
}

#[no_mangle]
pub extern "C" fn veyra_get_version() -> *mut c_char {
    clear_last_error();

    let version = format!("veyra_core {}", env!("CARGO_PKG_VERSION"));
    match CString::new(version) {
        Ok(s) => s.into_raw(),
        Err(e) => {
            set_error_from("create version string", e);
            ptr::null_mut()
        }
    }
}

#[no_mangle]
pub extern "C" fn veyra_free_string(s: *mut c_char) {
    if !s.is_null() {
        unsafe {
            let _ = CString::from_raw(s);
        }
    }
}

