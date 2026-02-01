use std::cell::RefCell;
use std::ffi::CStr;
use std::os::raw::c_char;
use std::path::PathBuf;

use crate::error::{VeyraError, VeyraStatus, VeyraResult};

thread_local! {
    static LAST_ERROR: RefCell<Vec<u8>> = RefCell::new(Vec::new());
}

pub fn clear_last_error() {
    LAST_ERROR.with(|c| c.borrow_mut().clear());
}

pub fn set_last_error(msg: &str) {
    LAST_ERROR.with(|c| {
        let mut b = msg.as_bytes().to_vec();
        b.push(0);
        *c.borrow_mut() = b;
    });
}

pub fn last_error_bytes() -> Vec<u8> {
    LAST_ERROR.with(|c| c.borrow().clone())
}

pub unsafe fn cstr_to_path_utf8(ptr: *const c_char) -> VeyraResult<PathBuf> {
    if ptr.is_null() {
        return Err(VeyraError::new(VeyraStatus::InvalidArg, "path is null"));
    }
    let s = CStr::from_ptr(ptr)
        .to_str()
        .map_err(|_| VeyraError::new(VeyraStatus::Utf8Error, "path is not valid utf-8"))?;
    Ok(PathBuf::from(s))
}

pub unsafe fn write_bytes(
    out: *mut u8,
    out_len: u64,
    data: &[u8],
    written: *mut u64,
) -> VeyraResult<()> {
    if written.is_null() {
        return Err(VeyraError::new(VeyraStatus::InvalidArg, "written is null"));
    }
    *written = data.len() as u64;

    if out.is_null() || out_len == 0 {
        return Ok(());
    }
    if out_len < data.len() as u64 {
        return Err(VeyraError::new(VeyraStatus::BufferTooSmall, "output buffer too small"));
    }

    std::ptr::copy_nonoverlapping(data.as_ptr(), out, data.len());
    Ok(())
}

pub unsafe fn write_u64(out: *mut u64, value: u64) -> VeyraResult<()> {
    if out.is_null() {
        return Err(VeyraError::new(VeyraStatus::InvalidArg, "out is null"));
    }
    *out = value;
    Ok(())
}
