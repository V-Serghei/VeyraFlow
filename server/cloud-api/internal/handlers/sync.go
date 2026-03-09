package handlers

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
)

type cloudRepositoryInfo struct {
	RepositoryID             int        `json:"repositoryId"`
	Name                     string     `json:"name"`
	Description              string     `json:"description,omitempty"`
	LatestSnapshotID         *int64     `json:"latestSnapshotId,omitempty"`
	LatestSnapshotCreatedAt  *time.Time `json:"latestSnapshotCreatedAt,omitempty"`
	LatestSnapshotTitle      string     `json:"latestSnapshotTitle,omitempty"`
	LatestSnapshotTrigger    string     `json:"latestSnapshotTrigger,omitempty"`
	LatestSnapshotFileCount  int        `json:"latestSnapshotFileCount,omitempty"`
	LatestSnapshotEntryCount int        `json:"latestSnapshotEntryCount,omitempty"`
}

type pushSnapshotRequest struct {
	Repository   syncRepositoryMeta  `json:"repository"`
	Snapshot     syncSnapshotMeta    `json:"snapshot"`
	Entries      []syncSnapshotEntry `json:"entries"`
	FileVersions []syncFileVersion   `json:"fileVersions"`
}

type pushSnapshotResponse struct {
	Ok                bool     `json:"ok"`
	MissingBlockHashes []string `json:"missingBlockHashes"`
}

type syncRepositoryMeta struct {
	ID          int    `json:"id"`
	Name        string `json:"name"`
	Description string `json:"description"`
}

type syncSnapshotMeta struct {
	ID              int64     `json:"id"`
	Title           string    `json:"title"`
	Trigger         string    `json:"trigger"`
	CreatedAt       time.Time `json:"createdAt"`
	TotalEntries    int       `json:"totalEntries"`
	FileEntries     int       `json:"fileEntries"`
	DirectoryEntries int      `json:"directoryEntries"`
	TotalFileBytes  int64     `json:"totalFileBytes"`
	PayloadSHA256   string    `json:"payloadSha256"`
}

type syncSnapshotEntry struct {
	RelativePath       string    `json:"relativePath"`
	ParentRelativePath string    `json:"parentRelativePath"`
	Name               string    `json:"name"`
	IsDirectory        bool      `json:"isDirectory"`
	Extension          string    `json:"extension"`
	SizeBytes          int64     `json:"sizeBytes"`
	LastWriteUTC       time.Time `json:"lastWriteUtc"`
	ContentHashSHA256  string    `json:"contentHashSha256"`
}

type syncFileVersion struct {
	RelativePath      string         `json:"relativePath"`
	FileVersionID     int64          `json:"fileVersionId"`
	ContentHashSHA256 string         `json:"contentHashSha256"`
	SizeBytes         int64          `json:"sizeBytes"`
	IsDeletionMarker  bool           `json:"isDeletionMarker"`
	CreatedAt         time.Time      `json:"createdAt"`
	Blocks            []syncBlockRef `json:"blocks"`
}

type syncBlockRef struct {
	Sequence        int    `json:"sequence"`
	BlockHash       string `json:"blockHash"`
	LengthBytes     int    `json:"lengthBytes"`
	StoredSizeBytes int64  `json:"storedSizeBytes"`
}

type latestSnapshotResponse struct {
	Ok           bool                `json:"ok"`
	Repository   syncRepositoryMeta  `json:"repository"`
	Snapshot     syncSnapshotMeta    `json:"snapshot"`
	Entries      []syncSnapshotEntry `json:"entries"`
	FileVersions []syncFileVersion   `json:"fileVersions"`
}

