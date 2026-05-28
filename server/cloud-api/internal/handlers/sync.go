package handlers

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"database/sql/driver"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	stdhash "hash"
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
	Ok                 bool     `json:"ok"`
	MissingBlockHashes []string `json:"missingBlockHashes"`
}

type putBlocksBatchResponse struct {
	Ok            bool `json:"ok"`
	StoredBlocks  int  `json:"storedBlocks"`
	SkippedBlocks int  `json:"skippedBlocks"`
}

type syncRepositoryMeta struct {
	ID          int    `json:"id"`
	Name        string `json:"name"`
	Description string `json:"description"`
}

type syncSnapshotMeta struct {
	ID               int64         `json:"id"`
	Title            string        `json:"title"`
	Trigger          string        `json:"trigger"`
	CreatedAt        syncTimestamp `json:"createdAt"`
	TotalEntries     int           `json:"totalEntries"`
	FileEntries      int           `json:"fileEntries"`
	DirectoryEntries int           `json:"directoryEntries"`
	TotalFileBytes   int64         `json:"totalFileBytes"`
	PayloadSHA256    string        `json:"payloadSha256"`
}

type syncSnapshotEntry struct {
	RelativePath       string        `json:"relativePath"`
	ParentRelativePath string        `json:"parentRelativePath"`
	Name               string        `json:"name"`
	IsDirectory        bool          `json:"isDirectory"`
	Extension          string        `json:"extension"`
	SizeBytes          int64         `json:"sizeBytes"`
	LastWriteUTC       syncTimestamp `json:"lastWriteUtc"`
	ContentHashSHA256  string        `json:"contentHashSha256"`
}

