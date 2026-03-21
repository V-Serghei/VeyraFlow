package handlers

import (
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"net/mail"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"golang.org/x/crypto/bcrypt"
)

type Handler struct {
	db                   *pgxpool.Pool
	tokenSecret          []byte
	tokenLifetime        time.Duration
	refreshTokenLifetime time.Duration
	blockStoreDir        string
	packTargetBytes      int64
	maxPushPayloadBytes  int64
	packMu               sync.Mutex
}

func New(
	db *pgxpool.Pool,
	tokenSecret string,
	blockStoreDir string,
	packTargetBytes int64,
	maxPushPayloadBytes int64,
	tokenLifetime time.Duration,
	refreshTokenLifetime time.Duration,
) *Handler {
	if strings.TrimSpace(tokenSecret) == "" {
		tokenSecret = "veyra-dev-secret-change-me"
	}
	if strings.TrimSpace(blockStoreDir) == "" {
		blockStoreDir = "./data/blocks"
	}

	if tokenLifetime <= 0 {
		tokenLifetime = 24 * time.Hour
	}
	if refreshTokenLifetime <= 0 {
		refreshTokenLifetime = 30 * 24 * time.Hour
	}
	if refreshTokenLifetime < tokenLifetime {
		refreshTokenLifetime = tokenLifetime
	}

	if packTargetBytes <= 0 {
		packTargetBytes = defaultBlockPackTargetBytes
	}
	if maxPushPayloadBytes <= 0 {
		maxPushPayloadBytes = 256 * 1024 * 1024
	}

	return &Handler{
		db:                   db,
		tokenSecret:          []byte(tokenSecret),
		tokenLifetime:        tokenLifetime,
		refreshTokenLifetime: refreshTokenLifetime,
		blockStoreDir:        blockStoreDir,
		packTargetBytes:      packTargetBytes,
		maxPushPayloadBytes:  maxPushPayloadBytes,
	}
}

func (h *Handler) Health(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

type authReq struct {
	Username string `json:"username"`
	Email    string `json:"email"`
	Password string `json:"password"`
}

type authResp struct {
	Ok                  bool      `json:"ok"`
	Message             string    `json:"message,omitempty"`
	UserID              int64     `json:"userId,omitempty"`
	Username            string    `json:"username,omitempty"`
	Email               string    `json:"email,omitempty"`
	SessionID           int64     `json:"sessionId,omitempty"`
	AccessToken         string    `json:"accessToken,omitempty"`
	RefreshToken        string    `json:"refreshToken,omitempty"`
	IsNewUser           bool      `json:"isNewUser,omitempty"`
	ExpiresAtUtc        time.Time `json:"expiresAtUtc,omitempty"`
	RefreshExpiresAtUtc time.Time `json:"refreshExpiresAtUtc,omitempty"`
}

type refreshReq struct {
	RefreshToken string `json:"refreshToken"`
}

func (h *Handler) Register(w http.ResponseWriter, r *http.Request) {
	var req authReq
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		logHTTPRequestEvent(r, "warning", "register", "bad json")
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "bad json"})
		return
	}

	req.Username = strings.TrimSpace(req.Username)
	req.Email = strings.TrimSpace(strings.ToLower(req.Email))
	if req.Username == "" || len(req.Username) > 50 {
		logHTTPRequestEvent(r, "warning", "register", "invalid username",
			"username="+quoteLogValue(req.Username))
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "invalid username"})
		return
	}
	if !isValidEmail(req.Email) || len(req.Email) > 320 {
		logHTTPRequestEvent(r, "warning", "register", "invalid email",
			"username="+quoteLogValue(req.Username),
			"email="+quoteLogValue(req.Email))
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "invalid email"})
		return
	}
	if len(req.Password) < 8 || len(req.Password) > 100 {
		logHTTPRequestEvent(r, "warning", "register", "invalid password length",
			"username="+quoteLogValue(req.Username),
			"password_length="+fmt.Sprintf("%d", len(req.Password)))
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
		`INSERT INTO users (username, email, password_hash) VALUES ($1, $2, $3) RETURNING id`,
		req.Username,
		req.Email,
		string(hash),
	).Scan(&userID)
	if err != nil {
		errText := strings.ToLower(err.Error())
		if strings.Contains(errText, "ux_users_email_ci") || strings.Contains(errText, "email") {
			logHTTPRequestEvent(r, "warning", "register", "email already exists",
				"username="+quoteLogValue(req.Username),
				"email="+quoteLogValue(req.Email))
			writeJSON(w, http.StatusConflict, authResp{Ok: false, Message: "email already exists"})
			return
		}
		if strings.Contains(errText, "duplicate") || strings.Contains(errText, "users_username_key") {
			logHTTPRequestEvent(r, "warning", "register", "username already exists",
				"username="+quoteLogValue(req.Username),
				"email="+quoteLogValue(req.Email))
			writeJSON(w, http.StatusConflict, authResp{Ok: false, Message: "username already exists"})
			return
		}
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "failed to register user"})
		return
	}

	sessionID, token, expiresAtUtc, refreshToken, refreshExpiresAtUtc, err := h.issueSessionTokens(ctx, r, userID, req.Username)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "failed to create session"})
		return
	}

	writeJSON(w, http.StatusOK, authResp{
		Ok:                  true,
		UserID:              userID,
		Username:            req.Username,
		Email:               req.Email,
		SessionID:           sessionID,
		AccessToken:         token,
		RefreshToken:        refreshToken,
		IsNewUser:           true,
		ExpiresAtUtc:        expiresAtUtc,
		RefreshExpiresAtUtc: refreshExpiresAtUtc,
	})
	logHTTPRequestEvent(r, "information", "register", "user registered",
		"user_id="+fmt.Sprintf("%d", userID),
		"username="+quoteLogValue(req.Username),
		"email="+quoteLogValue(req.Email),
		"session_id="+fmt.Sprintf("%d", sessionID))
}