func (h *Handler) ListRepositories(w http.ResponseWriter, r *http.Request) {
	auth, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	rows, err := h.db.Query(r.Context(), `
SELECT
    r.external_repository_id,
    r.name,
    COALESCE(r.description, ''),
    s.external_snapshot_id,
    s.created_at,
    COALESCE(s.title, ''),
    COALESCE(s.trigger, ''),
    COALESCE(s.file_entries, 0),
    COALESCE(s.total_entries, 0)
FROM repositories r
LEFT JOIN LATERAL (
    SELECT external_snapshot_id, created_at, title, trigger, file_entries, total_entries
    FROM snapshots s
    WHERE s.repository_id = r.id
      AND EXISTS (
          SELECT 1
          FROM snapshot_file_versions sfv
          WHERE sfv.snapshot_id = s.id)
    ORDER BY s.created_at DESC, s.id DESC
    LIMIT 1
) s ON true
WHERE r.user_id = $1
ORDER BY r.updated_at DESC, r.id DESC;
`, auth.UserID)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "query failed"})
		return
	}
	defer rows.Close()

	result := make([]cloudRepositoryInfo, 0, 16)
	for rows.Next() {
		var info cloudRepositoryInfo
		var latestID *int64
		var latestAt *time.Time
		if scanErr := rows.Scan(
			&info.RepositoryID,
			&info.Name,
			&info.Description,
			&latestID,
			&latestAt,
			&info.LatestSnapshotTitle,
			&info.LatestSnapshotTrigger,
			&info.LatestSnapshotFileCount,
			&info.LatestSnapshotEntryCount,
		); scanErr != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "scan failed"})
			return
		}

		info.LatestSnapshotID = latestID
		info.LatestSnapshotCreatedAt = latestAt
		result = append(result, info)
	}

	writeJSON(w, http.StatusOK, map[string]any{
		"ok":           true,
		"repositories": result,
	})
}