type syncFileVersion struct {
	RelativePath      string         `json:"relativePath"`
	FileVersionID     int64          `json:"fileVersionId"`
	ContentHashSHA256 string         `json:"contentHashSha256"`
	SizeBytes         int64          `json:"sizeBytes"`
	IsDeletionMarker  bool           `json:"isDeletionMarker"`
	CreatedAt         syncTimestamp  `json:"createdAt"`
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

type repositorySnapshotsResponse struct {
	Ok        bool                     `json:"ok"`
	Snapshots []latestSnapshotResponse `json:"snapshots"`
}

const (
	syncProtocolHeader    = "X-Veyra-Sync-Protocol"
	supportedSyncProtocol = "1"
	idempotencyKeyHeader  = "X-Idempotency-Key"
)

type syncIdempotencyReplay struct {
	StatusCode int
	Payload    []byte
}

type syncTimestamp struct {
	time.Time
}

func (t *syncTimestamp) UnmarshalJSON(data []byte) error {
	raw := strings.TrimSpace(string(data))
	if raw == "null" || raw == `""` || raw == "" {
		t.Time = time.Time{}
		return nil
	}

	parsed, err := parseFlexibleTimestamp(strings.Trim(raw, `"`))
	if err != nil {
		return err
	}

	t.Time = parsed.UTC()
	return nil
}

func (t syncTimestamp) MarshalJSON() ([]byte, error) {
	if t.Time.IsZero() {
		return []byte(`"0001-01-01T00:00:00Z"`), nil
	}

	return json.Marshal(t.Time.UTC().Format(time.RFC3339Nano))
}

func (t *syncTimestamp) Scan(value any) error {
	switch v := value.(type) {
	case nil:
		t.Time = time.Time{}
		return nil
	case time.Time:
		t.Time = v.UTC()
		return nil
	case string:
		parsed, err := parseFlexibleTimestamp(v)
		if err != nil {
			return err
		}
		t.Time = parsed.UTC()
		return nil
	case []byte:
		parsed, err := parseFlexibleTimestamp(string(v))
		if err != nil {
			return err
		}
		t.Time = parsed.UTC()
		return nil
	default:
		return fmt.Errorf("unsupported syncTimestamp scan type %T", value)
	}
}

func (t syncTimestamp) Value() (driver.Value, error) {
	if t.Time.IsZero() {
		return nil, nil
	}

	return t.Time.UTC(), nil
}

func parseFlexibleTimestamp(raw string) (time.Time, error) {
	value := strings.TrimSpace(raw)
	if value == "" {
		return time.Time{}, nil
	}

	layouts := []struct {
		layout string
		utc    bool
	}{
		{layout: time.RFC3339Nano, utc: false},
		{layout: "2006-01-02T15:04:05.999999999", utc: true},
		{layout: "2006-01-02T15:04:05", utc: true},
	}

	var lastErr error
	for _, candidate := range layouts {
		var (
			parsed time.Time
			err    error
		)
		if candidate.utc {
			parsed, err = time.ParseInLocation(candidate.layout, value, time.UTC)
		} else {
			parsed, err = time.Parse(candidate.layout, value)
		}
		if err == nil {
			return parsed.UTC(), nil
		}
		lastErr = err
	}

	return time.Time{}, lastErr
}

func (h *Handler) ListRepositories(w http.ResponseWriter, r *http.Request) {
	if !ensureSyncProtocol(w, r) {
		return
	}

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

func (h *Handler) DeleteRepository(w http.ResponseWriter, r *http.Request) {
	if !ensureSyncProtocol(w, r) {
		return
	}

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

	tx, err := h.db.Begin(r.Context())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "transaction begin failed"})
		return
	}
	defer tx.Rollback(r.Context())

	var cloudRepoID int64
	var snapshotCount int
	err = tx.QueryRow(r.Context(), `
SELECT r.id, COUNT(s.id)
FROM repositories r
LEFT JOIN snapshots s ON s.repository_id = r.id
WHERE r.user_id = $1 AND r.external_repository_id = $2
GROUP BY r.id
LIMIT 1;
`, auth.UserID, repositoryID).Scan(&cloudRepoID, &snapshotCount)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			writeJSON(w, http.StatusNotFound, map[string]any{"ok": false, "message": "repository not found"})
			return
		}

		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "repository lookup failed"})
		return
	}

	if _, err = tx.Exec(r.Context(), `
DELETE FROM snapshot_entries
WHERE snapshot_id IN (SELECT id FROM snapshots WHERE repository_id = $1);
`, cloudRepoID); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "delete entries failed"})
		return
	}

	if _, err = tx.Exec(r.Context(), `
DELETE FROM snapshot_file_versions
WHERE snapshot_id IN (SELECT id FROM snapshots WHERE repository_id = $1);
`, cloudRepoID); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "delete versions failed"})
		return
	}

	if _, err = tx.Exec(r.Context(), `DELETE FROM snapshots WHERE repository_id = $1;`, cloudRepoID); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "delete snapshots failed"})
		return
	}

	if _, err = tx.Exec(r.Context(), `DELETE FROM repositories WHERE id = $1 AND user_id = $2;`, cloudRepoID, auth.UserID); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "delete repository failed"})
		return
	}

	if err = tx.Commit(r.Context()); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "transaction commit failed"})
		return
	}

	logHTTPRequestEvent(r, "warning", "delete_repository", "cloud repository metadata deleted",
		"user_id="+fmt.Sprintf("%d", auth.UserID),
		"repository_id="+fmt.Sprintf("%d", repositoryID),
		"snapshots="+fmt.Sprintf("%d", snapshotCount))
	writeJSON(w, http.StatusOK, map[string]any{
		"ok":               true,
		"deletedSnapshots": snapshotCount,
	})
}

