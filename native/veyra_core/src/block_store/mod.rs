use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

const BLOCK_PAYLOAD_MAGIC: &[u8; 4] = b"VYB1";
const BLOCK_PAYLOAD_VERSION: u8 = 1;
const BLOCK_COMPRESSION_NONE: u8 = 0;
const BLOCK_COMPRESSION_ZSTD: u8 = 1;
const BLOCK_COMPRESSION_LEVEL: i32 = 6;

#[derive(Serialize)]
struct StoredBlockRef {
    sequence: u32,
    block_hash_blake3: String,
    length_bytes: u32,
    stored_size_bytes: u64,
}

#[derive(Serialize)]
struct StoreFileBlocksResult {
    file_size_bytes: u64,
    stored_size_bytes: u64,
    block_count: u32,
    deduped_blocks: u32,
    new_blocks: u32,
    blocks: Vec<StoredBlockRef>,
}

#[derive(Deserialize)]
struct RestoreBlockRef {
    block_hash_blake3: String,
    length_bytes: u32,
}

fn block_path_for_hash(store_root: &Path, hash: &str) -> PathBuf {
    let p1 = hash.get(0..2).unwrap_or("00");
    let p2 = hash.get(2..4).unwrap_or("00");

    store_root
        .join("blocks")
        .join(p1)
        .join(p2)
        .join(format!("{hash}.zst"))
}

fn persist_block(store_root: &Path, hash: &str, bytes: &[u8]) -> Result<(u64, bool), String> {
    let block_path = block_path_for_hash(store_root, hash);

    if block_path.exists() {
        let sz = fs::metadata(&block_path)
            .map_err(|e| format!("metadata {}: {e}", block_path.display()))?
            .len();
        return Ok((sz, false));
    }

    let parent = block_path
        .parent()
        .ok_or_else(|| format!("no parent for {}", block_path.display()))?;

    fs::create_dir_all(parent).map_err(|e| format!("create_dir_all {}: {e}", parent.display()))?;

    if block_path.exists() {
        let sz = fs::metadata(&block_path)
            .map_err(|e| format!("metadata {}: {e}", block_path.display()))?
            .len();
        return Ok((sz, false));
    }

    let encoded = encode_block_payload(hash, bytes)?;

    let temp_path = block_path.with_extension(format!("{}.tmp", std::process::id()));
    if temp_path.exists() {
        fs::remove_file(&temp_path).ok();
    }

    {
        let mut f = File::create(&temp_path)
            .map_err(|e| format!("create temp {}: {e}", temp_path.display()))?;
        f.write_all(&encoded)
            .map_err(|e| format!("write temp {}: {e}", temp_path.display()))?;
        f.flush()
            .map_err(|e| format!("flush temp {}: {e}", temp_path.display()))?;
    }

    match fs::rename(&temp_path, &block_path) {
        Ok(_) => {}
        Err(_) => {
            if block_path.exists() {
                fs::remove_file(&temp_path).ok();
                let sz = fs::metadata(&block_path)
                    .map_err(|e| format!("metadata {}: {e}", block_path.display()))?
                    .len();
                return Ok((sz, false));
            }
            return Err(format!(
                "rename {} -> {} failed",
                temp_path.display(),
                block_path.display()
            ));
        }
    }

    let stored_size = fs::metadata(&block_path)
        .map_err(|e| format!("metadata {}: {e}", block_path.display()))?
        .len();

    Ok((stored_size, true))
}

fn encode_block_payload(hash: &str, bytes: &[u8]) -> Result<Vec<u8>, String> {
    let compressed = zstd::encode_all(bytes, BLOCK_COMPRESSION_LEVEL)
        .map_err(|e| format!("compress block {hash}: {e}"))?;

    let (compression_kind, payload) = if compressed.len() < bytes.len() {
        (BLOCK_COMPRESSION_ZSTD, compressed)
    } else {
        (BLOCK_COMPRESSION_NONE, bytes.to_vec())
    };

    let mut encoded = Vec::with_capacity(BLOCK_PAYLOAD_MAGIC.len() + 2 + payload.len());
    encoded.extend_from_slice(BLOCK_PAYLOAD_MAGIC);
    encoded.push(BLOCK_PAYLOAD_VERSION);
    encoded.push(compression_kind);
    encoded.extend_from_slice(&payload);
    Ok(encoded)
}

fn decode_block_payload(hash: &str, bytes: &[u8]) -> Result<Vec<u8>, String> {
    let header_len = BLOCK_PAYLOAD_MAGIC.len() + 2;
    if bytes.len() >= header_len && &bytes[..BLOCK_PAYLOAD_MAGIC.len()] == BLOCK_PAYLOAD_MAGIC {
        let version = bytes[BLOCK_PAYLOAD_MAGIC.len()];
        if version != BLOCK_PAYLOAD_VERSION {
            return Err(format!(
                "unsupported block payload version {version} for block {hash}"
            ));
        }

        let compression_kind = bytes[BLOCK_PAYLOAD_MAGIC.len() + 1];
        let payload = &bytes[header_len..];

        return match compression_kind {
            BLOCK_COMPRESSION_NONE => Ok(payload.to_vec()),
            BLOCK_COMPRESSION_ZSTD => zstd::decode_all(payload)
                .map_err(|e| format!("decompress block {hash}: {e}")),
            _ => Err(format!(
                "unsupported block compression kind {compression_kind} for block {hash}"
            )),
        };
    }

    // Legacy native blocks were stored as bare zstd streams without an envelope.
    zstd::decode_all(bytes).map_err(|e| format!("decompress block {hash}: {e}"))
}

