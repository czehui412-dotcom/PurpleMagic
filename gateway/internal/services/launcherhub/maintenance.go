package launcherhub

import (
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"time"
)

// maintenanceEventPath is served directly by the player gateway on 8443.
// Launchers open one long-lived SSE connection here and receive maintenance
// changes immediately instead of polling the edge static file.
const maintenanceEventPath = "/launcher/v2/maintenance/stream"

// MaintenanceSnapshot is the wire payload of an SSE maintenance event. It is
// intentionally a small, flat, forward-compatible shape.
type MaintenanceSnapshot struct {
	Enabled   bool   `json:"enabled"`
	Message   string `json:"message,omitempty"`
	Since     string `json:"since,omitempty"`
	Kicked    int    `json:"kicked,omitempty"`
	UpdatedAt string `json:"updatedAt,omitempty"`
}

func maintenanceStatusPath() string {
	dir := os.Getenv("DNF90_MAINTENANCE_DIR")
	if dir == "" {
		dir = "/data/status"
	}
	return filepath.Join(dir, maintenanceJSONName)
}

const maintenanceJSONName = "maintenance.json"

// readMaintenanceStatusFile returns the authoritative status mirrored by the
// GM maintenance handler. Missing/invalid files are treated as "not enabled".
func readMaintenanceStatusFile() MaintenanceSnapshot {
	data, err := os.ReadFile(maintenanceStatusPath())
	if err != nil {
		return MaintenanceSnapshot{}
	}
	var m MaintenanceSnapshot
	if json.Unmarshal(data, &m) != nil {
		return MaintenanceSnapshot{}
	}
	return m
}

// BroadcastMaintenance fans out a maintenance change to every connected
// launcher. Called in-process by the GM maintenance handler after the status
// file has been written, so subscribers see the change immediately.
func (h *Hub) BroadcastMaintenance(enabled bool, message, since string, kicked int) {
	m := MaintenanceSnapshot{
		Enabled: enabled, Message: message, Since: since, Kicked: kicked,
		UpdatedAt: time.Now().UTC().Format(time.RFC3339),
	}
	h.mu.Lock()
	h.maintenance = m
	subs := make([]chan MaintenanceSnapshot, 0, len(h.maintenanceSubs))
	for ch := range h.maintenanceSubs {
		subs = append(subs, ch)
	}
	h.mu.Unlock()
	for _, ch := range subs {
		select {
		case ch <- m:
		default: // never block the GM request on a slow/stuck subscriber
		}
	}
}

// serveMaintenanceStream implements the SSE endpoint. It sends the current
// file state as the first frame (so reconnecting clients catch up), then
// streams broadcast changes and periodic heartbeat comments.
func (h *Hub) serveMaintenanceStream(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet || r.URL.RawQuery != "" {
		http.NotFound(w, r)
		return
	}
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, "streaming unsupported", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Connection", "keep-alive")
	w.Header().Set("X-Accel-Buffering", "no")

	ch := make(chan MaintenanceSnapshot, 4)
	h.mu.Lock()
	if h.maintenanceSubs == nil {
		h.maintenanceSubs = make(map[chan MaintenanceSnapshot]struct{})
	}
	h.maintenanceSubs[ch] = struct{}{}
	h.mu.Unlock()
	defer func() {
		h.mu.Lock()
		delete(h.maintenanceSubs, ch)
		h.mu.Unlock()
	}()

	write := func(m MaintenanceSnapshot) bool {
		data, e := json.Marshal(m)
		if e != nil {
			return false
		}
		if _, e = io.WriteString(w, "event: maintenance\ndata: "+string(data)+"\n\n"); e != nil {
			return false
		}
		flusher.Flush()
		return true
	}

	// Baseline from the mirrored status file. Existing subscribers may already
	// have a broadcast queued; both frames are harmless (idempotent apply).
	if !write(readMaintenanceStatusFile()) {
		return
	}
	ticker := time.NewTicker(25 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case m := <-ch:
			if !write(m) {
				return
			}
		case <-ticker.C:
			if _, e := io.WriteString(w, ": ping\n\n"); e != nil {
				return
			}
			flusher.Flush()
		}
	}
}
