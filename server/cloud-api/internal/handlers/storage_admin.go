package handlers

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
)

type storageMetricsResponse struct {
	Ok         bool                   `json:"ok"`
	Summary    storageSummaryMetrics  `json:"summary"`
	Blocks     storageBlockMetrics    `json:"blocks"`
	Packs      storagePackMetrics     `json:"packs"`
	Filesystem storageFilesystemStats `json:"filesystem"`
}

type storageSummaryMetrics struct {
	LogicalBlockCount         int64 `json:"logicalBlockCount"`
	LogicalBytes              int64 `json:"logicalBytes"`
	PhysicalObjectCount       int64 `json:"physicalObjectCount"`
	PhysicalPayloadBytes      int64 `json:"physicalPayloadBytes"`
	MissingBlockCount         int64 `json:"missingBlockCount"`
	ReducedObjectCount        int64 `json:"reducedObjectCount"`
	ReducedObjectPercentFloor int64 `json:"reducedObjectPercentFloor"`
}

type storageBlockMetrics struct {
	TotalBlocks      int64 `json:"totalBlocks"`
	PackedBlocks     int64 `json:"packedBlocks"`
	LooseBlocks      int64 `json:"looseBlocks"`
	MissingBlocks    int64 `json:"missingBlocks"`
	LogicalBytes     int64 `json:"logicalBytes"`
	PackedBytes      int64 `json:"packedBytes"`
	LooseBytes       int64 `json:"looseBytes"`
	MissingBytes     int64 `json:"missingBytes"`
}

type storagePackMetrics struct {
	TotalPacks      int64 `json:"totalPacks"`
	ActivePacks     int64 `json:"activePacks"`
	SealedPacks     int64 `json:"sealedPacks"`
	BytesWritten    int64 `json:"bytesWritten"`
	PackedBlockRefs int64 `json:"packedBlockRefs"`
}

type storageFilesystemStats struct {
	PackFileCount    int64 `json:"packFileCount"`
	LooseFileCount   int64 `json:"looseFileCount"`
	OtherFileCount   int64 `json:"otherFileCount"`
	PackFileBytes    int64 `json:"packFileBytes"`
	LooseFileBytes   int64 `json:"looseFileBytes"`
	OtherFileBytes   int64 `json:"otherFileBytes"`
	TotalPhysicalBytes int64 `json:"totalPhysicalBytes"`
}

type storageRepairRequest struct {
	ScanLimit    int `json:"scanLimit"`
	CompactLimit int `json:"compactLimit"`
}

type storageRepairResponse struct {
	Ok      bool               `json:"ok"`
	Repair  storageRepairStats `json:"repair"`
	Metrics storageMetricsResponse `json:"metrics"`
}

type storageRepairStats struct {
	Scanned         int `json:"scanned"`
	MissingMarked   int `json:"missingMarked"`
	BrokenLooseRefs int `json:"brokenLooseRefs"`
	BrokenPackRefs  int `json:"brokenPackRefs"`
	Compacted       int `json:"compacted"`
}

type repairScanRecord struct {
	BlockHash         string
	StorageKind       string
	PackRelativePath  string
	PackOffsetBytes   int64
	StoredSizeBytes   int64
	LengthBytes       int64
}

func (h *Handler) GetStorageMetrics(w http.ResponseWriter, r *http.Request) {
	_, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	metrics, err := h.collectStorageMetrics(r.Context())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to collect storage metrics"})
		return
	}

	writeJSON(w, http.StatusOK, metrics)
}

func (h *Handler) RepairStorage(w http.ResponseWriter, r *http.Request) {
	_, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	req := storageRepairRequest{
		ScanLimit:    512,
		CompactLimit: 128,
	}
	if r.Body != nil {
		defer r.Body.Close()
		decoder := json.NewDecoder(r.Body)
		decoder.DisallowUnknownFields()
		if err := decoder.Decode(&req); err != nil && !errors.Is(err, io.EOF) {
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "bad json"})
			return
		}
	}

	stats, err := h.RepairBlockStorage(r.Context(), req.ScanLimit, req.CompactLimit)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "storage repair failed"})
		return
	}

	metrics, err := h.collectStorageMetrics(r.Context())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "repair completed but metrics collection failed"})
		return
	}

	writeJSON(w, http.StatusOK, storageRepairResponse{
		Ok:      true,
		Repair:  stats,
		Metrics: metrics,
	})
}

