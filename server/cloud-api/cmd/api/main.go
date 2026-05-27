package main

import (
	"context"
	"log"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"

	"veyraflow/server/cloud-api/internal/handlers"
	"veyraflow/server/cloud-api/internal/obs"
	"veyraflow/server/cloud-api/internal/store/postgres"
)

func main() {
	port := getenv("HTTP_PORT", "8080")
	dsn := getenv("DB_DSN", "postgres://veyra:veyra_pass_Dev@localhost:5432/veyraflow?sslmode=disable")
	seqURL := getenv("SEQ_URL", "")
	tokenSecret := getenv("AUTH_TOKEN_SECRET", "veyra-dev-secret-change-me")
	blockStoreDir := getenv("BLOCK_STORE_DIR", "./data/blocks")
	blockPackTargetMB := getenvInt("BLOCK_PACK_TARGET_MB", 128, 16, 4096)
	blockCompactionIntervalSeconds := getenvInt("BLOCK_COMPACTION_INTERVAL_SECONDS", 120, 10, 86400)
	blockCompactionBatchSize := getenvInt("BLOCK_COMPACTION_BATCH_SIZE", 128, 1, 10000)
	maxPushPayloadMB := getenvInt("SYNC_MAX_PUSH_PAYLOAD_MB", 256, 16, 1024)
	tokenLifetimeMinutes := getenvInt("AUTH_TOKEN_LIFETIME_MINUTES", 1440, 5, 30*24*60)
	refreshTokenLifetimeMinutes := getenvInt("AUTH_REFRESH_TOKEN_LIFETIME_MINUTES", 30*24*60, 60, 90*24*60)
	closeLogger := obs.ConfigureStandardLogger("veyra-cloud-api", seqURL)
	defer closeLogger()

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	pool, err := postgres.Open(ctx, dsn)
	if err != nil {
		log.Fatalf("db open error: %v", err)
	}
	defer pool.Close()

	if err = postgres.Migrate(ctx, pool); err != nil {
		log.Fatalf("migration error: %v", err)
	}

	if getenv("SEED_ADMIN", "false") == "true" {
		if err = postgres.SeedAdmin(ctx, pool, "admin", "admin"); err != nil {
			log.Fatalf("seed error: %v", err)
		}
	}

	h := handlers.New(
		pool,
		tokenSecret,
		blockStoreDir,
		int64(blockPackTargetMB)*1024*1024,
		int64(maxPushPayloadMB)*1024*1024,
		time.Duration(tokenLifetimeMinutes)*time.Minute,
		time.Duration(refreshTokenLifetimeMinutes)*time.Minute)

	maintenanceCtx, maintenanceCancel := context.WithCancel(context.Background())
	defer maintenanceCancel()
	go runBlockMaintenance(
		maintenanceCtx,
		h,
		blockCompactionBatchSize,
		time.Duration(blockCompactionIntervalSeconds)*time.Second)

	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", h.Health)
	mux.HandleFunc("POST /api/register", h.Register)
	mux.HandleFunc("POST /api/login", h.Login)
	mux.HandleFunc("POST /api/refresh", h.Refresh)
	mux.HandleFunc("POST /api/logout", h.WithAuth(h.Logout))

	mux.HandleFunc("GET /api/sync/repositories", h.WithAuth(h.ListRepositories))
	mux.HandleFunc("DELETE /api/sync/repositories/{repositoryId}", h.WithAuth(h.DeleteRepository))
	mux.HandleFunc("GET /api/sync/repositories/{repositoryId}/latest", h.WithAuth(h.GetLatestRepositorySnapshot))
	mux.HandleFunc("GET /api/sync/repositories/{repositoryId}/snapshots", h.WithAuth(h.ListRepositorySnapshots))
	mux.HandleFunc("POST /api/sync/repositories/{repositoryId}/snapshots", h.WithAuth(h.PushRepositorySnapshot))
	mux.HandleFunc("HEAD /api/sync/blocks/{blockHash}", h.WithAuth(h.HeadBlock))
	mux.HandleFunc("POST /api/sync/blocks/batch", h.WithAuth(h.PutBlocksBatch))
	mux.HandleFunc("POST /api/sync/blocks/{blockHash}", h.WithAuth(h.PutBlock))
	mux.HandleFunc("GET /api/sync/blocks/{blockHash}", h.WithAuth(h.GetBlock))
	mux.HandleFunc("GET /api/admin/storage/metrics", h.WithAuth(h.GetStorageMetrics))
	mux.HandleFunc("POST /api/admin/storage/repair", h.WithAuth(h.RepairStorage))

	srv := &http.Server{
		Addr:         ":" + port,
		Handler:      withRecoveryLogging(withHTTPAccessLogging(mux)),
		ReadTimeout:  15 * time.Second,
		WriteTimeout: 60 * time.Second,
	}

	log.Printf(
		"cloud-api listening on :%s seq_url=%q pack_target_mb=%d push_payload_mb=%d compaction_interval_seconds=%d compaction_batch_size=%d",
		port,
		seqURL,
		blockPackTargetMB,
		maxPushPayloadMB,
		blockCompactionIntervalSeconds,
		blockCompactionBatchSize)
	if err = srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		log.Fatal(err)
	}
}