func (h *Handler) PushRepositorySnapshot(w http.ResponseWriter, r *http.Request) {
	if !ensureSyncProtocol(w, r) {
		return
	}

	auth, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	repositoryID, err := parsePathInt(r, "repositoryId")
	if err != nil {
		logHTTPRequestEvent(r, "warning", "push_snapshot", "invalid repository id")
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid repository id"})
		return
	}

	idempotencyKey := normalizeIdempotencyKey(r.Header.Get(idempotencyKeyHeader))
	if idempotencyKey == "" {
		logHTTPRequestEvent(r, "warning", "push_snapshot", "missing idempotency key",
			"repository_id="+fmt.Sprintf("%d", repositoryID))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing idempotency key"})
		return
	}

	reader := http.MaxBytesReader(w, r.Body, h.maxPushPayloadBytes)
	if err = os.MkdirAll(h.blockStoreDir, 0o755); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to prepare request staging directory"})
		return
	}

	stagedBody, err := os.CreateTemp(h.blockStoreDir, "push-snapshot-*.json")
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to allocate request staging file"})
		return
	}
	stagedBodyPath := stagedBody.Name()
	defer func() {
		_ = stagedBody.Close()
		_ = os.Remove(stagedBodyPath)
	}()

	requestHasher := sha256.New()
	written, readErr := io.Copy(io.MultiWriter(stagedBody, requestHasher), reader)
	if readErr != nil {
		logHTTPRequestEvent(r, "warning", "push_snapshot", "failed to read request body",
			"repository_id="+fmt.Sprintf("%d", repositoryID),
			"idempotency_key="+quoteLogValue(idempotencyKey),
			"written_bytes="+fmt.Sprintf("%d", written),
			"max_payload_bytes="+fmt.Sprintf("%d", h.maxPushPayloadBytes),
			"error="+quoteLogValue(readErr.Error()))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "failed to read request body"})
		return
	}
	if written <= 0 {
		logHTTPRequestEvent(r, "warning", "push_snapshot", "request body is empty",
			"repository_id="+fmt.Sprintf("%d", repositoryID),
			"idempotency_key="+quoteLogValue(idempotencyKey))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "request body is empty"})
		return
	}
	if err = stagedBody.Sync(); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to stage request body"})
		return
	}
	if _, err = stagedBody.Seek(0, io.SeekStart); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to rewind request body"})
		return
	}

	requestSHA := strings.ToLower(hex.EncodeToString(requestHasher.Sum(nil)))

	var req pushSnapshotRequest
	decoder := json.NewDecoder(stagedBody)
	if err = decoder.Decode(&req); err != nil {
		logHTTPRequestEvent(r, "warning", "push_snapshot", "bad json payload",
			"repository_id="+fmt.Sprintf("%d", repositoryID),
			"idempotency_key="+quoteLogValue(idempotencyKey),
			"request_sha="+quoteLogValue(requestSHA),
			"written_bytes="+fmt.Sprintf("%d", written),
			"error="+quoteLogValue(err.Error()))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "bad json"})
		return
	}
	var trailing json.RawMessage
	if err = decoder.Decode(&trailing); err != io.EOF {
		logHTTPRequestEvent(r, "warning", "push_snapshot", "bad json trailing payload",
			"repository_id="+fmt.Sprintf("%d", repositoryID),
			"idempotency_key="+quoteLogValue(idempotencyKey),
			"request_sha="+quoteLogValue(requestSHA),
			"error="+quoteLogValue(err.Error()))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "bad json"})
		return
	}

	if req.Repository.ID > 0 && req.Repository.ID != repositoryID {
		logHTTPRequestEvent(r, "warning", "push_snapshot", "repository id mismatch",
			"repository_id="+fmt.Sprintf("%d", repositoryID),
			"payload_repository_id="+fmt.Sprintf("%d", req.Repository.ID),
			"snapshot_id="+fmt.Sprintf("%d", req.Snapshot.ID),
			"idempotency_key="+quoteLogValue(idempotencyKey))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "repository id mismatch"})
		return
	}
	if req.Snapshot.ID <= 0 {
		logHTTPRequestEvent(r, "warning", "push_snapshot", "snapshot id must be positive",
			"repository_id="+fmt.Sprintf("%d", repositoryID),
			"snapshot_id="+fmt.Sprintf("%d", req.Snapshot.ID),
			"idempotency_key="+quoteLogValue(idempotencyKey))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "snapshot id must be positive"})
		return
	}

	repositoryName := strings.TrimSpace(req.Repository.Name)
	if repositoryName == "" {
		repositoryName = fmt.Sprintf("repository-%d", repositoryID)
	}

	if len(req.FileVersions) == 0 && len(req.Entries) > 0 {
		logHTTPRequestEvent(r, "warning", "push_snapshot", "snapshot has entries but no file versions",
			"repository_id="+fmt.Sprintf("%d", repositoryID),
			"snapshot_id="+fmt.Sprintf("%d", req.Snapshot.ID),
			"entries="+fmt.Sprintf("%d", len(req.Entries)),
			"file_versions=0",
			"idempotency_key="+quoteLogValue(idempotencyKey))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "snapshot payload has entries but no file versions"})
		return
	}

	tx, err := h.db.Begin(r.Context())
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "transaction begin failed"})
		return
	}
	defer func() { _ = tx.Rollback(r.Context()) }()

	idempotencyID, replay, claimErr := claimSyncIdempotencyKey(r.Context(), tx, auth.UserID, repositoryID, idempotencyKey, requestSHA)
	if claimErr != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "idempotency check failed"})
		return
	}
	if replay != nil {
		logHTTPRequestEvent(r, "information", "push_snapshot", "idempotency replay served",
			"repository_id="+fmt.Sprintf("%d", repositoryID),
			"snapshot_id="+fmt.Sprintf("%d", req.Snapshot.ID),
			"idempotency_key="+quoteLogValue(idempotencyKey),
			"status_code="+fmt.Sprintf("%d", replay.StatusCode),
			"request_sha="+quoteLogValue(requestSHA))
		if commitErr := tx.Commit(r.Context()); commitErr != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "idempotency replay commit failed"})
			return
		}
		writeJSONBytes(w, replay.StatusCode, replay.Payload)
		return
	}

	var cloudRepoID int64
	err = tx.QueryRow(r.Context(), `
SELECT id
FROM repositories
WHERE user_id = $1
  AND external_repository_id = $2
LIMIT 1;
`, auth.UserID, repositoryID).Scan(&cloudRepoID)

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
			logHTTPRequestEvent(r, "warning", "push_snapshot", "invalid block payload",
				"repository_id="+fmt.Sprintf("%d", repositoryID),
				"snapshot_id="+fmt.Sprintf("%d", req.Snapshot.ID),
				"relative_path="+quoteLogValue(version.RelativePath),
				"file_version_id="+fmt.Sprintf("%d", version.FileVersionID),
				"blocks="+fmt.Sprintf("%d", len(version.Blocks)),
				"error="+quoteLogValue(marshalErr.Error()))
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
		rows, qErr := tx.Query(r.Context(), `
SELECT block_hash
FROM cloud_blocks
WHERE block_hash = ANY($1)
  AND COALESCE(NULLIF(storage_kind, ''), 'loose') <> 'missing'`, allHashes)
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

	response := pushSnapshotResponse{
		Ok:                 true,
		MissingBlockHashes: missing,
	}

	responsePayload, marshalErr := json.Marshal(response)
	if marshalErr != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "response serialization failed"})
		return
	}

	if _, err = tx.Exec(r.Context(), `
UPDATE sync_idempotency_keys
SET state = 'completed',
    status_code = $2,
    response_json = $3::jsonb,
    updated_at = now()
WHERE id = $1;
`, idempotencyID, http.StatusOK, string(responsePayload)); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "idempotency state update failed"})
		return
	}

	if err = tx.Commit(r.Context()); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "transaction commit failed"})
		return
	}

	logHTTPRequestEvent(r, "information", "push_snapshot", "snapshot accepted",
		"user_id="+fmt.Sprintf("%d", auth.UserID),
		"repository_id="+fmt.Sprintf("%d", repositoryID),
		"snapshot_id="+fmt.Sprintf("%d", req.Snapshot.ID),
		"idempotency_key="+quoteLogValue(idempotencyKey),
		"request_sha="+quoteLogValue(requestSHA),
		"entries="+fmt.Sprintf("%d", len(req.Entries)),
		"file_versions="+fmt.Sprintf("%d", len(req.FileVersions)),
		"missing_blocks="+fmt.Sprintf("%d", len(missing)),
		"written_bytes="+fmt.Sprintf("%d", written))
	writeJSONBytes(w, http.StatusOK, responsePayload)
}

