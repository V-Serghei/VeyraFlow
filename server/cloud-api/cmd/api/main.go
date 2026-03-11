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
	"veyraflow/server/cloud-api/internal/store/postgres"
)

func main() {
	port := getenv("HTTP_PORT", "8080")
	dsn := getenv("DB_DSN", "postgres://veyra:veyra_pass_Dev@localhost:5432/veyraflow?sslmode=disable")
	tokenSecret := getenv("AUTH_TOKEN_SECRET", "veyra-dev-secret-change-me")
	blockStoreDir := getenv("BLOCK_STORE_DIR", "./data/blocks")
	blockPackTargetMB := getenvInt("BLOCK_PACK_TARGET_MB", 128, 16, 4096)
	blockCompactionIntervalSeconds := getenvInt("BLOCK_COMPACTION_INTERVAL_SECONDS", 120, 10, 86400)
	blockCompactionBatchSize := getenvInt("BLOCK_COMPACTION_BATCH_SIZE", 128, 1, 10000)
	tokenLifetimeMinutes := getenvInt("AUTH_TOKEN_LIFETIME_MINUTES", 1440, 5, 30*24*60)
	refreshTokenLifetimeMinutes := getenvInt("AUTH_REFRESH_TOKEN_LIFETIME_MINUTES", 30*24*60, 60, 90*24*60)

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
	mux.HandleFunc("GET /api/sync/repositories/{repositoryId}/latest", h.WithAuth(h.GetLatestRepositorySnapshot))
	mux.HandleFunc("POST /api/sync/repositories/{repositoryId}/snapshots", h.WithAuth(h.PushRepositorySnapshot))
	mux.HandleFunc("HEAD /api/sync/blocks/{blockHash}", h.WithAuth(h.HeadBlock))
	mux.HandleFunc("POST /api/sync/blocks/{blockHash}", h.WithAuth(h.PutBlock))
	mux.HandleFunc("GET /api/sync/blocks/{blockHash}", h.WithAuth(h.GetBlock))

	srv := &http.Server{
		Addr:         ":" + port,
		Handler:      mux,
		ReadTimeout:  15 * time.Second,
		WriteTimeout: 60 * time.Second,
	}

	log.Printf("cloud-api listening on :%s", port)
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

		compacted, err := h.CompactLooseBlocks(runCtx, batchSize)
		if err != nil {
			log.Printf("cloud block compaction error. trigger=%s compacted=%d err=%v", trigger, compacted, err)
			return
		}
		if compacted > 0 {
			log.Printf("cloud block compaction completed. trigger=%s compacted=%d", trigger, compacted)
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