func runBlockMaintenance(
	ctx context.Context,
	h *handlers.Handler,
	batchSize int,
	interval time.Duration,
) {
	runOnce := func(trigger string) {
		runCtx, cancel := context.WithTimeout(ctx, 5*time.Minute)
		defer cancel()

		stats, err := h.RepairBlockStorage(runCtx, batchSize*4, batchSize)
		if err != nil {
			log.Printf(
				"cloud block maintenance error. trigger=%s scanned=%d missing_marked=%d broken_loose=%d broken_pack=%d compacted=%d err=%v",
				trigger,
				stats.Scanned,
				stats.MissingMarked,
				stats.BrokenLooseRefs,
				stats.BrokenPackRefs,
				stats.Compacted,
				err,
			)
			return
		}
		if stats.MissingMarked > 0 || stats.Compacted > 0 || stats.BrokenPackRefs > 0 || stats.BrokenLooseRefs > 0 {
			log.Printf(
				"cloud block maintenance completed. trigger=%s scanned=%d missing_marked=%d broken_loose=%d broken_pack=%d compacted=%d",
				trigger,
				stats.Scanned,
				stats.MissingMarked,
				stats.BrokenLooseRefs,
				stats.BrokenPackRefs,
				stats.Compacted,
			)
		}
	}

	runOnce("startup")

	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			runOnce("interval")
		}
	}
}

func getenv(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func getenvInt(k string, def, min, max int) int {
	v := strings.TrimSpace(os.Getenv(k))
	if v == "" {
		return def
	}

	parsed, err := strconv.Atoi(v)
	if err != nil {
		return def
	}

	if parsed < min {
		return min
	}
	if parsed > max {
		return max
	}
	return parsed
}

type statusCapturingResponseWriter struct {
	http.ResponseWriter
	statusCode int
}

func (w *statusCapturingResponseWriter) WriteHeader(statusCode int) {
	w.statusCode = statusCode
	w.ResponseWriter.WriteHeader(statusCode)
}

func withHTTPAccessLogging(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		startedAt := time.Now().UTC()
		captured := &statusCapturingResponseWriter{
			ResponseWriter: w,
			statusCode:     http.StatusOK,
		}

		next.ServeHTTP(captured, r)

		duration := time.Since(startedAt)
		log.Printf(
			"http request completed method=%s path=%s status=%d duration_ms=%d remote=%s user_agent=%q",
			r.Method,
			r.URL.Path,
			captured.statusCode,
			duration.Milliseconds(),
			strings.TrimSpace(r.RemoteAddr),
			strings.TrimSpace(r.UserAgent()))
	})
}

func withRecoveryLogging(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		defer func() {
			if recovered := recover(); recovered != nil {
				log.Printf("panic recovered method=%s path=%s remote=%s panic=%v", r.Method, r.URL.Path, strings.TrimSpace(r.RemoteAddr), recovered)
				http.Error(w, http.StatusText(http.StatusInternalServerError), http.StatusInternalServerError)
			}
		}()

		next.ServeHTTP(w, r)
	})
}