func (h *Handler) GetLatestRepositorySnapshot(w http.ResponseWriter, r *http.Request) {
	if !ensureSyncProtocol(w, r) {
		return
	}

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

func (h *Handler) ListRepositorySnapshots(w http.ResponseWriter, r *http.Request) {
	if !ensureSyncProtocol(w, r) {
		return
	}

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

	snapshotRows, err := h.db.Query(r.Context(), `
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
ORDER BY created_at ASC, id ASC;
`, cloudRepoID)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "snapshots query failed"})
		return
	}
	defer snapshotRows.Close()

	type snapshotHeader struct {
		rowID    int64
		snapshot syncSnapshotMeta
	}
	headers := make([]snapshotHeader, 0, 16)
	for snapshotRows.Next() {
		var header snapshotHeader
		if scanErr := snapshotRows.Scan(
			&header.rowID,
			&header.snapshot.ID,
			&header.snapshot.Title,
			&header.snapshot.Trigger,
			&header.snapshot.CreatedAt,
			&header.snapshot.TotalEntries,
			&header.snapshot.FileEntries,
			&header.snapshot.DirectoryEntries,
			&header.snapshot.TotalFileBytes,
			&header.snapshot.PayloadSHA256,
		); scanErr != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "snapshot scan failed"})
			return
		}
		headers = append(headers, header)
	}

	snapshots := make([]latestSnapshotResponse, 0, len(headers))
	for _, header := range headers {
		entries, entryErr := h.loadSnapshotEntries(r.Context(), header.rowID)
		if entryErr != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "entries query failed"})
			return
		}

		versions, versionErr := h.loadSnapshotFileVersions(r.Context(), header.rowID)
		if versionErr != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "versions query failed"})
			return
		}

		snapshots = append(snapshots, latestSnapshotResponse{
			Ok: true,
			Repository: syncRepositoryMeta{
				ID:          repositoryID,
				Name:        name,
				Description: description,
			},
			Snapshot:     header.snapshot,
			Entries:      entries,
			FileVersions: versions,
		})
	}

	writeJSON(w, http.StatusOK, repositorySnapshotsResponse{
		Ok:        true,
		Snapshots: snapshots,
	})
}