pub fn store_file_blocks(
    file_path: &Path,
    store_root: &Path,
    chunk_size: usize,
) -> Result<Vec<u8>, String> {
    if !file_path.exists() {
        return Err(format!("file not found: {}", file_path.display()));
    }

    if !file_path.is_file() {
        return Err(format!("path is not a file: {}", file_path.display()));
    }

    if chunk_size == 0 {
        return Err("chunk size must be positive".to_string());
    }

    let mut file =
        File::open(file_path).map_err(|e| format!("open {}: {e}", file_path.display()))?;

    let mut buffer = vec![0_u8; chunk_size];
    let mut blocks = Vec::new();

    let mut total_file_size = 0_u64;
    let mut total_stored_size = 0_u64;
    let mut deduped_blocks = 0_u32;
    let mut new_blocks = 0_u32;

    let mut sequence = 0_u32;

    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|e| format!("read {}: {e}", file_path.display()))?;

        if read == 0 {
            break;
        }

        let chunk = &buffer[..read];
        let hash = blake3::hash(chunk).to_hex().to_string();

        let (stored_size, created) = persist_block(store_root, &hash, chunk)?;

        total_file_size += read as u64;
        total_stored_size += stored_size;

        if created {
            new_blocks += 1;
        } else {
            deduped_blocks += 1;
        }

        blocks.push(StoredBlockRef {
            sequence,
            block_hash_blake3: hash,
            length_bytes: read as u32,
            stored_size_bytes: stored_size,
        });

        sequence += 1;
    }

    let result = StoreFileBlocksResult {
        file_size_bytes: total_file_size,
        stored_size_bytes: total_stored_size,
        block_count: blocks.len() as u32,
        deduped_blocks,
        new_blocks,
        blocks,
    };

    serde_json::to_vec(&result).map_err(|e| format!("serialize store result: {e}"))
}

fn read_block(store_root: &Path, hash: &str) -> Result<Vec<u8>, String> {
    let block_path = block_path_for_hash(store_root, hash);

    if !block_path.exists() {
        return Err(format!("block not found: {}", block_path.display()));
    }

    let bytes = fs::read(&block_path).map_err(|e| format!("read {}: {e}", block_path.display()))?;

    decode_block_payload(hash, bytes.as_slice())
}

pub fn restore_file_from_blocks(
    store_root: &Path,
    blocks_json: &str,
    target_path: &Path,
    overwrite_existing: bool,
) -> Result<i64, String> {
    let blocks: Vec<RestoreBlockRef> =
        serde_json::from_str(blocks_json).map_err(|e| format!("parse blocks json: {e}"))?;

    if blocks.is_empty() {
        return Err("blocks list is empty".to_string());
    }

    if target_path.exists() && !overwrite_existing {
        return Err(format!("target already exists: {}", target_path.display()));
    }

    if let Some(parent) = target_path.parent() {
        fs::create_dir_all(parent)
            .map_err(|e| format!("create_dir_all {}: {e}", parent.display()))?;
    }

    let mut total_written = 0_i64;

    let temp_path = target_path.with_extension(format!("{}.tmp", std::process::id()));
    if temp_path.exists() {
        fs::remove_file(&temp_path).ok();
    }

    {
        let mut out =
            File::create(&temp_path).map_err(|e| format!("create {}: {e}", temp_path.display()))?;

        for block in blocks {
            let decoded = read_block(store_root, &block.block_hash_blake3)?;

            let expected = block.length_bytes as usize;
            if decoded.len() < expected {
                return Err(format!(
                    "decoded block shorter than expected for hash {} (decoded {}, expected {})",
                    block.block_hash_blake3,
                    decoded.len(),
                    expected
                ));
            }

            out.write_all(&decoded[..expected])
                .map_err(|e| format!("write {}: {e}", temp_path.display()))?;

            total_written += expected as i64;
        }

        out.flush()
            .map_err(|e| format!("flush {}: {e}", temp_path.display()))?;
    }

    if target_path.exists() {
        fs::remove_file(target_path)
            .map_err(|e| format!("remove existing {}: {e}", target_path.display()))?;
    }

    fs::rename(&temp_path, target_path).map_err(|e| {
        format!(
            "rename {} -> {}: {e}",
            temp_path.display(),
            target_path.display()
        )
    })?;

    Ok(total_written)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encode_decode_block_payload_round_trips_compressible_data() {
        let raw = vec![b'A'; 16 * 1024];
        let encoded = encode_block_payload("compressible", raw.as_slice()).unwrap();

        assert_eq!(&encoded[..BLOCK_PAYLOAD_MAGIC.len()], BLOCK_PAYLOAD_MAGIC);
        assert_eq!(encoded[BLOCK_PAYLOAD_MAGIC.len() + 1], BLOCK_COMPRESSION_ZSTD);

        let decoded = decode_block_payload("compressible", encoded.as_slice()).unwrap();
        assert_eq!(decoded, raw);
    }

    #[test]
    fn encode_decode_block_payload_round_trips_incompressible_data_without_forcing_zstd() {
        let mut raw = Vec::with_capacity(64 * 1024);
        let mut counter = 0_u64;
        while raw.len() < 64 * 1024 {
            let digest = blake3::hash(counter.to_le_bytes().as_slice());
            raw.extend_from_slice(digest.as_bytes());
            counter += 1;
        }
        raw.truncate(64 * 1024);
        let encoded = encode_block_payload("incompressible", raw.as_slice()).unwrap();

        assert_eq!(&encoded[..BLOCK_PAYLOAD_MAGIC.len()], BLOCK_PAYLOAD_MAGIC);
        assert_eq!(encoded[BLOCK_PAYLOAD_MAGIC.len() + 1], BLOCK_COMPRESSION_NONE);

        let decoded = decode_block_payload("incompressible", encoded.as_slice()).unwrap();
        assert_eq!(decoded, raw);
    }
}
