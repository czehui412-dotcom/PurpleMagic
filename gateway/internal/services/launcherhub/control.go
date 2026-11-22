package launcherhub

import (
	"encoding/json"
	"io"
	"net/http"
	"strconv"
	"time"
)

// controlEventPath is the signed, general-purpose push channel. Maintenance is
// the first frame type; policy/command frames can be added without a new
// endpoint. Seqs are monotonic per process so clients can detect gaps and
// resume with ?since=<seq>.
const controlEventPath = "/launcher/v2/control/stream"

// ControlFrame is the inner payload; the wire form is a signed Envelope exactly
// like bootstrap.json, so clients verify with the same embedded trust root.
type ControlFrame struct {
	Seq     int64           `json:"seq"`
	Type    string          `json:"type"`
	Payload json.RawMessage `json:"payload"`
	At      string          `json:"at"`
}

// controlWire is the SSE `data:` line. `envelope` is the base64(payload)+sig.
type controlWire struct {
	Envelope string `json:"envelope"`
}

func (h *Hub) nextControlSeq() int64 {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.controlSeq++
	return h.controlSeq
}

// BroadcastControl signs and fans out a control frame. Non-blocking per
// subscriber so a stuck client never delays GM operations.
func (h *Hub) BroadcastControl(frameType string, payload any) {
	raw, e := json.Marshal(payload)
	if e != nil {
		return
	}
	frame := ControlFrame{Seq: h.nextControlSeq(), Type: frameType, Payload: raw, At: time.Now().UTC().Format(time.RFC3339)}
	signed, e := sign(frame, h.key)
	if e != nil {
		return
	}
	wire, e := json.Marshal(controlWire{Envelope: string(signed)})
	if e != nil {
		return
	}
	h.mu.Lock()
	subs := make([]chan []byte, 0, len(h.controlSubs))
	for ch := range h.controlSubs {
		subs = append(subs, ch)
	}
	h.mu.Unlock()
	for _, ch := range subs {
		select {
		case ch <- wire:
		default:
		}
	}
}

// serveControlStream serves signed control frames. It replays the current
// maintenance state as the first frame so reconnecting clients catch up.
func (h *Hub) serveControlStream(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.NotFound(w, r)
		return
	}
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, "streaming unsupported", http.StatusInternalServerError)
		return
	}
	var since int64
	if raw := r.URL.Query().Get("since"); raw != "" {
		since, _ = strconv.ParseInt(raw, 10, 64)
	}
	w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Connection", "keep-alive")
	w.Header().Set("X-Accel-Buffering", "no")

	ch := make(chan []byte, 8)
	h.mu.Lock()
	if h.controlSubs == nil {
		h.controlSubs = make(map[chan []byte]struct{})
	}
	h.controlSubs[ch] = struct{}{}
	current := h.controlSeq
	h.mu.Unlock()
	defer func() {
		h.mu.Lock()
		delete(h.controlSubs, ch)
		h.mu.Unlock()
	}()

	write := func(wire []byte) bool {
		if _, e := io.WriteString(w, "event: control\ndata: "+string(wire)+"\n\n"); e != nil {
			return false
		}
		flusher.Flush()
		return true
	}

	// Gap detection: tell the client to re-fetch bootstrap/state if it missed frames.
	if since > 0 && since+1 < current {
		_ = write([]byte(`{"envelope":"","gap":true}`))
	}
	// Replay current maintenance as frame 0 semantics (idempotent on client).
	payload, _ := json.Marshal(readMaintenanceStatusFile())
	frame := ControlFrame{Seq: current, Type: "maintenance", Payload: payload, At: time.Now().UTC().Format(time.RFC3339)}
	if signed, e := sign(frame, h.key); e == nil {
		if wire, e := json.Marshal(controlWire{Envelope: string(signed)}); e == nil {
			if !write(wire) {
				return
			}
		}
	}
	ticker := time.NewTicker(25 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case wire := <-ch:
			if !write(wire) {
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
