use std::ffi::CString;
use std::os::raw::{c_char, c_int};
use std::path::Path;
use std::ptr;
use std::slice;

use super::{
    clear_last_error, copy_bytes_to_out, get_last_error, ptr_to_str, set_error_from,
    set_last_error, write_bytes,
};
use crate::{block_store, crypto, hash, scan, snapshot_compare, text_diff};
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
    let hex = match hash::blake3_file_hex(path) {
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
    let hex = match hash::sha256_file_hex(path) {
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

    let filters = scan::parse_extension_filters(ptr_to_str(extensions_csv_ptr));
    let root = Path::new(root_path_str);

    let data = match scan::run_directory_scan(root, &filters, 0, 0) {
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

    let filters = scan::parse_extension_filters(ptr_to_str(extensions_csv_ptr));
    let root = Path::new(root_path_str);

    let data = match scan::run_directory_scan(root, &filters, max_read_bytes_per_sec, 0) {
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

    let filters = scan::parse_extension_filters(ptr_to_str(extensions_csv_ptr));
    let root = Path::new(root_path_str);

    let data = match scan::run_directory_scan(
        root,
        &filters,
        max_read_bytes_per_sec,
        max_file_ops_per_sec,
    ) {
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

    let path = Path::new(file_path_str);
    let store_root = Path::new(store_root_str);

    let safe_chunk = if chunk_size == 0 {
        64 * 1024
    } else {
        chunk_size as usize
    };
    let chunk = safe_chunk.clamp(4 * 1024, 4 * 1024 * 1024);

    let data = match block_store::store_file_blocks(path, store_root, chunk) {
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

    let Some(blocks_json) = ptr_to_str(blocks_json_ptr) else {
        set_last_error("blocks json is null or invalid utf-8");
        return -1;
    };

    let Some(target_path_str) = ptr_to_str(target_path_ptr) else {
        set_last_error("target path is null or invalid utf-8");
        return -1;
    };

    let store_root = Path::new(store_root_str);
    let target_path = Path::new(target_path_str);
    let overwrite = overwrite_existing != 0;

    match block_store::restore_file_from_blocks(store_root, blocks_json, target_path, overwrite) {
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

    let compressed = match crypto::zstd_compress_buffer(input, level) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
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

    let decompressed = match crypto::zstd_decompress_buffer(input) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
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
pub extern "C" fn veyra_build_text_diff_utf8(
    left_file_path_ptr: *const c_char,
    right_file_path_ptr: *const c_char,
    max_lines: u32,
    out: *mut u8,
    out_len: u64,
    written: *mut u64,
) -> i32 {
    clear_last_error();

    let Some(left_file_path_str) = ptr_to_str(left_file_path_ptr) else {
        set_last_error("left file path is null or invalid utf-8");
        return -1;
    };

    let Some(right_file_path_str) = ptr_to_str(right_file_path_ptr) else {
        set_last_error("right file path is null or invalid utf-8");
        return -1;
    };

    let effective_max_lines = if max_lines == 0 {
        200
    } else {
        max_lines as usize
    }
    .clamp(200, 20_000);

    let left_path = Path::new(left_file_path_str);
    let right_path = Path::new(right_file_path_str);

    let payload = match text_diff::build_text_diff_json(left_path, right_path, effective_max_lines)
    {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    write_bytes(out, out_len, &payload, written)
}

#[no_mangle]
pub extern "C" fn veyra_compare_snapshot_links_utf8(
    current_states_json_ptr: *const c_char,
    previous_states_json_ptr: *const c_char,
    out: *mut u8,
    out_len: u64,
    written: *mut u64,
) -> i32 {
    clear_last_error();

    let Some(current_json) = ptr_to_str(current_states_json_ptr) else {
        set_last_error("current states json is null or invalid utf-8");
        return -1;
    };

    let Some(previous_json) = ptr_to_str(previous_states_json_ptr) else {
        set_last_error("previous states json is null or invalid utf-8");
        return -1;
    };

    let payload = match snapshot_compare::compare_snapshot_links_json(current_json, previous_json) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    write_bytes(out, out_len, &payload, written)
}

#[no_mangle]
pub extern "C" fn veyra_compare_repository_paths_utf8(
    current_states_json_ptr: *const c_char,
    baseline_states_json_ptr: *const c_char,
    take: u32,
    out: *mut u8,
    out_len: u64,
    written: *mut u64,
) -> i32 {
    clear_last_error();

    let Some(current_json) = ptr_to_str(current_states_json_ptr) else {
        set_last_error("current states json is null or invalid utf-8");
        return -1;
    };

    let Some(baseline_json) = ptr_to_str(baseline_states_json_ptr) else {
        set_last_error("baseline states json is null or invalid utf-8");
        return -1;
    };

    let effective_take = if take == 0 { 2000 } else { take as usize }.clamp(1, 5000);

    let payload = match snapshot_compare::compare_repository_paths_json(
        current_json,
        baseline_json,
        effective_take,
    ) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    write_bytes(out, out_len, &payload, written)
}

#[no_mangle]
pub extern "C" fn veyra_plan_repository_versions_utf8(
    states_json_ptr: *const c_char,
    out: *mut u8,
    out_len: u64,
    written: *mut u64,
) -> i32 {
    clear_last_error();

    let Some(states_json) = ptr_to_str(states_json_ptr) else {
        set_last_error("states json is null or invalid utf-8");
        return -1;
    };

    let payload = match snapshot_compare::plan_repository_versions_json(states_json) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
            return -1;
        }
    };

    write_bytes(out, out_len, &payload, written)
}

#[no_mangle]
pub extern "C" fn veyra_zstd_compress_file(
    src_path_ptr: *const c_char,
    dst_path_ptr: *const c_char,
    level: c_int,
) -> i64 {
    clear_last_error();

    let Some(src_path_str) = ptr_to_str(src_path_ptr) else {
        set_last_error("source path is null or invalid utf-8");
        return -1;
    };

    let Some(dst_path_str) = ptr_to_str(dst_path_ptr) else {
        set_last_error("destination path is null or invalid utf-8");
        return -1;
    };

    let src_path = Path::new(src_path_str);
    let dst_path = Path::new(dst_path_str);

    match crypto::zstd_compress_file(src_path, dst_path, level) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
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

    let Some(src_path_str) = ptr_to_str(src_path_ptr) else {
        set_last_error("source path is null or invalid utf-8");
        return -1;
    };

    let Some(dst_path_str) = ptr_to_str(dst_path_ptr) else {
        set_last_error("destination path is null or invalid utf-8");
        return -1;
    };

    let src_path = Path::new(src_path_str);
    let dst_path = Path::new(dst_path_str);

    match crypto::zstd_decompress_file(src_path, dst_path) {
        Ok(v) => v,
        Err(e) => {
            set_last_error(&e);
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