func (h *Handler) loadSnapshotEntries(ctx context.Context, snapshotRowID int64) ([]syncSnapshotEntry, error) {
	entryRows, err := h.db.Query(ctx, `
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
		return nil, err
	}
	defer entryRows.Close()

	entries := make([]syncSnapshotEntry, 0, 1024)
	for entryRows.Next() {
		var entry syncSnapshotEntry
		if err := entryRows.Scan(
			&entry.RelativePath,
			&entry.ParentRelativePath,
			&entry.Name,
			&entry.IsDirectory,
			&entry.Extension,
			&entry.SizeBytes,
			&entry.LastWriteUTC,
			&entry.ContentHashSHA256,
		); err != nil {
			return nil, err
		}
		entries = append(entries, entry)
	}

	return entries, entryRows.Err()
}

func (h *Handler) loadSnapshotFileVersions(ctx context.Context, snapshotRowID int64) ([]syncFileVersion, error) {
	versionRows, err := h.db.Query(ctx, `
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
		return nil, err
	}
	defer versionRows.Close()

	versions := make([]syncFileVersion, 0, 1024)
	for versionRows.Next() {
		var version syncFileVersion
		var blocksJSON []byte
		if err := versionRows.Scan(
			&version.RelativePath,
			&version.FileVersionID,
			&version.ContentHashSHA256,
			&version.SizeBytes,
			&version.IsDeletionMarker,
			&version.CreatedAt,
			&blocksJSON,
		); err != nil {
			return nil, err
		}

		if len(blocksJSON) > 0 {
			if err = json.Unmarshal(blocksJSON, &version.Blocks); err != nil {
				return nil, err
			}
		}

		versions = append(versions, version)
	}

	return versions, versionRows.Err()
}

func (h *Handler) HeadBlock(w http.ResponseWriter, r *http.Request) {
	if !ensureSyncProtocol(w, r) {
		return
	}

	_, ok := getAuthUser(r)
	if !ok {
		w.WriteHeader(http.StatusUnauthorized)
		return
	}

	hash := strings.TrimSpace(r.PathValue("blockHash"))
	if hash == "" || !isValidBlockHash(hash) {
		logHTTPRequestEvent(r, "warning", "head_block", "invalid block hash")
		w.WriteHeader(http.StatusBadRequest)
		return
	}

	var exists bool
	if err := h.db.QueryRow(r.Context(), `
SELECT EXISTS(
    SELECT 1
    FROM cloud_blocks
    WHERE block_hash = $1
      AND COALESCE(NULLIF(storage_kind, ''), 'loose') <> 'missing')`, hash).Scan(&exists); err != nil {
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
	if !ensureSyncProtocol(w, r) {
		return
	}

	_, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	hash := strings.TrimSpace(r.PathValue("blockHash"))
	if hash == "" || !isValidBlockHash(hash) {
		logHTTPRequestEvent(r, "warning", "put_block", "invalid block hash",
			"block_hash="+quoteLogValue(hash))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid block hash"})
		return
	}

	if existing, exists, err := h.loadBlockRecord(r.Context(), hash); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to query block metadata"})
		return
	} else if exists {
		writeJSON(w, http.StatusOK, map[string]any{"ok": true, "size_bytes": existing.LengthBytes})
		return
	}

	reader := http.MaxBytesReader(w, r.Body, 32*1024*1024)
	if err := os.MkdirAll(h.blockStoreDir, 0o755); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to prepare block store"})
		return
	}

	staged, err := os.CreateTemp(h.blockStoreDir, "upload-*.block")
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to allocate temporary block storage"})
		return
	}
	stagedPath := staged.Name()
	defer func() {
		_ = staged.Close()
		_ = os.Remove(stagedPath)
	}()

	var writer io.Writer = staged
	var digestWriter stdhash.Hash
	if strings.HasPrefix(strings.ToLower(hash), "sha256-") {
		digestWriter = sha256.New()
		writer = io.MultiWriter(staged, digestWriter)
	}

	sizeBytes, err := io.Copy(writer, reader)
	if err != nil {
		logHTTPRequestEvent(r, "warning", "put_block", "failed to read block request body",
			"block_hash="+quoteLogValue(hash),
			"error="+quoteLogValue(err.Error()))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "failed to read request body"})
		return
	}

	if err = staged.Sync(); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to stage block payload"})
		return
	}

	if digestWriter != nil {
		expected := strings.TrimPrefix(strings.ToLower(hash), "sha256-")
		actual := strings.ToLower(hex.EncodeToString(digestWriter.Sum(nil)))
		if expected != actual {
			logHTTPRequestEvent(r, "warning", "put_block", "sha256 mismatch",
				"block_hash="+quoteLogValue(hash),
				"expected="+quoteLogValue(expected),
				"actual="+quoteLogValue(actual))
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "sha256 mismatch"})
			return
		}
	}

	if err = staged.Close(); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to finalize staged block payload"})
		return
	}

	if err = h.storeBlockInPack(r.Context(), hash, stagedPath, sizeBytes); err != nil {
		logHTTPRequestEvent(r, "error", "put_block", "failed to persist block payload",
			"block_hash="+quoteLogValue(hash),
			"size_bytes="+fmt.Sprintf("%d", sizeBytes),
			"error="+quoteLogValue(err.Error()))
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to persist block payload", "blockHash": hash})
		return
	}

	logHTTPRequestEvent(r, "information", "put_block", "block stored",
		"block_hash="+quoteLogValue(hash),
		"size_bytes="+fmt.Sprintf("%d", sizeBytes))
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "size_bytes": sizeBytes})
}

