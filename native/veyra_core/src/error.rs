#[repr(i32)]
#[derive(Clone, Copy, Debug)]
pub enum VeyraStatus {
    Ok = 0,
    InvalidArg = -1,
    BufferTooSmall = -2,
    IoError = -3,
    Utf8Error = -4,
    InternalError = -5,
}

impl VeyraStatus {
    pub fn code(self) -> i32 {
        self as i32
    }
}

#[derive(Debug)]
pub struct VeyraError {
    pub status: VeyraStatus,
    pub message: String,
}

impl VeyraError {
    pub fn new(status: VeyraStatus, message: impl Into<String>) -> Self {
        Self {
            status,
            message: message.into(),
        }
    }
}

pub type VeyraResult<T> = Result<T, VeyraError>;
