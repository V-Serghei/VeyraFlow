use std::os::raw::c_char;

mod error;
mod ffi;
mod io;

use error::{VeyraResult, VeyraStatus};

static VERSION: &[u8] = b"0.1.0\0";

fn run_ffi(f: impl FnOnce() -> VeyraResult<()>) -> i32 {
    ffi::clear_last_error();
    let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(f));
    match r {
        Ok(Ok(())) => VeyraStatus::Ok.code(),
        Ok(Err(e)) => {
            ffi::set_last_error(&e.message);
            e.status.code()
        }
        Err(_) => {
            ffi::set_last_error("panic");
            VeyraStatus::InternalError.code()
        }
    }
}

#[no_mangle]
pub extern "C" fn veyra_get_version() -> *const c_char {
    VERSION.as_ptr() as *const c_char
}

#[no_mangle]
pub extern "C" fn veyra_last_error_utf8(out: *mut u8, out_len: u64, written: *mut u64) -> i32 {
    run_ffi(|| unsafe {
        let data = ffi::last_error_bytes();
        ffi::write_bytes(out, out_len, &data, written)?;
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn veyra_file_size_utf8(path: *const c_char, size_out: *mut u64) -> i32 {
    run_ffi(|| unsafe {
        let p = ffi::cstr_to_path_utf8(path)?;
        let size = io::file::file_size(&p)?;
        ffi::write_u64(size_out, size)?;
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn veyra_read_file_utf8(
    path: *const c_char,
    out: *mut u8,
    out_len: u64,
    written: *mut u64,
) -> i32 {
    run_ffi(|| unsafe {
        let p = ffi::cstr_to_path_utf8(path)?;
        let size = io::file::file_size(&p)?;
        if out.is_null() || out_len == 0 {
            *written = size;
            return Ok(());
        }
        if out_len < size {
            *written = size;
            return Err(crate::error::VeyraError::new(
                crate::error::VeyraStatus::BufferTooSmall,
                "output buffer too small",
            ));
        }
        let data = io::file::read_all(&p)?;
        ffi::write_bytes(out, out_len, &data, written)?;
        Ok(())
    })
}