func (h *Handler) PutBlocksBatch(w http.ResponseWriter, r *http.Request) {
	if !ensureSyncProtocol(w, r) {
		return
	}

	_, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	reader := http.MaxBytesReader(w, r.Body, h.maxPushPayloadBytes)
	r.Body = reader

	multipartReader, err := r.MultipartReader()
	if err != nil {
		logHTTPRequestEvent(r, "warning", "put_blocks_batch", "invalid multipart payload",
			"error="+quoteLogValue(err.Error()))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid multipart payload"})
		return
	}

	if err = os.MkdirAll(h.blockStoreDir, 0o755); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to prepare block store"})
		return
	}

	storedBlocks := 0
	skippedBlocks := 0

	for {
		part, nextErr := multipartReader.NextPart()
		if errors.Is(nextErr, io.EOF) {
			break
		}
		if nextErr != nil {
			logHTTPRequestEvent(r, "warning", "put_blocks_batch", "failed to read multipart block batch",
				"stored_blocks="+fmt.Sprintf("%d", storedBlocks),
				"skipped_blocks="+fmt.Sprintf("%d", skippedBlocks),
				"error="+quoteLogValue(nextErr.Error()))
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "failed to read multipart block batch"})
			return
		}

		if part.FormName() != "blocks" {
			_, _ = io.Copy(io.Discard, part)
			_ = part.Close()
			continue
		}

		hash := strings.TrimSpace(part.Header.Get("X-Block-Hash"))
		if hash == "" {
			hash = strings.TrimSpace(part.FileName())
		}

		if hash == "" || !isValidBlockHash(hash) {
			_, _ = io.Copy(io.Discard, part)
			_ = part.Close()
			logHTTPRequestEvent(r, "warning", "put_blocks_batch", "invalid block hash in batch",
				"block_hash="+quoteLogValue(hash))
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid block hash in batch"})
			return
		}

		if existing, exists, loadErr := h.loadBlockRecord(r.Context(), hash); loadErr != nil {
			_, _ = io.Copy(io.Discard, part)
			_ = part.Close()
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to query block metadata"})
			return
		} else if exists && existing.StorageKind != "missing" {
			_, _ = io.Copy(io.Discard, part)
			_ = part.Close()
			skippedBlocks++
			continue
		}

		staged, createErr := os.CreateTemp(h.blockStoreDir, "upload-batch-*.block")
		if createErr != nil {
			_, _ = io.Copy(io.Discard, part)
			_ = part.Close()
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to allocate temporary block storage"})
			return
		}

		stagedPath := staged.Name()
		var digestWriter stdhash.Hash
		var writer io.Writer = staged
		if strings.HasPrefix(strings.ToLower(hash), "sha256-") {
			digestWriter = sha256.New()
			writer = io.MultiWriter(staged, digestWriter)
		}

		sizeBytes, copyErr := io.Copy(writer, part)
		_ = part.Close()
		if copyErr != nil {
			_ = staged.Close()
			_ = os.Remove(stagedPath)
			logHTTPRequestEvent(r, "warning", "put_blocks_batch", "failed to read staged block payload",
				"block_hash="+quoteLogValue(hash),
				"error="+quoteLogValue(copyErr.Error()))
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "failed to read block payload"})
			return
		}

		if syncErr := staged.Sync(); syncErr != nil {
			_ = staged.Close()
			_ = os.Remove(stagedPath)
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to stage block payload"})
			return
		}

		if digestWriter != nil {
			expected := strings.TrimPrefix(strings.ToLower(hash), "sha256-")
			actual := strings.ToLower(hex.EncodeToString(digestWriter.Sum(nil)))
			if expected != actual {
				_ = staged.Close()
				_ = os.Remove(stagedPath)
				logHTTPRequestEvent(r, "warning", "put_blocks_batch", "sha256 mismatch in batch",
					"block_hash="+quoteLogValue(hash),
					"expected="+quoteLogValue(expected),
					"actual="+quoteLogValue(actual))
				writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "sha256 mismatch"})
				return
			}
		}

		if closeErr := staged.Close(); closeErr != nil {
			_ = os.Remove(stagedPath)
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to finalize staged block payload"})
			return
		}

		storeErr := h.storeBlockInPack(r.Context(), hash, stagedPath, sizeBytes)
		_ = os.Remove(stagedPath)
		if storeErr != nil {
			logHTTPRequestEvent(r, "error", "put_blocks_batch", "failed to persist block payload",
				"block_hash="+quoteLogValue(hash),
				"size_bytes="+fmt.Sprintf("%d", sizeBytes),
				"stored_blocks="+fmt.Sprintf("%d", storedBlocks),
				"skipped_blocks="+fmt.Sprintf("%d", skippedBlocks),
				"error="+quoteLogValue(storeErr.Error()))
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to persist block payload", "blockHash": hash})
			return
		}

		storedBlocks++
	}

	logHTTPRequestEvent(r, "information", "put_blocks_batch", "batch block upload stored",
		"stored_blocks="+fmt.Sprintf("%d", storedBlocks),
		"skipped_blocks="+fmt.Sprintf("%d", skippedBlocks))
	writeJSON(w, http.StatusOK, putBlocksBatchResponse{
		Ok:            true,
		StoredBlocks:  storedBlocks,
		SkippedBlocks: skippedBlocks,
	})
}

