use std::collections::hash_map::DefaultHasher;
use std::fs::File;
use std::hash::{Hash, Hasher};
use std::io::Read;
use std::path::Path;
use std::time::{Duration, Instant};

use sha2::{Digest, Sha256};

pub fn bytes_to_hex(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        out.push_str(&format!("{b:02x}"));
    }
    out
}

pub fn blake3_file_hex(path: &Path) -> Result<String, String> {
    let mut file = File::open(path).map_err(|e| format!("open {}: {e}", path.display()))?;
    let mut hasher = blake3::Hasher::new();
    let mut buf = [0u8; 65536];
    loop {
        let n = file.read(&mut buf).map_err(|e| format!("read: {e}"))?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hasher.finalize().to_hex().to_string())
}

pub fn sha256_file_hex(path: &Path) -> Result<String, String> {
    sha256_file_hex_with_limit(path, 0)
}

pub fn sha256_file_hex_with_limit(
    path: &Path,
    max_read_bytes_per_sec: u64,
) -> Result<String, String> {
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

            let expected =
                Duration::from_secs_f64(total_read as f64 / max_read_bytes_per_sec as f64);
            let elapsed = start.elapsed();
            if expected > elapsed {
                std::thread::sleep(expected - elapsed);
            }
        }
    }

    let digest = hasher.finalize();
    Ok(bytes_to_hex(&digest))
}

pub fn hash_line(value: &str) -> u64 {
    let mut hasher = DefaultHasher::new();
    value.hash(&mut hasher);
    hasher.finish()
}
