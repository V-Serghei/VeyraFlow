package handlers

import (
	"log"
	"net/http"
	"strings"
)

func logHTTPRequestEvent(r *http.Request, level string, operation string, message string, fields ...string) {
	remote := ""
	if r != nil {
		remote = strings.TrimSpace(r.RemoteAddr)
	}

	parts := []string{
		"level=" + strings.TrimSpace(level),
		"operation=" + strings.TrimSpace(operation),
		"message=" + quoteLogValue(message),
	}

	if r != nil {
		parts = append(parts,
			"method="+r.Method,
			"path="+r.URL.Path,
			"remote="+quoteLogValue(remote))
	}

	for _, field := range fields {
		if strings.TrimSpace(field) == "" {
			continue
		}
		parts = append(parts, field)
	}

	log.Printf(strings.Join(parts, " "))
}

func quoteLogValue(value string) string {
	trimmed := strings.TrimSpace(value)
	if trimmed == "" {
		return `""`
	}

	escaped := strings.ReplaceAll(trimmed, `"`, `'`)
	return `"` + escaped + `"`
}
