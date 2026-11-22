package gmtool

import (
	"context"
	"strconv"
	"sync"
	"time"
)

// paodianWorker is a single periodic scheduler. It must be started from the
// shared newServer construction path so standalone and managed modes behave
// identically, and must be cancelled from Stop to avoid orphan grants.
type paodianWorker struct {
	server  *Server
	mu      sync.Mutex
	latest  PaodianCycle
	lastAt  string
	running bool
	wake    chan struct{}
}

func (s *Server) startPaodianWorker(ctx context.Context) {
	w := &paodianWorker{server: s, wake: make(chan struct{}, 1)}
	s.paodianWorker = w
	go w.run(ctx)
}

func (w *paodianWorker) run(ctx context.Context) {
	defer close(w.server.paodianDone)
	timer := time.NewTicker(time.Second)
	defer timer.Stop()
	for {
		cfg, err := w.server.loadPaodianConfig(ctx)
		if err != nil {
			w.server.paodianLog("gm-paodian-config-failed", "error", err)
		}
		interval := time.Duration(cfg.IntervalSeconds) * time.Second
		if cfg.IntervalSeconds <= 0 {
			interval = time.Minute
		}
		timer.Reset(interval)
		// Immediate run at startup when enabled; otherwise wait for tick.
		if cfg.Enabled {
			w.execute(ctx, cfg)
		}
		select {
		case <-ctx.Done():
			return
		case <-w.wake:
			continue
		case <-timer.C:
		}
	}
}

// trigger requests an immediate re-evaluation of the schedule.
func (w *paodianWorker) trigger() {
	if w == nil || w.wake == nil {
		return
	}
	select {
	case w.wake <- struct{}{}:
	default:
	}
}

func (w *paodianWorker) execute(ctx context.Context, cfg PaodianConfig) {
	w.mu.Lock()
	if w.running {
		w.mu.Unlock()
		return
	}
	w.running = true
	w.mu.Unlock()
	defer func() {
		w.mu.Lock()
		w.running = false
		w.mu.Unlock()
	}()

	cycleID := "c" + strconv.FormatInt(time.Now().UTC().UnixNano(), 10)
	result, err := w.server.runPaodianCycle(ctx, cfg, cycleID)
	if err != nil {
		w.server.paodianLog("gm-paodian-cycle-failed", "error", err)
		return
	}
	w.mu.Lock()
	w.latest = result
	w.lastAt = result.RanAt
	w.mu.Unlock()
	w.server.paodianLog("gm-paodian-cycle", "online", result.OnlineCount, "accounts", result.AccountCount, "granted", result.Granted, "skipped", result.Skipped)
}

func (w *paodianWorker) snapshot() (PaodianCycle, string) {
	if w == nil {
		return PaodianCycle{}, ""
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	return w.latest, w.lastAt
}