func (h *Handler) RepairBlockStorage(ctx context.Context, scanLimit, compactLimit int) (storageRepairStats, error) {
	if scanLimit <= 0 {
		scanLimit = 512
	}
	if compactLimit <= 0 {
		compactLimit = 128
	}

	stats, err := h.repairStorageReferences(ctx, scanLimit)
	if err != nil {
		return stats, err
	}

	compacted, err := h.CompactLooseBlocks(ctx, compactLimit)
	stats.Compacted = compacted
	if err != nil {
		return stats, err
	}

	return stats, nil
}

func (h *Handler) repairStorageReferences(ctx context.Context, scanLimit int) (storageRepairStats, error) {
	rows, err := h.db.Query(ctx, `
SELECT
    cb.block_hash,
    COALESCE(NULLIF(cb.storage_kind, ''), 'loose') AS storage_kind,
    COALESCE(cbp.relative_path, ''),
    COALESCE(cb.pack_offset_bytes, 0),
    COALESCE(cb.stored_size_bytes, cb.length_bytes),
    cb.length_bytes
FROM cloud_blocks cb
LEFT JOIN cloud_block_packs cbp ON cb.pack_id = cbp.id
WHERE COALESCE(NULLIF(cb.storage_kind, ''), 'loose') IN ('loose', 'pack')
ORDER BY cb.created_at ASC, cb.block_hash ASC
LIMIT $1;
`, scanLimit)
	if err != nil {
		return storageRepairStats{}, err
	}
	defer rows.Close()

	records := make([]repairScanRecord, 0, scanLimit)
	for rows.Next() {
		var item repairScanRecord
		if scanErr := rows.Scan(
			&item.BlockHash,
			&item.StorageKind,
			&item.PackRelativePath,
			&item.PackOffsetBytes,
			&item.StoredSizeBytes,
			&item.LengthBytes,
		); scanErr != nil {
			return storageRepairStats{}, scanErr
		}
		records = append(records, item)
	}

	stats := storageRepairStats{}
	for _, item := range records {
		stats.Scanned++

		switch item.StorageKind {
		case "loose":
			if _, statErr := os.Stat(h.blockPath(item.BlockHash)); statErr != nil {
				if os.IsNotExist(statErr) {
					if markErr := h.markBlockMissing(ctx, item.BlockHash); markErr != nil {
						return stats, markErr
					}
					stats.MissingMarked++
					stats.BrokenLooseRefs++
					continue
				}
				return stats, statErr
			}
		case "pack":
			storedSize := item.StoredSizeBytes
			if storedSize <= 0 {
				storedSize = item.LengthBytes
			}
			if strings.TrimSpace(item.PackRelativePath) == "" {
				if markErr := h.markBlockMissing(ctx, item.BlockHash); markErr != nil {
					return stats, markErr
				}
				stats.MissingMarked++
				stats.BrokenPackRefs++
				continue
			}

			packPath := h.blockPackPath(item.PackRelativePath)
			packInfo, statErr := os.Stat(packPath)
			if statErr != nil {
				if os.IsNotExist(statErr) {
					if markErr := h.markBlockMissing(ctx, item.BlockHash); markErr != nil {
						return stats, markErr
					}
					stats.MissingMarked++
					stats.BrokenPackRefs++
					continue
				}
				return stats, statErr
			}
			if packInfo.Size() < item.PackOffsetBytes+storedSize {
				if markErr := h.markBlockMissing(ctx, item.BlockHash); markErr != nil {
					return stats, markErr
				}
				stats.MissingMarked++
				stats.BrokenPackRefs++
			}
		}
	}

	return stats, nil
}

