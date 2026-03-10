package handlers

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"golang.org/x/crypto/bcrypt"
)

type Handler struct {
	db            *pgxpool.Pool
	tokenSecret   []byte
	tokenLifetime time.Duration
	blockStoreDir string
}

func New(db *pgxpool.Pool, tokenSecret string, blockStoreDir string, tokenLifetime time.Duration) *Handler {
	if strings.TrimSpace(tokenSecret) == "" {
		tokenSecret = "veyra-dev-secret-change-me"
	}
	if strings.TrimSpace(blockStoreDir) == "" {
		blockStoreDir = "./data/blocks"
	}

	if tokenLifetime <= 0 {
		tokenLifetime = 24 * time.Hour
	}

	return &Handler{
		db:            db,
		tokenSecret:   []byte(tokenSecret),
		tokenLifetime: tokenLifetime,
		blockStoreDir: blockStoreDir,
	}
}

func (h *Handler) Health(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

type authReq struct {
	Username string `json:"username"`
	Password string `json:"password"`
}

type authResp struct {
	Ok           bool      `json:"ok"`
	Message      string    `json:"message,omitempty"`
	UserID       int64     `json:"userId,omitempty"`
	Username     string    `json:"username,omitempty"`
	AccessToken  string    `json:"accessToken,omitempty"`
	IsNewUser    bool      `json:"isNewUser,omitempty"`
	ExpiresAtUtc time.Time `json:"expiresAtUtc,omitempty"`
}

func (h *Handler) Register(w http.ResponseWriter, r *http.Request) {
	var req authReq
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "bad json"})
		return
	}

	req.Username = strings.TrimSpace(req.Username)
	if req.Username == "" || len(req.Username) > 50 {
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "invalid username"})
		return
	}
	if len(req.Password) < 8 || len(req.Password) > 100 {
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "invalid password"})
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
	defer cancel()

	hash, err := bcrypt.GenerateFromPassword([]byte(req.Password), bcrypt.DefaultCost)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "password hash failed"})
		return
	}

	var userID int64
	err = h.db.QueryRow(ctx,
		`INSERT INTO users (username, password_hash) VALUES ($1, $2) RETURNING id`,
		req.Username,
		string(hash),
	).Scan(&userID)
	if err != nil {
		if strings.Contains(strings.ToLower(err.Error()), "duplicate") {
			writeJSON(w, http.StatusConflict, authResp{Ok: false, Message: "username already exists"})
			return
		}
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "failed to register user"})
		return
	}

	token, expiresAtUtc, err := h.makeToken(userID, req.Username)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "failed to create token"})
		return
	}

	writeJSON(w, http.StatusOK, authResp{
		Ok:           true,
		UserID:       userID,
		Username:     req.Username,
		AccessToken:  token,
		IsNewUser:    true,
		ExpiresAtUtc: expiresAtUtc,
	})
}

func (h *Handler) Login(w http.ResponseWriter, r *http.Request) {
	var req authReq
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "bad json"})
		return
	}

	req.Username = strings.TrimSpace(req.Username)

	ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
	defer cancel()

	var userID int64
	var hash sql.NullString
	err := h.db.QueryRow(ctx,
		`SELECT id, password_hash FROM users WHERE username = $1`,
		req.Username,
	).Scan(&userID, &hash)
	if err != nil || !hash.Valid {
		writeAuthFail(w)
		return
	}

	if bcrypt.CompareHashAndPassword([]byte(hash.String), []byte(req.Password)) != nil {
		writeAuthFail(w)
		return
	}

	token, expiresAtUtc, err := h.makeToken(userID, req.Username)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "failed to create token"})
		return
	}

	writeJSON(w, http.StatusOK, authResp{
		Ok:           true,
		UserID:       userID,
		Username:     req.Username,
		AccessToken:  token,
		IsNewUser:    false,
		ExpiresAtUtc: expiresAtUtc,
	})
}

type tokenPayload struct {
	UID int64  `json:"uid"`
	USR string `json:"usr"`
	EXP int64  `json:"exp"`
}

func (h *Handler) makeToken(userID int64, username string) (string, time.Time, error) {
	headerJSON := []byte(`{"alg":"HS256","typ":"JWT"}`)
	expiresAtUtc := time.Now().UTC().Add(h.tokenLifetime)
	payloadJSON, err := json.Marshal(tokenPayload{
		UID: userID,
		USR: username,
		EXP: expiresAtUtc.Unix(),
	})
	if err != nil {
		return "", time.Time{}, err
	}

	header := base64.RawURLEncoding.EncodeToString(headerJSON)
	payload := base64.RawURLEncoding.EncodeToString(payloadJSON)
	signingInput := header + "." + payload

	mac := hmac.New(sha256.New, h.tokenSecret)
	_, _ = mac.Write([]byte(signingInput))
	sig := base64.RawURLEncoding.EncodeToString(mac.Sum(nil))

	return signingInput + "." + sig, expiresAtUtc, nil
}

func (h *Handler) parseToken(token string) (tokenPayload, error) {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return tokenPayload{}, fmt.Errorf("invalid token format")
	}

	signingInput := parts[0] + "." + parts[1]

	mac := hmac.New(sha256.New, h.tokenSecret)
	_, _ = mac.Write([]byte(signingInput))
	expectedSig := mac.Sum(nil)

	sig, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil {
		return tokenPayload{}, fmt.Errorf("invalid token signature")
	}

	if !hmac.Equal(sig, expectedSig) {
		return tokenPayload{}, fmt.Errorf("signature mismatch")
	}

	payloadBytes, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return tokenPayload{}, fmt.Errorf("invalid token payload")
	}

	var payload tokenPayload
	if err = json.Unmarshal(payloadBytes, &payload); err != nil {
		return tokenPayload{}, fmt.Errorf("invalid token payload json")
	}

	if payload.UID <= 0 || payload.USR == "" {
		return tokenPayload{}, fmt.Errorf("invalid token claims")
	}
	if payload.EXP <= time.Now().Unix() {
		return tokenPayload{}, fmt.Errorf("token expired")
	}

	return payload, nil
}

type ctxKey string

const ctxUserKey ctxKey = "auth-user"

type authUser struct {
	UserID   int64
	Username string
}

func (h *Handler) WithAuth(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		header := strings.TrimSpace(r.Header.Get("Authorization"))
		if header == "" || !strings.HasPrefix(strings.ToLower(header), "bearer ") {
			writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "missing bearer token"})
			return
		}

		token := strings.TrimSpace(header[len("Bearer "):])
		claims, err := h.parseToken(token)
		if err != nil {
			writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "invalid token"})
			return
		}

		ctx := context.WithValue(r.Context(), ctxUserKey, authUser{
			UserID:   claims.UID,
			Username: claims.USR,
		})
		next(w, r.WithContext(ctx))
	}
}

func getAuthUser(r *http.Request) (authUser, bool) {
	v := r.Context().Value(ctxUserKey)
	if v == nil {
		return authUser{}, false
	}

	u, ok := v.(authUser)
	return u, ok
}

func parsePathInt(r *http.Request, key string) (int, error) {
	raw := strings.TrimSpace(r.PathValue(key))
	if raw == "" {
		return 0, fmt.Errorf("missing path value")
	}

	v, err := strconv.Atoi(raw)
	if err != nil {
		return 0, fmt.Errorf("invalid path value")
	}

	return v, nil
}

func writeAuthFail(w http.ResponseWriter) {
	writeJSON(w, http.StatusUnauthorized, authResp{Ok: false, Message: "invalid credentials"})
}

func writeJSON(w http.ResponseWriter, code int, payload any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(payload)
}