func (h *Handler) Login(w http.ResponseWriter, r *http.Request) {
	var req authReq
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		logHTTPRequestEvent(r, "warning", "login", "bad json")
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "bad json"})
		return
	}

	req.Username = strings.TrimSpace(req.Username)
	req.Email = strings.TrimSpace(strings.ToLower(req.Email))
	identity := req.Username
	if identity == "" {
		identity = req.Email
	}
	if identity == "" {
		logHTTPRequestEvent(r, "warning", "login", "missing identity")
		writeAuthFail(w)
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
	defer cancel()

	var userID int64
	var hash sql.NullString
	var username string
	var email sql.NullString
	err := h.db.QueryRow(ctx,
		`
SELECT id, username, email, password_hash
FROM users
WHERE lower(username) = lower($1)
UNION ALL
SELECT id, username, email, password_hash
FROM users
WHERE lower(email) = lower($1)
  AND lower(username) <> lower($1)
LIMIT 1`,
		identity,
	).Scan(&userID, &username, &email, &hash)
	if err != nil || !hash.Valid {
		logHTTPRequestEvent(r, "warning", "login", "identity not found",
			"identity="+quoteLogValue(identity))
		writeAuthFail(w)
		return
	}

	if bcrypt.CompareHashAndPassword([]byte(hash.String), []byte(req.Password)) != nil {
		logHTTPRequestEvent(r, "warning", "login", "password mismatch",
			"identity="+quoteLogValue(identity),
			"user_id="+fmt.Sprintf("%d", userID))
		writeAuthFail(w)
		return
	}

	sessionID, token, expiresAtUtc, refreshToken, refreshExpiresAtUtc, err := h.issueSessionTokens(ctx, r, userID, username)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "failed to create session"})
		return
	}

	writeJSON(w, http.StatusOK, authResp{
		Ok:                  true,
		UserID:              userID,
		Username:            username,
		Email:               strings.TrimSpace(email.String),
		SessionID:           sessionID,
		AccessToken:         token,
		RefreshToken:        refreshToken,
		IsNewUser:           false,
		ExpiresAtUtc:        expiresAtUtc,
		RefreshExpiresAtUtc: refreshExpiresAtUtc,
	})
	logHTTPRequestEvent(r, "information", "login", "user signed in",
		"user_id="+fmt.Sprintf("%d", userID),
		"username="+quoteLogValue(username),
		"session_id="+fmt.Sprintf("%d", sessionID))
}