func (h *Handler) GetBlock(w http.ResponseWriter, r *http.Request) {
	if !ensureSyncProtocol(w, r) {
		return
	}

	_, ok := getAuthUser(r)
	if !ok {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	hash := strings.TrimSpace(r.PathValue("blockHash"))
	if hash == "" || !isValidBlockHash(hash) {
		logHTTPRequestEvent(r, "warning", "get_block", "invalid block hash",
			"block_hash="+quoteLogValue(hash))
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid block hash"})
		return
	}

	record, exists, err := h.loadBlockRecord(r.Context(), hash)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to query block metadata"})
		return
	}
	if !exists || strings.EqualFold(record.StorageKind, "missing") {
		logHTTPRequestEvent(r, "warning", "get_block", "block not found",
			"block_hash="+quoteLogValue(hash),
			"storage_kind="+quoteLogValue(record.StorageKind))
		writeJSON(w, http.StatusNotFound, map[string]any{"ok": false, "message": "block not found"})
		return
	}

	if strings.EqualFold(record.StorageKind, "pack") && record.PackRelativePath != "" {
		packPath := h.blockPackPath(record.PackRelativePath)
		file, err := os.Open(packPath)
		if err != nil {
			if os.IsNotExist(err) {
				writeJSON(w, http.StatusNotFound, map[string]any{"ok": false, "message": "block pack not found"})
				return
			}
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to read packed block"})
			return
		}
		defer file.Close()

		if _, err = file.Seek(record.PackOffsetBytes, io.SeekStart); err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to seek packed block"})
			return
		}

		w.Header().Set("Content-Type", "application/octet-stream")
		w.Header().Set("Content-Length", fmt.Sprintf("%d", record.StoredSizeBytes))
		w.WriteHeader(http.StatusOK)
		if _, err = io.CopyN(w, file, record.StoredSizeBytes); err != nil {
			return
		}
		return
	}

	path := h.blockPath(hash)
	file, err := os.Open(path)
	if err != nil {
		if os.IsNotExist(err) {
			writeJSON(w, http.StatusNotFound, map[string]any{"ok": false, "message": "block not found"})
			return
		}
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to read block"})
		return
	}
	defer file.Close()

	stat, err := file.Stat()
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to stat block"})
		return
	}

	w.Header().Set("Content-Type", "application/octet-stream")
	w.Header().Set("Content-Length", fmt.Sprintf("%d", stat.Size()))
	w.WriteHeader(http.StatusOK)
	_, _ = io.Copy(w, file)
}

