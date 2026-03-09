package main

import (
	"context"
	"log"
	"net/http"
	"os"
	"time"

	"veyraflow/server/cloud-api/internal/handlers"
	"veyraflow/server/cloud-api/internal/store/postgres"
)

func main() {
	port := getenv("HTTP_PORT", "8080")
	dsn := getenv("DB_DSN", "postgres://veyra:veyra_pass_Dev@localhost:5432/veyraflow?sslmode=disable")
	tokenSecret := getenv("AUTH_TOKEN_SECRET", "veyra-dev-secret-change-me")
	blockStoreDir := getenv("BLOCK_STORE_DIR", "./data/blocks")

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

	h := handlers.New(pool, tokenSecret, blockStoreDir)

	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", h.Health)
	mux.HandleFunc("POST /api/register", h.Register)
	mux.HandleFunc("POST /api/login", h.Login)

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

func getenv(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}
