use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

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

    let compressed = zstd::encode_all(bytes, 3).map_err(|e| format!("compress block {hash}: {e}"))?;

    let temp_path = block_path.with_extension(format!("{}.tmp", std::process::id()));
    if temp_path.exists() {
        fs::remove_file(&temp_path).ok();
    }

    {
        let mut f = File::create(&temp_path)
            .map_err(|e| format!("create temp {}: {e}", temp_path.display()))?;
        f.write_all(&compressed)
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

pub fn store_file_blocks(file_path: &Path, store_root: &Path, chunk_size: usize) -> Result<Vec<u8>, String> {
    if !file_path.exists() {
        return Err(format!("file not found: {}", file_path.display()));
    }

    if !file_path.is_file() {
        return Err(format!("path is not a file: {}", file_path.display()));
    }

    if chunk_size == 0 {
        return Err("chunk size must be positive".to_string());
    }

    let mut file = File::open(file_path).map_err(|e| format!("open {}: {e}", file_path.display()))?;

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

    zstd::decode_all(bytes.as_slice()).map_err(|e| format!("decompress block {hash}: {e}"))
}

pub fn restore_file_from_blocks(
    store_root: &Path,
    blocks_json: &str,
    target_path: &Path,
    overwrite_existing: bool,
) -> Result<i64, String> {
    let blocks: Vec<RestoreBlockRef> = serde_json::from_str(blocks_json)
        .map_err(|e| format!("parse blocks json: {e}"))?;

    if blocks.is_empty() {
        return Err("blocks list is empty".to_string());
    }

    if target_path.exists() && !overwrite_existing {
        return Err(format!("target already exists: {}", target_path.display()));
    }

    if let Some(parent) = target_path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("create_dir_all {}: {e}", parent.display()))?;
    }

    let mut total_written = 0_i64;

    let temp_path = target_path.with_extension(format!("{}.tmp", std::process::id()));
    if temp_path.exists() {
        fs::remove_file(&temp_path).ok();
    }

    {
        let mut out = File::create(&temp_path)
            .map_err(|e| format!("create {}: {e}", temp_path.display()))?;

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

    fs::rename(&temp_path, target_path)
        .map_err(|e| format!("rename {} -> {}: {e}", temp_path.display(), target_path.display()))?;

    Ok(total_written)
}