func (h *Handler) Refresh(w http.ResponseWriter, r *http.Request) {
	var req refreshReq
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		logHTTPRequestEvent(r, "warning", "refresh", "bad json")
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "bad json"})
		return
	}

	refreshToken := strings.TrimSpace(req.RefreshToken)
	if refreshToken == "" {
		logHTTPRequestEvent(r, "warning", "refresh", "missing refresh token")
		writeJSON(w, http.StatusBadRequest, authResp{Ok: false, Message: "missing refresh token"})
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
	defer cancel()

	tokenHash := hashOpaqueToken(refreshToken)

	var sessionID int64
	var userID int64
	var username string
	var email sql.NullString
	err := h.db.QueryRow(ctx, `
SELECT s.id, s.user_id, u.username, u.email
FROM user_sessions s
INNER JOIN users u ON u.id = s.user_id
WHERE s.refresh_token_hash = $1
  AND s.revoked_at IS NULL
  AND s.refresh_expires_at > now()
LIMIT 1;
	`, tokenHash).Scan(&sessionID, &userID, &username, &email)
	if err != nil {
		logHTTPRequestEvent(r, "warning", "refresh", "invalid session for refresh",
			"refresh_token_hash="+quoteLogValue(tokenHash))
		writeJSON(w, http.StatusUnauthorized, authResp{Ok: false, Message: "invalid session"})
		return
	}

	newRefreshToken, err := makeOpaqueToken(48)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "failed to rotate refresh token"})
		return
	}

	expiresAtUtc := time.Now().UTC().Add(h.tokenLifetime)
	refreshExpiresAtUtc := time.Now().UTC().Add(h.refreshTokenLifetime)

	accessToken, expiresAtUtc, err := h.makeTokenWithExpiry(userID, username, sessionID, expiresAtUtc)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "failed to issue access token"})
		return
	}

	_, err = h.db.Exec(ctx, `
UPDATE user_sessions
SET refresh_token_hash = $2,
    access_expires_at = $3,
    refresh_expires_at = $4,
    last_used_at = now()
WHERE id = $1
  AND revoked_at IS NULL;
`, sessionID, hashOpaqueToken(newRefreshToken), expiresAtUtc, refreshExpiresAtUtc)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, authResp{Ok: false, Message: "failed to persist session refresh"})
		return
	}

	writeJSON(w, http.StatusOK, authResp{
		Ok:                  true,
		UserID:              userID,
		Username:            username,
		Email:               strings.TrimSpace(email.String),
		SessionID:           sessionID,
		AccessToken:         accessToken,
		RefreshToken:        newRefreshToken,
		IsNewUser:           false,
		ExpiresAtUtc:        expiresAtUtc,
		RefreshExpiresAtUtc: refreshExpiresAtUtc,
	})
	logHTTPRequestEvent(r, "information", "refresh", "session refreshed",
		"user_id="+fmt.Sprintf("%d", userID),
		"username="+quoteLogValue(username),
		"session_id="+fmt.Sprintf("%d", sessionID))
}

func (h *Handler) Logout(w http.ResponseWriter, r *http.Request) {
	auth, ok := getAuthUser(r)
	if !ok {
		logHTTPRequestEvent(r, "warning", "logout", "unauthorized logout attempt")
		writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "unauthorized"})
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
	defer cancel()

	_, err := h.db.Exec(ctx, `
UPDATE user_sessions
SET revoked_at = COALESCE(revoked_at, now()),
    revoke_reason = COALESCE(revoke_reason, 'logout'),
    refresh_token_hash = '',
    last_used_at = now()
WHERE id = $1
  AND user_id = $2;
`, auth.SessionID, auth.UserID)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"ok": false, "message": "failed to revoke session"})
		return
	}

	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
	logHTTPRequestEvent(r, "information", "logout", "session revoked",
		"user_id="+fmt.Sprintf("%d", auth.UserID),
		"username="+quoteLogValue(auth.Username),
		"session_id="+fmt.Sprintf("%d", auth.SessionID))
}

