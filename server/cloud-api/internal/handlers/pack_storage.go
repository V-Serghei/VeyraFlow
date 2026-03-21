package handlers

import (
	"context"
	"crypto/rand"
	"database/sql"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"

	"github.com/jackc/pgx/v5"
)

const defaultBlockPackTargetBytes int64 = 128 * 1024 * 1024

type blockStorageRecord struct {
	LengthBytes      int64
	StorageKind      string
	PackRelativePath string
	PackOffsetBytes  int64
	StoredSizeBytes  int64
}

type blockPackRecord struct {
	ID           int64
	RelativePath string
	BytesWritten int64
	BlockCount   int64
}

func (h *Handler) loadBlockRecord(ctx context.Context, hash string) (blockStorageRecord, bool, error) {
	var record blockStorageRecord
	var packRelativePath sql.NullString
	var packOffsetBytes sql.NullInt64
	var storedSizeBytes sql.NullInt64

	err := h.db.QueryRow(ctx, `
SELECT
    cb.length_bytes,
    COALESCE(NULLIF(cb.storage_kind, ''), 'loose'),
    cbp.relative_path,
    cb.pack_offset_bytes,
    cb.stored_size_bytes
FROM cloud_blocks cb
LEFT JOIN cloud_block_packs cbp ON cb.pack_id = cbp.id
WHERE cb.block_hash = $1
LIMIT 1;
`, hash).Scan(
		&record.LengthBytes,
		&record.StorageKind,
		&packRelativePath,
		&packOffsetBytes,
		&storedSizeBytes,
	)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return blockStorageRecord{}, false, nil
		}
		return blockStorageRecord{}, false, err
	}

	if packRelativePath.Valid {
		record.PackRelativePath = packRelativePath.String
	}
	if packOffsetBytes.Valid {
		record.PackOffsetBytes = packOffsetBytes.Int64
	}
	if storedSizeBytes.Valid {
		record.StoredSizeBytes = storedSizeBytes.Int64
	} else {
		record.StoredSizeBytes = record.LengthBytes
	}

	return record, true, nil
}

func (h *Handler) storeBlockInPack(ctx context.Context, hash, stagedPath string, payloadSize int64) error {
	h.packMu.Lock()
	defer h.packMu.Unlock()

	record, exists, err := h.loadBlockRecord(ctx, hash)
	if err != nil {
		return err
	}
	if exists && record.StorageKind != "missing" {
		return nil
	}

	tx, err := h.db.Begin(ctx)
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	var existingStorageKind string
	err = tx.QueryRow(ctx, `
SELECT COALESCE(NULLIF(storage_kind, ''), 'loose')
FROM cloud_blocks
WHERE block_hash = $1
LIMIT 1;
`, hash).Scan(&existingStorageKind)
	if err == nil {
		if existingStorageKind != "missing" {
			if commitErr := tx.Commit(ctx); commitErr != nil {
				return commitErr
			}
			return nil
		}
	}
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return err
	}

	pack, err := h.loadWritablePackTx(ctx, tx, payloadSize)
	if err != nil {
		return err
	}

	packPath := h.blockPackPath(pack.RelativePath)
	if err = os.MkdirAll(filepath.Dir(packPath), 0o755); err != nil {
		return err
	}

	source, err := os.Open(stagedPath)
	if err != nil {
		return err
	}
	defer source.Close()

	target, err := os.OpenFile(packPath, os.O_CREATE|os.O_RDWR, 0o644)
	if err != nil {
		return err
	}
	defer target.Close()

	currentSize := pack.BytesWritten
	if stat, statErr := target.Stat(); statErr == nil {
		switch {
		case stat.Size() < currentSize:
			return fmt.Errorf("pack file %s is smaller than recorded size", packPath)
		case stat.Size() > currentSize:
			if truncateErr := target.Truncate(currentSize); truncateErr != nil {
				return fmt.Errorf("truncate pack %s: %w", packPath, truncateErr)
			}
		}
	}

	if _, err = target.Seek(currentSize, io.SeekStart); err != nil {
		return err
	}

	written, err := io.Copy(target, source)
	if err != nil {
		_ = target.Truncate(currentSize)
		return err
	}
	if written != payloadSize {
		_ = target.Truncate(currentSize)
		return fmt.Errorf("pack append size mismatch: expected %d, wrote %d", payloadSize, written)
	}
	if err = target.Sync(); err != nil {
		_ = target.Truncate(currentSize)
		return err
	}

	if existingStorageKind == "missing" {
		_, err = tx.Exec(ctx, `
UPDATE cloud_blocks
SET length_bytes = $2,
    storage_kind = 'pack',
    pack_id = $3,
    pack_offset_bytes = $4,
    stored_size_bytes = $5
WHERE block_hash = $1;
`, hash, payloadSize, pack.ID, currentSize, payloadSize)
		if err != nil {
			_ = target.Truncate(currentSize)
			return err
		}
	} else {
		insertTag, insertErr := tx.Exec(ctx, `
INSERT INTO cloud_blocks(
    block_hash,
    length_bytes,
    created_at,
    storage_kind,
    pack_id,
    pack_offset_bytes,
    stored_size_bytes)
VALUES($1, $2, now(), 'pack', $3, $4, $5)
ON CONFLICT (block_hash) DO NOTHING;
`, hash, payloadSize, pack.ID, currentSize, payloadSize)
		if insertErr != nil {
			_ = target.Truncate(currentSize)
			return insertErr
		}
		if insertTag.RowsAffected() == 0 {
			_ = target.Truncate(currentSize)
			return nil
		}
	}

	if err = h.updatePackAfterAppendTx(ctx, tx, pack.ID, currentSize+payloadSize); err != nil {
		_ = target.Truncate(currentSize)
		return err
	}

	if err = tx.Commit(ctx); err != nil {
		_ = target.Truncate(currentSize)
		return err
	}

	return nil
}

