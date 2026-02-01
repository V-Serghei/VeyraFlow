use std::fs;
use std::path::Path;

use crate::error::{VeyraError, VeyraStatus, VeyraResult};

pub fn file_size(path: &Path) -> VeyraResult<u64> {
    let meta = fs::metadata(path)
        .map_err(|e| VeyraError::new(VeyraStatus::IoError, format!("metadata: {e}")))?;
    Ok(meta.len())
}

pub fn read_all(path: &Path) -> VeyraResult<Vec<u8>> {
    fs::read(path).map_err(|e| VeyraError::new(VeyraStatus::IoError, format!("read: {e}")))
}
