package handlers

import (
	"context"
	"database/sql"
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"golang.org/x/crypto/bcrypt"
)

type Handler struct {
	db *pgxpool.Pool
}

func New(db *pgxpool.Pool) *Handler { return &Handler{db: db} }

func (h *Handler) Health(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{"ok": true})
}

type loginReq struct {
	Username string `json:"username"`
	Password string `json:"password"`
}
type loginResp struct {
	Ok      bool   `json:"ok"`
	Message string `json:"message,omitempty"`
}

func (h *Handler) Login(w http.ResponseWriter, r *http.Request) {
	var req loginReq
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "bad json", http.StatusBadRequest)
		return
	}
	req.Username = strings.TrimSpace(req.Username)

	ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
	defer cancel()

	var hash sql.NullString
	err := h.db.QueryRow(ctx,
		`SELECT password_hash FROM users WHERE username = $1`,
		req.Username,
	).Scan(&hash)
	if err != nil || !hash.Valid {
		writeAuthFail(w)
		return
	}

	if bcrypt.CompareHashAndPassword([]byte(hash.String), []byte(req.Password)) != nil {
		writeAuthFail(w)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(loginResp{Ok: true})
}

func writeAuthFail(w http.ResponseWriter) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusUnauthorized)
	_ = json.NewEncoder(w).Encode(loginResp{Ok: false, Message: "invalid credentials"})
}