func (h *Handler) CompactLooseBlocks(ctx context.Context, maxBlocks int) (int, error) {
	if maxBlocks <= 0 {
		return 0, nil
	}

	rows, err := h.db.Query(ctx, `
SELECT block_hash
FROM cloud_blocks
WHERE COALESCE(NULLIF(storage_kind, ''), 'loose') = 'loose'
ORDER BY created_at ASC, block_hash ASC
LIMIT $1;
`, maxBlocks)
	if err != nil {
		return 0, err
	}
	defer rows.Close()

	hashes := make([]string, 0, maxBlocks)
	for rows.Next() {
		var hash string
		if scanErr := rows.Scan(&hash); scanErr != nil {
			return 0, scanErr
		}
		hashes = append(hashes, hash)
	}

	compacted := 0
	var firstErr error
	for _, hash := range hashes {
		changed, compactErr := h.compactLooseBlock(ctx, hash)
		if compactErr != nil {
			if firstErr == nil {
				firstErr = compactErr
			}
			continue
		}
		if changed {
			compacted++
		}
	}

	return compacted, firstErr
}

func (h *Handler) compactLooseBlock(ctx context.Context, hash string) (bool, error) {
	h.packMu.Lock()
	defer h.packMu.Unlock()

	record, exists, err := h.loadBlockRecord(ctx, hash)
	if err != nil {
		return false, err
	}
	if !exists || record.StorageKind != "loose" {
		return false, nil
	}

	legacyPath := h.blockPath(hash)
	source, err := os.Open(legacyPath)
	if err != nil {
		if os.IsNotExist(err) {
			if markErr := h.markBlockMissing(ctx, hash); markErr != nil {
				return false, fmt.Errorf("legacy loose block file is missing for %s and metadata update failed: %w", hash, markErr)
			}
			return false, nil
		}
		return false, err
	}
	defer source.Close()

	stat, err := source.Stat()
	if err != nil {
		return false, err
	}
	payloadSize := stat.Size()
	if payloadSize <= 0 {
		payloadSize = record.LengthBytes
	}

	tx, err := h.db.Begin(ctx)
	if err != nil {
		return false, err
	}
	defer func() { _ = tx.Rollback(ctx) }()

	var storageKind string
	err = tx.QueryRow(ctx, `
SELECT COALESCE(NULLIF(storage_kind, ''), 'loose')
FROM cloud_blocks
WHERE block_hash = $1
FOR UPDATE;
`, hash).Scan(&storageKind)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return false, nil
		}
		return false, err
	}
	if storageKind != "loose" {
		if commitErr := tx.Commit(ctx); commitErr != nil {
			return false, commitErr
		}
		return false, nil
	}

	pack, err := h.loadWritablePackTx(ctx, tx, payloadSize)
	if err != nil {
		return false, err
	}

	packPath := h.blockPackPath(pack.RelativePath)
	if err = os.MkdirAll(filepath.Dir(packPath), 0o755); err != nil {
		return false, err
	}

	target, err := os.OpenFile(packPath, os.O_CREATE|os.O_RDWR, 0o644)
	if err != nil {
		return false, err
	}
	defer target.Close()

	currentSize := pack.BytesWritten
	if packStat, statErr := target.Stat(); statErr == nil {
		switch {
		case packStat.Size() < currentSize:
			return false, fmt.Errorf("pack file %s is smaller than recorded size", packPath)
		case packStat.Size() > currentSize:
			if truncateErr := target.Truncate(currentSize); truncateErr != nil {
				return false, fmt.Errorf("truncate pack %s: %w", packPath, truncateErr)
			}
		}
	}

	if _, err = target.Seek(currentSize, io.SeekStart); err != nil {
		return false, err
	}
	if _, err = source.Seek(0, io.SeekStart); err != nil {
		return false, err
	}

	written, err := io.Copy(target, source)
	if err != nil {
		_ = target.Truncate(currentSize)
		return false, err
	}
	if written != payloadSize {
		_ = target.Truncate(currentSize)
		return false, fmt.Errorf("pack append size mismatch during compaction: expected %d, wrote %d", payloadSize, written)
	}
	if err = target.Sync(); err != nil {
		_ = target.Truncate(currentSize)
		return false, err
	}

	result, err := tx.Exec(ctx, `
UPDATE cloud_blocks
SET storage_kind = 'pack',
    pack_id = $2,
    pack_offset_bytes = $3,
    stored_size_bytes = $4,
    length_bytes = $5
WHERE block_hash = $1
  AND COALESCE(NULLIF(storage_kind, ''), 'loose') = 'loose';
`, hash, pack.ID, currentSize, payloadSize, payloadSize)
	if err != nil {
		_ = target.Truncate(currentSize)
		return false, err
	}
	if result.RowsAffected() == 0 {
		_ = target.Truncate(currentSize)
		return false, nil
	}

	if err = h.updatePackAfterAppendTx(ctx, tx, pack.ID, currentSize+payloadSize); err != nil {
		_ = target.Truncate(currentSize)
		return false, err
	}

	if err = tx.Commit(ctx); err != nil {
		_ = target.Truncate(currentSize)
		return false, err
	}

	if removeErr := os.Remove(legacyPath); removeErr != nil && !os.IsNotExist(removeErr) {
		return true, fmt.Errorf("compaction committed but failed to remove legacy block %s: %w", hash, removeErr)
	}

	return true, nil
}

