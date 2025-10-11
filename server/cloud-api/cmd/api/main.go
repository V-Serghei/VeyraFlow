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
	// ENV
	port := getenv("HTTP_PORT", "8080")
	dsn := getenv("DB_DSN", "postgres://veyra:veyra_pass_Dev@localhost:5432/veyraflow?sslmode=disable")

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	// DB + migration + seed
	pool, err := postgres.Open(ctx, dsn)
	if err != nil {
		log.Fatalf("db open error: %v", err)
	}
	defer pool.Close()

	if err := postgres.Migrate(ctx, pool); err != nil {
		log.Fatalf("migration error: %v", err)
	}
	if err := postgres.SeedAdmin(ctx, pool, "admin", "admin"); err != nil {
		log.Fatalf("seed error: %v", err)
	}

	// HTTP
	h := handlers.New(pool)

	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", h.Health)
	mux.HandleFunc("POST /api/login", h.Login)

	srv := &http.Server{
		Addr:         ":" + port,
		Handler:      mux,
		ReadTimeout:  5 * time.Second,
		WriteTimeout: 10 * time.Second,
	}

	log.Printf("cloud-api listening on :%s", port)
	if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		log.Fatal(err)
	}
}

func getenv(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}