func ensureSyncProtocol(w http.ResponseWriter, r *http.Request) bool {
	w.Header().Set(syncProtocolHeader, supportedSyncProtocol)

	clientVersion := strings.TrimSpace(r.Header.Get(syncProtocolHeader))
	if clientVersion == supportedSyncProtocol {
		return true
	}

	logHTTPRequestEvent(r, "warning", "sync_protocol", "sync protocol mismatch",
		"provided_protocol="+quoteLogValue(clientVersion),
		"required_protocol="+quoteLogValue(supportedSyncProtocol))
	w.Header().Set("X-Veyra-Sync-Protocol-Supported", supportedSyncProtocol)
	writeJSON(w, http.StatusPreconditionFailed, map[string]any{
		"ok":               false,
		"message":          "sync protocol mismatch",
		"requiredProtocol": supportedSyncProtocol,
		"providedProtocol": clientVersion,
	})
	return false
}

func normalizeIdempotencyKey(raw string) string {
	trimmed := strings.TrimSpace(raw)
	if trimmed == "" {
		return ""
	}

	if len(trimmed) > 120 {
		return ""
	}

	for _, ch := range trimmed {
		if (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') {
			continue
		}
		switch ch {
		case '-', '_', '.', ':':
			continue
		default:
			return ""
		}
	}

	return trimmed
}

func claimSyncIdempotencyKey(
	ctx context.Context,
	tx pgx.Tx,
	userID int64,
	externalRepositoryID int,
	idempotencyKey string,
	requestSHA string,
) (int64, *syncIdempotencyReplay, error) {
	var keyID int64
	err := tx.QueryRow(ctx, `
INSERT INTO sync_idempotency_keys(
    user_id,
    external_repository_id,
    idempotency_key,
    request_sha256,
    state,
    created_at,
    updated_at)
VALUES($1, $2, $3, $4, 'in_progress', now(), now())
ON CONFLICT (user_id, external_repository_id, idempotency_key)
DO NOTHING
RETURNING id;
`, userID, externalRepositoryID, idempotencyKey, requestSHA).Scan(&keyID)
	if err == nil {
		return keyID, nil, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return 0, nil, err
	}

	var storedSHA string
	var state string
	var statusCode sql.NullInt32
	var responseJSON []byte

	err = tx.QueryRow(ctx, `
SELECT id, request_sha256, state, status_code, response_json
FROM sync_idempotency_keys
WHERE user_id = $1
  AND external_repository_id = $2
  AND idempotency_key = $3
FOR UPDATE;
`, userID, externalRepositoryID, idempotencyKey).Scan(&keyID, &storedSHA, &state, &statusCode, &responseJSON)
	if err != nil {
		return 0, nil, err
	}

	if !strings.EqualFold(storedSHA, requestSHA) {
		payload, _ := json.Marshal(map[string]any{
			"ok":      false,
			"message": "idempotency key was already used with a different payload",
		})
		return 0, &syncIdempotencyReplay{StatusCode: http.StatusConflict, Payload: payload}, nil
	}

	if state == "completed" && statusCode.Valid && len(responseJSON) > 0 {
		return 0, &syncIdempotencyReplay{StatusCode: int(statusCode.Int32), Payload: responseJSON}, nil
	}

	payload, _ := json.Marshal(map[string]any{
		"ok":      false,
		"message": "request with the same idempotency key is already in progress",
	})
	return 0, &syncIdempotencyReplay{StatusCode: http.StatusConflict, Payload: payload}, nil
}

func writeJSONBytes(w http.ResponseWriter, code int, payload []byte) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_, _ = w.Write(payload)
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
