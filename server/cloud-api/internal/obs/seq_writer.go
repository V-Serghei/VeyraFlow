package obs

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"
)

type seqCLEFWriter struct {
	service   string
	stdout    io.Writer
	stderr    io.Writer
	client    *http.Client
	endpoint  string
	mu        sync.Mutex
	buffer    bytes.Buffer
	queue     chan string
	closeOnce sync.Once
	wg        sync.WaitGroup
}

func ConfigureStandardLogger(service, seqURL string) func() {
	writer := newSeqCLEFWriter(service, seqURL)
	log.SetFlags(log.LstdFlags | log.LUTC | log.Lmicroseconds)
	log.SetOutput(writer)
	return writer.Close
}

func newSeqCLEFWriter(service, seqURL string) *seqCLEFWriter {
	writer := &seqCLEFWriter{
		service:  strings.TrimSpace(service),
		stdout:   os.Stdout,
		stderr:   os.Stderr,
		endpoint: normalizeSeqEndpoint(seqURL),
		client: &http.Client{
			Timeout: 4 * time.Second,
		},
		queue: make(chan string, 1024),
	}

	if writer.endpoint != "" {
		writer.wg.Add(1)
		go writer.run()
	}

	return writer
}

func (w *seqCLEFWriter) Write(p []byte) (int, error) {
	if _, err := w.stdout.Write(p); err != nil {
		return 0, err
	}

	if w.endpoint == "" {
		return len(p), nil
	}

	w.mu.Lock()
	defer w.mu.Unlock()

	for _, chunk := range bytes.SplitAfter(p, []byte{'\n'}) {
		if len(chunk) == 0 {
			continue
		}

		_, _ = w.buffer.Write(chunk)
		if chunk[len(chunk)-1] != '\n' {
			continue
		}

		line := strings.TrimSpace(w.buffer.String())
		w.buffer.Reset()
		if line == "" {
			continue
		}

		select {
		case w.queue <- line:
		default:
			fmt.Fprintf(w.stderr, "seq writer queue overflow, dropping log line: %s\n", line)
		}
	}

	return len(p), nil
}

func (w *seqCLEFWriter) Close() {
	w.closeOnce.Do(func() {
		if w.endpoint == "" {
			return
		}

		w.mu.Lock()
		line := strings.TrimSpace(w.buffer.String())
		w.buffer.Reset()
		w.mu.Unlock()

		if line != "" {
			select {
			case w.queue <- line:
			default:
				fmt.Fprintf(w.stderr, "seq writer queue overflow during close, dropping log line: %s\n", line)
			}
		}

		close(w.queue)
		w.wg.Wait()
	})
}

func (w *seqCLEFWriter) run() {
	defer w.wg.Done()

	for line := range w.queue {
		if err := w.post(line); err != nil {
			fmt.Fprintf(w.stderr, "seq writer post failed: %v\n", err)
		}
	}
}

func (w *seqCLEFWriter) post(line string) error {
	if line == "" || w.endpoint == "" {
		return nil
	}

	event := map[string]any{
		"@t":      time.Now().UTC().Format(time.RFC3339Nano),
		"@m":      line,
		"@l":      inferLevel(line),
		"Service": w.service,
	}

	payload, err := json.Marshal(event)
	if err != nil {
		return err
	}
	payload = append(payload, '\n')

	req, err := http.NewRequest(http.MethodPost, w.endpoint, bytes.NewReader(payload))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/vnd.serilog.clef")

	resp, err := w.client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode >= http.StatusMultipleChoices {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 2048))
		return fmt.Errorf("seq responded with %d: %s", resp.StatusCode, strings.TrimSpace(string(body)))
	}

	return nil
}

func normalizeSeqEndpoint(seqURL string) string {
	trimmed := strings.TrimSpace(seqURL)
	if trimmed == "" {
		return ""
	}

	trimmed = strings.TrimRight(trimmed, "/")
	if strings.Contains(trimmed, "/api/events/raw") {
		return trimmed
	}

	return trimmed + "/api/events/raw?clef"
}

func inferLevel(line string) string {
	normalized := strings.ToLower(line)
	switch {
	case strings.Contains(normalized, "[err]"),
		strings.Contains(normalized, " level=error"),
		strings.Contains(normalized, " error="),
		strings.Contains(normalized, " panic"),
		strings.Contains(normalized, " fatal"):
		return "Error"
	case strings.Contains(normalized, "[wrn]"),
		strings.Contains(normalized, " level=warn"),
		strings.Contains(normalized, " warning"),
		strings.Contains(normalized, " failed"),
		strings.Contains(normalized, " rejected"):
		return "Warning"
	case strings.Contains(normalized, "[dbg]"),
		strings.Contains(normalized, " level=debug"),
		strings.Contains(normalized, " debug"):
		return "Debug"
	default:
		return "Information"
	}
}