func (h *Handler) PushRepositorySnapshot(w http.ResponseWriter, r *http.Request) {
	auth, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	repositoryID, err := parsePathInt(r, "repositoryId")
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid repository id"})
		return
	}

	var req pushSnapshotRequest
	if err = json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "bad json"})
		return
	}

	if req.Repository.ID > 0 && req.Repository.ID != repositoryID {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "repository id mismatch"})
		return
	}
	if req.Snapshot.ID <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "snapshot id must be positive"})
		return
	}

	repositoryName := strings.TrimSpace(req.Repository.Name)
	if repositoryName == "" {
		repositoryName = fmt.Sprintf("repository-%d", repositoryID)
	}

	if len(req.FileVersions) == 0 && len(req.Entries) > 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "snapshot payload has entries but no file versions"})
		return
	}

	tx, err := h.db.Begin(r.Context())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "transaction begin failed"})
		return
	}
	defer func() { _ = tx.Rollback(r.Context()) }()

	var cloudRepoID int64
	err = tx.QueryRow(r.Context(), `
SELECT id
FROM repositories
WHERE user_id = $1 AND (external_repository_id = $2 OR lower(name) = lower($3))
ORDER BY CASE WHEN external_repository_id = $2 THEN 0 ELSE 1 END
LIMIT 1;
`, auth.UserID, repositoryID, repositoryName).Scan(&cloudRepoID)

	if err != nil {
		if !errors.Is(err, pgx.ErrNoRows) {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "repository lookup failed"})
			return
		}

		err = tx.QueryRow(r.Context(), `
INSERT INTO repositories(user_id, external_repository_id, name, description, created_at, updated_at)
VALUES($1, $2, $3, NULLIF($4, ''), now(), now())
RETURNING id;
`, auth.UserID, repositoryID, repositoryName, req.Repository.Description).Scan(&cloudRepoID)
		if err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "repository create failed"})
			return
		}
	} else {
		if _, err = tx.Exec(r.Context(), `
UPDATE repositories
SET external_repository_id = $2,
    name = $3,
    description = NULLIF($4, ''),
    updated_at = now()
WHERE id = $1;
`, cloudRepoID, repositoryID, repositoryName, req.Repository.Description); err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "repository update failed"})
			return
		}
	}

	var cloudSnapshotID int64
	err = tx.QueryRow(r.Context(), `
INSERT INTO snapshots(
    repository_id,
    external_snapshot_id,
    title,
    trigger,
    created_at,
    total_entries,
    file_entries,
    directory_entries,
    total_file_bytes,
    payload_sha256,
    inserted_at)
VALUES($1, $2, NULLIF($3, ''), $4, $5, $6, $7, $8, $9, NULLIF($10, ''), now())
ON CONFLICT (repository_id, external_snapshot_id)
DO UPDATE SET
    title = EXCLUDED.title,
    trigger = EXCLUDED.trigger,
    created_at = EXCLUDED.created_at,
    total_entries = EXCLUDED.total_entries,
    file_entries = EXCLUDED.file_entries,
    directory_entries = EXCLUDED.directory_entries,
    total_file_bytes = EXCLUDED.total_file_bytes,
    payload_sha256 = EXCLUDED.payload_sha256
RETURNING id;
`,
		cloudRepoID,
		req.Snapshot.ID,
		req.Snapshot.Title,
		req.Snapshot.Trigger,
		req.Snapshot.CreatedAt,
		req.Snapshot.TotalEntries,
		req.Snapshot.FileEntries,
		req.Snapshot.DirectoryEntries,
		req.Snapshot.TotalFileBytes,
		req.Snapshot.PayloadSHA256,
	).Scan(&cloudSnapshotID)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "snapshot upsert failed"})
		return
	}

	if _, err = tx.Exec(r.Context(), `DELETE FROM snapshot_entries WHERE snapshot_id = $1`, cloudSnapshotID); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "delete old entries failed"})
		return
	}
	if _, err = tx.Exec(r.Context(), `DELETE FROM snapshot_file_versions WHERE snapshot_id = $1`, cloudSnapshotID); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "delete old versions failed"})
		return
	}

	for _, entry := range req.Entries {
		if _, err = tx.Exec(r.Context(), `
INSERT INTO snapshot_entries(
    snapshot_id,
    relative_path,
    parent_relative_path,
    name,
    is_directory,
    extension,
    size_bytes,
    last_write_utc,
    content_hash_sha256)
VALUES($1,$2,NULLIF($3,''),$4,$5,NULLIF($6,''),$7,$8,NULLIF($9,''));
`,
			cloudSnapshotID,
			entry.RelativePath,
			entry.ParentRelativePath,
			entry.Name,
			entry.IsDirectory,
			entry.Extension,
			entry.SizeBytes,
			entry.LastWriteUTC,
			entry.ContentHashSHA256,
		); err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "insert entry failed"})
			return
		}
	}

	allHashes := make([]string, 0, 128)
	hashSet := make(map[string]struct{}, 128)

	for _, version := range req.FileVersions {
		blocksJSON, marshalErr := json.Marshal(version.Blocks)
		if marshalErr != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid block payload"})
			return
		}

		if _, err = tx.Exec(r.Context(), `
INSERT INTO snapshot_file_versions(
    snapshot_id,
    relative_path,
    file_version_id,
    content_hash_sha256,
    size_bytes,
    is_deletion_marker,
    created_at,
    blocks_json)
VALUES($1,$2,$3,$4,$5,$6,$7,$8::jsonb);
`,
			cloudSnapshotID,
			version.RelativePath,
			version.FileVersionID,
			version.ContentHashSHA256,
			version.SizeBytes,
			version.IsDeletionMarker,
			version.CreatedAt,
			string(blocksJSON),
		); err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "insert file version failed"})
			return
		}

		for _, block := range version.Blocks {
			hash := strings.TrimSpace(block.BlockHash)
			if hash == "" {
				continue
			}
			if _, exists := hashSet[hash]; exists {
				continue
			}
			hashSet[hash] = struct{}{}
			allHashes = append(allHashes, hash)
		}
	}

	missing := make([]string, 0)
	if len(allHashes) > 0 {
		rows, qErr := tx.Query(r.Context(), `SELECT block_hash FROM cloud_blocks WHERE block_hash = ANY($1)`, allHashes)
		if qErr != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "block lookup failed"})
			return
		}

		existing := make(map[string]struct{}, len(allHashes))
		for rows.Next() {
			var hash string
			if scanErr := rows.Scan(&hash); scanErr != nil {
				rows.Close()
				writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "block lookup scan failed"})
				return
			}
			existing[hash] = struct{}{}
		}
		rows.Close()

		for _, hash := range allHashes {
			if _, okExists := existing[hash]; !okExists {
				missing = append(missing, hash)
			}
		}
		sort.Strings(missing)
	}

	if err = tx.Commit(r.Context()); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "transaction commit failed"})
		return
	}

	writeJSON(w, http.StatusOK, pushSnapshotResponse{
		Ok:                true,
		MissingBlockHashes: missing,
	})
}

