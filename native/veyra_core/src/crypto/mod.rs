use std::fs::File;
use std::io::Write;
use std::os::raw::c_int;
use std::path::Path;

pub fn zstd_compress_buffer(data: &[u8], level: c_int) -> Result<Vec<u8>, String> {
    zstd::encode_all(data, level).map_err(|e| format!("zstd compress: {e}"))
}

pub fn zstd_decompress_buffer(data: &[u8]) -> Result<Vec<u8>, String> {
    zstd::decode_all(data).map_err(|e| format!("zstd decompress: {e}"))
}

pub fn zstd_compress_file(src_path: &Path, dst_path: &Path, level: c_int) -> Result<i64, String> {
    let input = std::fs::read(src_path).map_err(|e| format!("read source file: {e}"))?;
    let compressed = zstd_compress_buffer(input.as_slice(), level)?;

    let mut out_file =
        File::create(dst_path).map_err(|e| format!("create destination file: {e}"))?;
    out_file
        .write_all(&compressed)
        .map_err(|e| format!("write destination file: {e}"))?;

    Ok(compressed.len() as i64)
}

pub fn zstd_decompress_file(src_path: &Path, dst_path: &Path) -> Result<i64, String> {
    let input = std::fs::read(src_path).map_err(|e| format!("read source file: {e}"))?;
    let decompressed = zstd_decompress_buffer(input.as_slice())?;

    let mut out_file =
        File::create(dst_path).map_err(|e| format!("create destination file: {e}"))?;
    out_file
        .write_all(&decompressed)
        .map_err(|e| format!("write destination file: {e}"))?;

    Ok(decompressed.len() as i64)
}
