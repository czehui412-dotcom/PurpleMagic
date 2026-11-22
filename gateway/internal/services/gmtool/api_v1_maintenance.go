package gmtool

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"time"

	"dnf90.local/gateway/internal/diagnostics"
	"dnf90.local/gateway/internal/modules/dnf/adminapi"
)

const maintenanceFileName = "maintenance.json"

// v1Maintenance handles GET/POST /api/v1/maintenance.
// POST forwards {op:enter|leave|status} to the game server admin API, then
// mirrors the resulting state to the shared status directory so the launcher
// can read it from the public edge without touching the (restarting) gateway.
func (s *Server) v1Maintenance(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodGet {
		st := s.readMaintenanceStatus(r)
		v1Reply(w, r, http.StatusOK, v1Envelope{Data: map[string]any{"maintenance": st}})
		return
	}

	var body struct {
		Op      string `json:"op"`
		Message string `json:"message,omitempty"`
		Actor   string `json:"actor,omitempty"`
	}
	if !v1DecodeLimit(w, r, &body, 2048) {
		return
	}
	if body.Op != "enter" && body.Op != "leave" && body.Op != "status" {
		v1Fail(w, r, http.StatusBadRequest, "invalid_request", "op must be enter|leave|status", false)
		return
	}

	result, err := s.deps.Runtime.Call(r.Context(), adminapi.Request{
		Version:     1,
		Action:      "maintenance",
		Maintenance: &adminapi.MaintenanceChange{Op: body.Op, Message: body.Message, Actor: body.Actor},
	})
	if err != nil || !result.OK {
		code, msg := "dependency_unavailable", "game server unavailable"
		if err != nil {
			msg = err.Error()
		} else if result.Error != "" {
			code, msg = "game_error", result.Error
		}
		v1Fail(w, r, http.StatusBadGateway, code, msg, true)
		return
	}

	st := result.Maintenance
	if st == nil {
		st = &adminapi.MaintenanceStatus{Enabled: body.Op == "enter"}
	}
	if err := s.writeMaintenanceStatus(r, st); err != nil {
		// Maintenance flag is on the game server already; report the mirror
		// failure but do not roll back — the operator can retry.
		diagnostics.Code(r.Context(), "maintenance_mirror_failed")
		v1Reply(w, r, http.StatusOK, v1Envelope{Data: map[string]any{
			"maintenance": st,
			"warning":     "status file not written: " + err.Error(),
		}})
		return
	}
	// Push the change to every connected launcher immediately. This runs in the
	// same PlayerGateway process as the launcher SSE hub, so no extra hop.
	if s.onMaintenance != nil {
		s.onMaintenance(st.Enabled, st.Message, st.Since, st.Kicked)
	}
	v1Reply(w, r, http.StatusOK, v1Envelope{Data: map[string]any{"maintenance": st}})
}

func (s *Server) maintenanceDir() string {
	if dir := os.Getenv("DNF90_MAINTENANCE_DIR"); dir != "" {
		return dir
	}
	return "/data/status"
}

func (s *Server) writeMaintenanceStatus(r *http.Request, st *adminapi.MaintenanceStatus) error {
	dir := s.maintenanceDir()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	payload := map[string]any{
		"enabled":   st.Enabled,
		"message":   st.Message,
		"since":     st.Since,
		"kicked":    st.Kicked,
		"updatedAt": time.Now().UTC().Format(time.RFC3339),
	}
	data, err := json.MarshalIndent(payload, "", "  ")
	if err != nil {
		return err
	}
	tmp := filepath.Join(dir, maintenanceFileName+".tmp")
	if err := os.WriteFile(tmp, append(data, '\n'), 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, filepath.Join(dir, maintenanceFileName))
}

func (s *Server) readMaintenanceStatus(r *http.Request) *adminapi.MaintenanceStatus {
	data, err := os.ReadFile(filepath.Join(s.maintenanceDir(), maintenanceFileName))
	if err != nil {
		return &adminapi.MaintenanceStatus{Enabled: false}
	}
	var st adminapi.MaintenanceStatus
	if json.Unmarshal(data, &st) != nil {
		return &adminapi.MaintenanceStatus{Enabled: false}
	}
	return &st
}