func (h *Handler) collectStorageMetrics(ctx context.Context) (storageMetricsResponse, error) {
	response := storageMetricsResponse{Ok: true}

	err := h.db.QueryRow(ctx, `
SELECT
    COUNT(*)::bigint AS total_blocks,
    COUNT(*) FILTER (WHERE COALESCE(NULLIF(storage_kind, ''), 'loose') = 'pack')::bigint AS packed_blocks,
    COUNT(*) FILTER (WHERE COALESCE(NULLIF(storage_kind, ''), 'loose') = 'loose')::bigint AS loose_blocks,
    COUNT(*) FILTER (WHERE COALESCE(NULLIF(storage_kind, ''), 'loose') = 'missing')::bigint AS missing_blocks,
    COALESCE(SUM(length_bytes)::bigint, 0) AS logical_bytes,
    COALESCE(SUM(CASE WHEN COALESCE(NULLIF(storage_kind, ''), 'loose') = 'pack' THEN COALESCE(stored_size_bytes, length_bytes) ELSE 0 END)::bigint, 0) AS packed_bytes,
    COALESCE(SUM(CASE WHEN COALESCE(NULLIF(storage_kind, ''), 'loose') = 'loose' THEN length_bytes ELSE 0 END)::bigint, 0) AS loose_bytes,
    COALESCE(SUM(CASE WHEN COALESCE(NULLIF(storage_kind, ''), 'loose') = 'missing' THEN length_bytes ELSE 0 END)::bigint, 0) AS missing_bytes
FROM cloud_blocks;
`).Scan(
		&response.Blocks.TotalBlocks,
		&response.Blocks.PackedBlocks,
		&response.Blocks.LooseBlocks,
		&response.Blocks.MissingBlocks,
		&response.Blocks.LogicalBytes,
		&response.Blocks.PackedBytes,
		&response.Blocks.LooseBytes,
		&response.Blocks.MissingBytes,
	)
	if err != nil {
		return storageMetricsResponse{}, err
	}

	err = h.db.QueryRow(ctx, `
SELECT
    COUNT(*)::bigint AS total_packs,
    COUNT(*) FILTER (WHERE state = 'active')::bigint AS active_packs,
    COUNT(*) FILTER (WHERE state = 'sealed')::bigint AS sealed_packs,
    COALESCE(SUM(bytes_written)::bigint, 0) AS bytes_written,
    COALESCE(SUM(block_count)::bigint, 0) AS packed_block_refs
FROM cloud_block_packs;
`).Scan(
		&response.Packs.TotalPacks,
		&response.Packs.ActivePacks,
		&response.Packs.SealedPacks,
		&response.Packs.BytesWritten,
		&response.Packs.PackedBlockRefs,
	)
	if err != nil {
		return storageMetricsResponse{}, err
	}

	filesystemStats, err := h.collectFilesystemStats()
	if err != nil {
		return storageMetricsResponse{}, err
	}
	response.Filesystem = filesystemStats

	response.Summary.LogicalBlockCount = response.Blocks.TotalBlocks
	response.Summary.LogicalBytes = response.Blocks.LogicalBytes
	response.Summary.MissingBlockCount = response.Blocks.MissingBlocks
	response.Summary.PhysicalObjectCount = response.Packs.TotalPacks + response.Blocks.LooseBlocks
	response.Summary.PhysicalPayloadBytes = response.Packs.BytesWritten + response.Blocks.LooseBytes
	if response.Blocks.TotalBlocks > response.Summary.PhysicalObjectCount {
		response.Summary.ReducedObjectCount = response.Blocks.TotalBlocks - response.Summary.PhysicalObjectCount
	}
	if response.Blocks.TotalBlocks > 0 {
		response.Summary.ReducedObjectPercentFloor = (response.Summary.ReducedObjectCount * 100) / response.Blocks.TotalBlocks
	}

	return response, nil
}

func (h *Handler) collectFilesystemStats() (storageFilesystemStats, error) {
	stats := storageFilesystemStats{}
	walkErr := filepath.Walk(h.blockStoreDir, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if info == nil || info.IsDir() {
			return nil
		}

		ext := strings.ToLower(filepath.Ext(info.Name()))
		switch ext {
		case ".pak":
			stats.PackFileCount++
			stats.PackFileBytes += info.Size()
		case ".bin":
			stats.LooseFileCount++
			stats.LooseFileBytes += info.Size()
		default:
			stats.OtherFileCount++
			stats.OtherFileBytes += info.Size()
		}
		return nil
	})
	if walkErr != nil && !os.IsNotExist(walkErr) {
		return storageFilesystemStats{}, walkErr
	}

	stats.TotalPhysicalBytes = stats.PackFileBytes + stats.LooseFileBytes + stats.OtherFileBytes
	return stats, nil
}
