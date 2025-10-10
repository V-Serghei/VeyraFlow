use std::os::raw::{c_char, c_int, c_uchar};
use std::slice;
use std::ffi::CString;
use blake3::Hash;
use chacha20poly1305::{
    aead::{Aead, KeyInit, OsRng},
    XChaCha20Poly1305, XNonce,
};
use chacha20poly1305::aead::rand_core::RngCore;

/// Computes BLAKE3 hash of the given data
/// Returns length of hash written to output buffer
#[no_mangle]
pub extern "C" fn veyra_blake3_hash(
    data: *const c_uchar,
    data_len: c_int,
    output: *mut c_uchar,
    output_len: c_int,
) -> c_int {
    if data.is_null() || output.is_null() || data_len <= 0 || output_len < 32 {
        return -1;
    }

    let data_slice = unsafe { slice::from_raw_parts(data, data_len as usize) };
    let hash = blake3::hash(data_slice);
    let hash_bytes = hash.as_bytes();

    unsafe {
        std::ptr::copy_nonoverlapping(hash_bytes.as_ptr(), output, 32);
    }

    32
}

/// Compresses data using zstd
/// Returns length of compressed data, or -1 on error
#[no_mangle]
pub extern "C" fn veyra_zstd_compress(
    data: *const c_uchar,
    data_len: c_int,
    output: *mut c_uchar,
    output_len: c_int,
    level: c_int,
) -> c_int {
    if data.is_null() || output.is_null() || data_len <= 0 || output_len <= 0 {
        return -1;
    }

    let data_slice = unsafe { slice::from_raw_parts(data, data_len as usize) };
    let compression_level = if level > 0 { level } else { 3 };

    match zstd::bulk::compress(data_slice, compression_level) {
        Ok(compressed) => {
            if compressed.len() > output_len as usize {
                return -1;
            }
            unsafe {
                std::ptr::copy_nonoverlapping(
                    compressed.as_ptr(),
                    output,
                    compressed.len(),
                );
            }
            compressed.len() as c_int
        }
        Err(_) => -1,
    }
}

/// Decompresses zstd data
/// Returns length of decompressed data, or -1 on error
#[no_mangle]
pub extern "C" fn veyra_zstd_decompress(
    data: *const c_uchar,
    data_len: c_int,
    output: *mut c_uchar,
    output_len: c_int,
) -> c_int {
    if data.is_null() || output.is_null() || data_len <= 0 || output_len <= 0 {
        return -1;
    }

    let data_slice = unsafe { slice::from_raw_parts(data, data_len as usize) };

    match zstd::bulk::decompress(data_slice, output_len as usize) {
        Ok(decompressed) => {
            if decompressed.len() > output_len as usize {
                return -1;
            }
            unsafe {
                std::ptr::copy_nonoverlapping(
                    decompressed.as_ptr(),
                    output,
                    decompressed.len(),
                );
            }
            decompressed.len() as c_int
        }
        Err(_) => -1,
    }
}

/// Gets version string
#[no_mangle]
pub extern "C" fn veyra_get_version() -> *const c_char {
    let version = CString::new("0.1.0").unwrap();
    version.into_raw()
}

/// Frees a string returned by veyra_get_version
#[no_mangle]
pub extern "C" fn veyra_free_string(s: *mut c_char) {
    if !s.is_null() {
        unsafe {
            drop(CString::from_raw(s));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_blake3_hash() {
        let data = b"Hello, World!";
        let mut output = [0u8; 32];
        
        let result = veyra_blake3_hash(
            data.as_ptr(),
            data.len() as c_int,
            output.as_mut_ptr(),
            output.len() as c_int,
        );
        
        assert_eq!(result, 32);
        assert_ne!(output, [0u8; 32]);
    }

    #[test]
    fn test_zstd_compress_decompress() {
        let data = b"Hello, World! This is a test string for compression.";
        let mut compressed = vec![0u8; 1024];
        let mut decompressed = vec![0u8; 1024];

        let compressed_len = veyra_zstd_compress(
            data.as_ptr(),
            data.len() as c_int,
            compressed.as_mut_ptr(),
            compressed.len() as c_int,
            3,
        );

        assert!(compressed_len > 0);

        let decompressed_len = veyra_zstd_decompress(
            compressed.as_ptr(),
            compressed_len,
            decompressed.as_mut_ptr(),
            decompressed.len() as c_int,
        );

        assert_eq!(decompressed_len, data.len() as c_int);
        assert_eq!(&decompressed[..decompressed_len as usize], data);
    }
}