type tokenPayload struct {
	UID int64  `json:"uid"`
	USR string `json:"usr"`
	SID int64  `json:"sid"`
	EXP int64  `json:"exp"`
}

func (h *Handler) makeToken(userID int64, username string, sessionID int64) (string, time.Time, error) {
	expiresAtUtc := time.Now().UTC().Add(h.tokenLifetime)
	return h.makeTokenWithExpiry(userID, username, sessionID, expiresAtUtc)
}

func (h *Handler) makeTokenWithExpiry(userID int64, username string, sessionID int64, expiresAtUtc time.Time) (string, time.Time, error) {
	headerJSON := []byte(`{"alg":"HS256","typ":"JWT"}`)
	payloadJSON, err := json.Marshal(tokenPayload{
		UID: userID,
		USR: username,
		SID: sessionID,
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

func (h *Handler) issueSessionTokens(
	ctx context.Context,
	r *http.Request,
	userID int64,
	username string,
) (int64, string, time.Time, string, time.Time, error) {
	deviceID, err := h.resolveOrUpsertDevice(ctx, userID, r)
	if err != nil {
		return 0, "", time.Time{}, "", time.Time{}, err
	}

	refreshToken, err := makeOpaqueToken(48)
	if err != nil {
		return 0, "", time.Time{}, "", time.Time{}, err
	}

	expiresAtUtc := time.Now().UTC().Add(h.tokenLifetime)
	refreshExpiresAtUtc := time.Now().UTC().Add(h.refreshTokenLifetime)

	var sessionID int64
	err = h.db.QueryRow(ctx, `
INSERT INTO user_sessions(
    user_id,
    device_id,
    refresh_token_hash,
    access_expires_at,
    refresh_expires_at,
    created_at,
    last_used_at)
VALUES($1, $2, $3, $4, $5, now(), now())
RETURNING id;
`,
		userID,
		deviceID,
		hashOpaqueToken(refreshToken),
		expiresAtUtc,
		refreshExpiresAtUtc).Scan(&sessionID)
	if err != nil {
		return 0, "", time.Time{}, "", time.Time{}, err
	}

	accessToken, expiresAtUtc, err := h.makeTokenWithExpiry(userID, username, sessionID, expiresAtUtc)
	if err != nil {
		return 0, "", time.Time{}, "", time.Time{}, err
	}

	return sessionID, accessToken, expiresAtUtc, refreshToken, refreshExpiresAtUtc, nil
}

func (h *Handler) resolveOrUpsertDevice(ctx context.Context, userID int64, r *http.Request) (int64, error) {
	deviceFingerprint := resolveDeviceFingerprint(r)
	deviceName := strings.TrimSpace(r.Header.Get("X-Veyra-Device-Name"))
	if deviceName == "" {
		deviceName = strings.TrimSpace(r.UserAgent())
	}

	platform := strings.TrimSpace(r.Header.Get("X-Veyra-Device-Platform"))

	if len(deviceName) > 200 {
		deviceName = deviceName[:200]
	}
	if len(platform) > 80 {
		platform = platform[:80]
	}

	var deviceID int64
	err := h.db.QueryRow(ctx, `
INSERT INTO devices(user_id, device_fingerprint, device_name, platform, created_at, last_seen_at)
VALUES($1, $2, NULLIF($3, ''), NULLIF($4, ''), now(), now())
ON CONFLICT (user_id, device_fingerprint)
DO UPDATE SET
    device_name = COALESCE(NULLIF(EXCLUDED.device_name, ''), devices.device_name),
    platform = COALESCE(NULLIF(EXCLUDED.platform, ''), devices.platform),
    last_seen_at = now()
RETURNING id;
`, userID, deviceFingerprint, deviceName, platform).Scan(&deviceID)
	if err != nil {
		return 0, err
	}

	return deviceID, nil
}

func resolveDeviceFingerprint(r *http.Request) string {
	if r != nil {
		explicit := strings.TrimSpace(r.Header.Get("X-Veyra-Device-Fingerprint"))
		if explicit != "" {
			if len(explicit) > 256 {
				return explicit[:256]
			}
			return explicit
		}
	}

	userAgent := ""
	remote := ""
	if r != nil {
		userAgent = strings.TrimSpace(strings.ToLower(r.UserAgent()))
		remote = strings.TrimSpace(r.Header.Get("X-Forwarded-For"))
		if remote == "" {
			host, _, err := net.SplitHostPort(strings.TrimSpace(r.RemoteAddr))
			if err == nil {
				remote = host
			} else {
				remote = strings.TrimSpace(r.RemoteAddr)
			}
		}
	}

	source := strings.TrimSpace(userAgent + "|" + remote)
	if source == "" {
		source = "unknown"
	}

	sum := sha256.Sum256([]byte(source))
	return "auto-" + strings.ToLower(hex.EncodeToString(sum[:16]))
}

func makeOpaqueToken(byteLength int) (string, error) {
	if byteLength < 32 {
		byteLength = 32
	}

	raw := make([]byte, byteLength)
	if _, err := rand.Read(raw); err != nil {
		return "", err
	}

	return base64.RawURLEncoding.EncodeToString(raw), nil
}

func hashOpaqueToken(token string) string {
	sum := sha256.Sum256([]byte(strings.TrimSpace(token)))
	return strings.ToLower(hex.EncodeToString(sum[:]))
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

	if payload.UID <= 0 || payload.USR == "" || payload.SID <= 0 {
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
	UserID    int64
	Username  string
	SessionID int64
}

func (h *Handler) WithAuth(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		header := strings.TrimSpace(r.Header.Get("Authorization"))
		if header == "" || !strings.HasPrefix(strings.ToLower(header), "bearer ") {
			logHTTPRequestEvent(r, "warning", "auth", "missing bearer token")
			writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "missing bearer token"})
			return
		}

		token := strings.TrimSpace(header[len("Bearer "):])
		claims, err := h.parseToken(token)
		if err != nil {
			logHTTPRequestEvent(r, "warning", "auth", "invalid bearer token",
				"error="+quoteLogValue(err.Error()))
			writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "invalid token"})
			return
		}

		var isSessionActive bool
		err = h.db.QueryRow(r.Context(), `
SELECT EXISTS (
    SELECT 1
    FROM user_sessions
    WHERE id = $1
      AND user_id = $2
      AND revoked_at IS NULL
      AND access_expires_at > now()
      AND refresh_expires_at > now());
`, claims.SID, claims.UID).Scan(&isSessionActive)
		if err != nil || !isSessionActive {
			logHTTPRequestEvent(r, "warning", "auth", "invalid session",
				"user_id="+fmt.Sprintf("%d", claims.UID),
				"username="+quoteLogValue(claims.USR),
				"session_id="+fmt.Sprintf("%d", claims.SID))
			writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "message": "invalid session"})
			return
		}

		_, _ = h.db.Exec(r.Context(), `UPDATE user_sessions SET last_used_at = now() WHERE id = $1`, claims.SID)
		log.Printf("level=debug operation=auth message=%q user_id=%d username=%q session_id=%d path=%s", "authorized request", claims.UID, claims.USR, claims.SID, r.URL.Path)

		ctx := context.WithValue(r.Context(), ctxUserKey, authUser{
			UserID:    claims.UID,
			Username:  claims.USR,
			SessionID: claims.SID,
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

func isValidEmail(value string) bool {
	if strings.TrimSpace(value) == "" {
		return false
	}
	parsed, err := mail.ParseAddress(value)
	if err != nil {
		return false
	}
	return strings.EqualFold(strings.TrimSpace(parsed.Address), strings.TrimSpace(value))
}

func writeJSON(w http.ResponseWriter, code int, payload any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(payload)
}
