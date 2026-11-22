package gmtool

import (
	"net/http"
	"strings"
	"time"
)

// v1Paodian serves GET /api/v1/paodian (config + last cycle) and
// POST /api/v1/paodian with op: save | run | status. Write auth/CSRF is handled
// by the v1Routes wrapper via the `writes` mux; no per-handler guard needed.
func (s *Server) v1Paodian(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	cfg, err := s.loadPaodianConfig(ctx)
	if err != nil {
		s.v1Unavailable(w, r, err)
		return
	}
	var latest PaodianCycle
	var lastAt string
	if s.paodianWorker != nil {
		latest, lastAt = s.paodianWorker.snapshot()
	}
	if r.Method == http.MethodGet {
		v1Reply(w, r, 200, v1Envelope{Data: map[string]any{
			"config": cfg, "lastCycle": latest, "lastAt": lastAt,
		}})
		return
	}
	if r.Method != http.MethodPost {
		http.NotFound(w, r)
		return
	}
	var body struct {
		Op              string `json:"op"`
		Enabled         *bool  `json:"enabled"`
		IntervalSeconds *int   `json:"intervalSeconds"`
		Amount          *int64 `json:"amount"`
		Currency        string `json:"currency"`
	}
	if !v1Decode(w, r, &body) {
		return
	}
	switch strings.ToLower(strings.TrimSpace(body.Op)) {
	case "save":
		if body.Enabled != nil {
			cfg.Enabled = *body.Enabled
		}
		if body.IntervalSeconds != nil {
			cfg.IntervalSeconds = *body.IntervalSeconds
		}
		if body.Amount != nil {
			cfg.Amount = *body.Amount
		}
		if strings.TrimSpace(body.Currency) != "" {
			cfg.Currency = strings.TrimSpace(body.Currency)
		}
		if !cfg.valid() {
			v1Fail(w, r, 400, "invalid_settings", "泡点配置不合法（间隔 10~86400 秒，数量 1~1000000）", false)
			return
		}
		if err := s.savePaodianConfig(ctx, cfg); err != nil {
			s.v1Unavailable(w, r, err)
			return
		}
		v1Reply(w, r, 200, v1Envelope{Data: map[string]any{"config": cfg}})
		if s.paodianWorker != nil {
			s.paodianWorker.trigger()
		}
		return
	case "run":
		cycleID := "m" + time.Now().UTC().Format("20060102T150405.000000000")
		result, err := s.runPaodianCycle(ctx, cfg, cycleID)
		if err != nil {
			if err == errPaodianOnline {
				v1Fail(w, r, 503, "online_unavailable", "无法读取在线快照", true)
			} else {
				s.v1Unavailable(w, r, err)
			}
			return
		}
		v1Reply(w, r, 200, v1Envelope{Data: map[string]any{"cycle": result}})
		return
	case "status":
		v1Reply(w, r, 200, v1Envelope{Data: map[string]any{
			"config": cfg, "lastCycle": latest, "lastAt": lastAt,
		}})
		return
	default:
		v1Fail(w, r, 400, "invalid_settings", "op 必须是 save|run|status", false)
	}
}
