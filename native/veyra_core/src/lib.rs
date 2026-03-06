use std::cell::RefCell;
use std::collections::HashSet;
use std::ffi::{CStr, CString};
use std::fs::{self, File};
use std::io::{Read, Write};
use std::os::raw::{c_char, c_int};
use std::path::Path;
use std::ptr;
use std::slice;
use std::time::{SystemTime, UNIX_EPOCH};

use serde::Serialize;
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
    let mut file = File::open(path).map_err(|e| format!("open {}: {e}", path.display()))?;
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 1024 * 1024];

    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|e| format!("read {}: {e}", path.display()))?;

        if read == 0 {
            break;
        }

        hasher.update(&buffer[..read]);
    }

    let digest = hasher.finalize();
    Ok(bytes_to_hex(&digest))
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

fn collect_entries(
    root: &Path,
    current: &Path,
    extension_filters: &HashSet<String>,
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

            collect_entries(root, &path, extension_filters, entries)?;
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


        let hash = blake3_file_hex(&path)?;
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

fn run_directory_scan(root_path: &Path, extension_filters: &HashSet<String>) -> Result<Vec<u8>, String> {
    if !root_path.exists() {
        return Err(format!("path not found: {}", root_path.display()));
    }

    if !root_path.is_dir() {
        return Err(format!("path is not a directory: {}", root_path.display()));
    }

    let mut entries = Vec::new();
    collect_entries(root_path, root_path, extension_filters, &mut entries)?;
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
    let mut file = match File::open(path) {
        Ok(v) => v,
        Err(e) => {
            set_error_from("open file", e);
            return -1;
        }
    };

    let mut hasher = blake3::Hasher::new();
    let mut buf = [0_u8; 64 * 1024];

    loop {
        let read = match file.read(&mut buf) {
            Ok(v) => v,
            Err(e) => {
                set_error_from("read file", e);
                return -1;
            }
        };

        if read == 0 {
            break;
        }

        hasher.update(&buf[..read]);
    }

    let hex = hasher.finalize().to_hex();
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

    let data = match run_directory_scan(root, &filters) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    write_bytes(out, out_len, &data, written)
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
