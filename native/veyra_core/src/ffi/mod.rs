pub mod api;

use std::cell::RefCell;
use std::ffi::CStr;
use std::os::raw::{c_char, c_int};
use std::ptr;

thread_local! {
    static LAST_ERROR: RefCell<Vec<u8>> = RefCell::new(Vec::new());
}

pub fn clear_last_error() {
    LAST_ERROR.with(|c| c.borrow_mut().clear());
}

pub fn set_last_error(msg: &str) {
    LAST_ERROR.with(|c| *c.borrow_mut() = msg.as_bytes().to_vec());
}

pub fn set_error_from<E: std::fmt::Display>(prefix: &str, err: E) {
    set_last_error(&format!("{prefix}: {err}"));
}

pub fn get_last_error() -> Vec<u8> {
    LAST_ERROR.with(|c| c.borrow().clone())
}

pub fn write_bytes(out: *mut u8, out_len: u64, data: &[u8], written: *mut u64) -> i32 {
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

pub fn copy_bytes_to_out(data: &[u8], out: *mut u8, out_len: c_int) -> c_int {
    if out.is_null() || out_len < 0 {
        return -1;
    }

    let to_copy = data.len().min(out_len as usize);
    unsafe {
        ptr::copy_nonoverlapping(data.as_ptr(), out, to_copy);
    }

    to_copy as c_int
}

pub fn ptr_to_str<'a>(ptr: *const c_char) -> Option<&'a str> {
    if ptr.is_null() {
        return None;
    }

    unsafe { CStr::from_ptr(ptr) }.to_str().ok()
}