func (h *Handler) GetLatestRepositorySnapshot(w http.ResponseWriter, r *http.Request) {
	auth, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	repositoryID, err := parsePathInt(r, "repositoryId")
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid repository id"})
		return
	}

	var cloudRepoID int64
	var name string
	var description string
	err = h.db.QueryRow(r.Context(), `
SELECT id, name, COALESCE(description, '')
FROM repositories
WHERE user_id = $1 AND external_repository_id = $2
LIMIT 1;
`, auth.UserID, repositoryID).Scan(&cloudRepoID, &name, &description)
	if err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"ok": false, "message": "repository not found"})
		return
	}

	var snapshotRowID int64
	var snapshot syncSnapshotMeta
	err = h.db.QueryRow(r.Context(), `
SELECT
    id,
    external_snapshot_id,
    COALESCE(title, ''),
    trigger,
    created_at,
    total_entries,
    file_entries,
    directory_entries,
    total_file_bytes,
    COALESCE(payload_sha256, '')
FROM snapshots
WHERE repository_id = $1
  AND EXISTS (
      SELECT 1
      FROM snapshot_file_versions sfv
      WHERE sfv.snapshot_id = snapshots.id)
ORDER BY created_at DESC, id DESC
LIMIT 1;
`, cloudRepoID).Scan(
		&snapshotRowID,
		&snapshot.ID,
		&snapshot.Title,
		&snapshot.Trigger,
		&snapshot.CreatedAt,
		&snapshot.TotalEntries,
		&snapshot.FileEntries,
		&snapshot.DirectoryEntries,
		&snapshot.TotalFileBytes,
		&snapshot.PayloadSHA256,
	)
	if err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"ok": false, "message": "snapshot not found"})
		return
	}

	entryRows, err := h.db.Query(r.Context(), `
SELECT
    relative_path,
    COALESCE(parent_relative_path, ''),
    name,
    is_directory,
    COALESCE(extension, ''),
    size_bytes,
    last_write_utc,
    COALESCE(content_hash_sha256, '')
FROM snapshot_entries
WHERE snapshot_id = $1
ORDER BY relative_path;
`, snapshotRowID)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "entries query failed"})
		return
	}
	defer entryRows.Close()

	entries := make([]syncSnapshotEntry, 0, 1024)
	for entryRows.Next() {
		var entry syncSnapshotEntry
		if scanErr := entryRows.Scan(
			&entry.RelativePath,
			&entry.ParentRelativePath,
			&entry.Name,
			&entry.IsDirectory,
			&entry.Extension,
			&entry.SizeBytes,
			&entry.LastWriteUTC,
			&entry.ContentHashSHA256,
		); scanErr != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "entry scan failed"})
			return
		}
		entries = append(entries, entry)
	}

	versionRows, err := h.db.Query(r.Context(), `
SELECT
    relative_path,
    file_version_id,
    content_hash_sha256,
    size_bytes,
    is_deletion_marker,
    created_at,
    blocks_json
FROM snapshot_file_versions
WHERE snapshot_id = $1
ORDER BY relative_path, file_version_id;
`, snapshotRowID)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "versions query failed"})
		return
	}
	defer versionRows.Close()

	versions := make([]syncFileVersion, 0, 1024)
	for versionRows.Next() {
		var version syncFileVersion
		var blocksJSON []byte
		if scanErr := versionRows.Scan(
			&version.RelativePath,
			&version.FileVersionID,
			&version.ContentHashSHA256,
			&version.SizeBytes,
			&version.IsDeletionMarker,
			&version.CreatedAt,
			&blocksJSON,
		); scanErr != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "version scan failed"})
			return
		}

		if len(blocksJSON) > 0 {
			if err = json.Unmarshal(blocksJSON, &version.Blocks); err != nil {
				writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "invalid stored blocks payload"})
				return
			}
		}

		versions = append(versions, version)
	}

	writeJSON(w, http.StatusOK, latestSnapshotResponse{
		Ok: true,
		Repository: syncRepositoryMeta{
			ID:          repositoryID,
			Name:        name,
			Description: description,
		},
		Snapshot:     snapshot,
		Entries:      entries,
		FileVersions: versions,
	})
}