func (h *Handler) loadWritablePackTx(ctx context.Context, tx pgx.Tx, incomingBytes int64) (blockPackRecord, error) {
	var pack blockPackRecord

	err := tx.QueryRow(ctx, `
SELECT id, relative_path, bytes_written, block_count
FROM cloud_block_packs
WHERE state = 'active'
ORDER BY id DESC
LIMIT 1
FOR UPDATE;
`).Scan(&pack.ID, &pack.RelativePath, &pack.BytesWritten, &pack.BlockCount)
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return blockPackRecord{}, err
	}

	if err == nil && pack.BlockCount > 0 && pack.BytesWritten+incomingBytes > h.packTargetBytes {
		if _, err = tx.Exec(ctx, `
UPDATE cloud_block_packs
SET state = 'sealed',
    updated_at = now(),
    sealed_at = COALESCE(sealed_at, now())
WHERE id = $1;
`, pack.ID); err != nil {
			return blockPackRecord{}, err
		}
		pack = blockPackRecord{}
	}

	if pack.ID != 0 {
		return pack, nil
	}

	return h.createWritablePackTx(ctx, tx)
}

func (h *Handler) markBlockMissing(ctx context.Context, hash string) error {
	_, err := h.db.Exec(ctx, `
UPDATE cloud_blocks
SET storage_kind = 'missing',
    pack_id = NULL,
    pack_offset_bytes = NULL,
    stored_size_bytes = NULL
WHERE block_hash = $1;
`, hash)
	return err
}

func (h *Handler) createWritablePackTx(ctx context.Context, tx pgx.Tx) (blockPackRecord, error) {
	relativePath := h.newPackRelativePath()
	var pack blockPackRecord

	err := tx.QueryRow(ctx, `
INSERT INTO cloud_block_packs(
    relative_path,
    state,
    bytes_written,
    block_count,
    created_at,
    updated_at)
VALUES($1, 'active', 0, 0, now(), now())
RETURNING id;
`, relativePath).Scan(&pack.ID)
	if err != nil {
		return blockPackRecord{}, err
	}

	pack.RelativePath = relativePath
	return pack, nil
}

func (h *Handler) updatePackAfterAppendTx(ctx context.Context, tx pgx.Tx, packID int64, newBytesWritten int64) error {
	newState := "active"
	if newBytesWritten >= h.packTargetBytes {
		newState = "sealed"
	}

	_, err := tx.Exec(ctx, `
UPDATE cloud_block_packs
SET bytes_written = $2,
    block_count = block_count + 1,
    state = $3,
    updated_at = now(),
    sealed_at = CASE
        WHEN $3 = 'sealed' THEN COALESCE(sealed_at, now())
        ELSE sealed_at
    END
WHERE id = $1;
`, packID, newBytesWritten, newState)
	return err
}

func (h *Handler) newPackRelativePath() string {
	now := time.Now().UTC()
	random := make([]byte, 4)
	if _, err := rand.Read(random); err != nil {
		return filepath.ToSlash(filepath.Join(
			"packs",
			now.Format("20060102"),
			fmt.Sprintf("pack-%s-fallback.pak", now.Format("150405.000000000")),
		))
	}

	return filepath.ToSlash(filepath.Join(
		"packs",
		now.Format("20060102"),
		fmt.Sprintf("pack-%s-%x.pak", now.Format("150405.000000000"), random),
	))
}

func (h *Handler) blockPackPath(relativePath string) string {
	return filepath.Join(h.blockStoreDir, filepath.FromSlash(relativePath))
}