func (h *Handler) HeadBlock(w http.ResponseWriter, r *http.Request) {
	_, ok := getAuthUser(r)
	if !ok {
		w.WriteHeader(http.StatusUnauthorized)
		return
	}

	hash := strings.TrimSpace(r.PathValue("blockHash"))
	if hash == "" || !isValidBlockHash(hash) {
		w.WriteHeader(http.StatusBadRequest)
		return
	}

	var exists bool
	if err := h.db.QueryRow(r.Context(), `SELECT EXISTS(SELECT 1 FROM cloud_blocks WHERE block_hash = $1)`, hash).Scan(&exists); err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		return
	}
	if !exists {
		w.WriteHeader(http.StatusNotFound)
		return
	}

	w.WriteHeader(http.StatusOK)
}

func (h *Handler) PutBlock(w http.ResponseWriter, r *http.Request) {
	_, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	hash := strings.TrimSpace(r.PathValue("blockHash"))
	if hash == "" || !isValidBlockHash(hash) {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid block hash"})
		return
	}

	reader := http.MaxBytesReader(w, r.Body, 32*1024*1024)
	body, err := io.ReadAll(reader)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "failed to read request body"})
		return
	}

	if strings.HasPrefix(strings.ToLower(hash), "sha256-") {
		expected := strings.TrimPrefix(strings.ToLower(hash), "sha256-")
		sum := sha256.Sum256(body)
		actual := strings.ToLower(hex.EncodeToString(sum[:]))
		if expected != actual {
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "sha256 mismatch"})
			return
		}
	}

	path := h.blockPath(hash)
	if err = os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to prepare block directory"})
		return
	}

	if err = os.WriteFile(path, body, 0o644); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to persist block"})
		return
	}

	if _, err = h.db.Exec(r.Context(), `
INSERT INTO cloud_blocks(block_hash, length_bytes, created_at)
VALUES($1, $2, now())
ON CONFLICT (block_hash)
DO UPDATE SET length_bytes = EXCLUDED.length_bytes;
`, hash, len(body)); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to persist block metadata"})
		return
	}

	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "size_bytes": len(body)})
}

func (h *Handler) GetBlock(w http.ResponseWriter, r *http.Request) {
	_, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	hash := strings.TrimSpace(r.PathValue("blockHash"))
	if hash == "" || !isValidBlockHash(hash) {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid block hash"})
		return
	}

	path := h.blockPath(hash)
	body, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			writeJSON(w, http.StatusNotFound, map[string]any{"ok": false, "message": "block not found"})
			return
		}
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to read block"})
		return
	}

	w.Header().Set("Content-Type", "application/octet-stream")
	w.Header().Set("Content-Length", fmt.Sprintf("%d", len(body)))
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(body)
}

func (h *Handler) blockPath(hash string) string {
	safe := strings.ToLower(strings.TrimSpace(hash))
	safe = strings.ReplaceAll(safe, "/", "_")
	safe = strings.ReplaceAll(safe, "\\", "_")

	p1 := "00"
	p2 := "00"
	if len(safe) >= 2 {
		p1 = safe[:2]
	}
	if len(safe) >= 4 {
		p2 = safe[2:4]
	}

	return filepath.Join(h.blockStoreDir, p1, p2, safe+".bin")
}

func isValidBlockHash(hash string) bool {
	if hash == "" || len(hash) > 256 {
		return false
	}
	for _, r := range hash {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') {
			continue
		}
		switch r {
		case '-', '_', ':', '.':
			continue
		default:
			return false
		}
	}
	return true
}

