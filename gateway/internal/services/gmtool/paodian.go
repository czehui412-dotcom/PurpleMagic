package gmtool

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"strconv"
	"strings"
	"time"

	"dnf90.local/gateway/internal/modules/dnf/repository"
)

// Paodian (在线泡点) grants a configured amount of one currency to every
// account that owns at least one character in the authoritative online snapshot.
// One account receives at most one grant per cycle. The gateway is the wallet
// writer; the game server projection is a separate, later step.
const paodianScope = "purplemagic:management:paodian"

var (
	errPaodianUnavailable = errors.New("paodian repositories unavailable")
	errPaodianOnline      = errors.New("online snapshot unavailable")
)

// PaodianConfig is persisted in the Settings store under paodianScope.
type PaodianConfig struct {
	Enabled         bool   `json:"enabled"`
	IntervalSeconds int    `json:"intervalSeconds"`
	Amount          int64  `json:"amount"`
	Currency        string `json:"currency"`
}

func DefaultPaodianConfig() PaodianConfig {
	return PaodianConfig{Enabled: false, IntervalSeconds: 60, Amount: 30, Currency: "token"}
}

func (c PaodianConfig) valid() bool {
	if c.IntervalSeconds < 10 || c.IntervalSeconds > 86400 {
		return false
	}
	if c.Amount < 1 || c.Amount > 1000000 {
		return false
	}
	return c.Currency == "cera" || c.Currency == "token" || c.Currency == "joytoken"
}

// metadataKey maps the configured currency to the account metadata key the
// game reads as its authoritative wallet.
func (c PaodianConfig) metadataKey() string {
	switch c.Currency {
	case "cera":
		return "account_cera"
	case "token":
		return "account_token_cera"
	case "joytoken":
		return "account_happy_token_cera"
	}
	return ""
}

func (c PaodianConfig) label() string {
	switch c.Currency {
	case "cera":
		return "点券"
	case "token":
		return "代币券"
	case "joytoken":
		return "欢乐代币券"
	}
	return "代币券"
}

func (s *Server) loadPaodianConfig(ctx context.Context) (PaodianConfig, error) {
	g, err := s.repositories()
	if err != nil || g.Settings == nil {
		return DefaultPaodianConfig(), errPaodianUnavailable
	}
	rec, found, err := g.Settings.Load(ctx, paodianScope)
	if err != nil {
		return DefaultPaodianConfig(), err
	}
	if !found {
		return DefaultPaodianConfig(), nil
	}
	var cfg PaodianConfig
	if json.Unmarshal(paodianBytes(rec.Values), &cfg) != nil || !cfg.valid() {
		return DefaultPaodianConfig(), nil // corrupt config fails closed to disabled default
	}
	return cfg, nil
}

// paodianBytes reconstructs JSON from the flat Settings values map. The config
// is stored as a single "config" value to keep the Settings row simple.
func paodianBytes(values map[string]string) []byte {
	if raw := strings.TrimSpace(values["config"]); raw != "" {
		return []byte(raw)
	}
	return nil
}

func (s *Server) savePaodianConfig(ctx context.Context, cfg PaodianConfig) error {
	if !cfg.valid() {
		return errors.New("paodian config invalid")
	}
	g, err := s.repositories()
	if err != nil || g.Settings == nil {
		return errPaodianUnavailable
	}
	raw, err := json.Marshal(cfg)
	if err != nil {
		return err
	}
	rec, found, err := g.Settings.Load(ctx, paodianScope)
	if err != nil {
		return err
	}
	if !found {
		rec = repository.SettingsRecord{Scope: paodianScope}
	}
	if rec.Values == nil {
		rec.Values = make(map[string]string)
	}
	rec.Values["config"] = string(raw)
	rec.UpdatedAt = time.Now().UTC().Truncate(time.Microsecond)
	return g.Settings.Save(ctx, rec)
}

// paodianCycle resolves the authoritative online snapshot, maps each online
// character to its owning account (one account once), and grants in a single
// account-management transaction per account. Receipts use a deterministic key
// so a repeated cycle in the same second cannot double-credit.
type PaodianCycle struct {
	CycleID      string `json:"cycleId"`
	RanAt        string `json:"ranAt"`
	OnlineCount  int    `json:"onlineCount"`
	AccountCount int    `json:"accountCount"`
	Granted      int    `json:"granted"`
	Skipped      int    `json:"skipped"`
	Currency     string `json:"currency"`
	Amount       int64  `json:"amount"`
}

func (s *Server) runPaodianCycle(ctx context.Context, cfg PaodianConfig, cycleID string) (PaodianCycle, error) {
	result := PaodianCycle{CycleID: cycleID, RanAt: time.Now().UTC().Format(time.RFC3339), Currency: cfg.Currency, Amount: cfg.Amount}
	if cycleID == "" {
		cycleID = "c" + strconv.FormatInt(time.Now().UTC().Unix(), 10)
		result.CycleID = cycleID
	}
	if s.deps.Runtime == nil {
		return result, errPaodianOnline
	}
	snapshot, err := s.deps.Runtime.OnlineSnapshot(ctx)
	if err != nil || snapshot == nil || !snapshot.Complete {
		return result, errPaodianOnline
	}
	result.OnlineCount = len(snapshot.Members)
	g, err := s.repositories()
	if err != nil || g.Account == nil || g.Character == nil {
		return result, errPaodianUnavailable
	}
	uow, ok := g.Account.(repository.AccountManagementUnitOfWork)
	if !ok {
		return result, errPaodianUnavailable
	}

	// characterID -> accountID, deduplicated by account.
	accounts := make([]string, 0, len(snapshot.Members))
	seen := make(map[string]bool)
	for _, member := range snapshot.Members {
		ch, found, e := g.Character.Load(ctx, strconv.Itoa(int(member.CharacterID)))
		if e != nil || !found || strings.TrimSpace(ch.AccountID) == "" {
			continue // orphan / deleted character: skip, never guess
		}
		id := strings.TrimSpace(ch.AccountID)
		if seen[id] {
			continue
		}
		seen[id] = true
		accounts = append(accounts, id)
	}
	result.AccountCount = len(accounts)

	key := paodianKey(cycleID, cfg)
	for _, accountID := range accounts {
		granted, e := grantPaodianToAccount(ctx, uow, accountID, cfg, key)
		if e != nil {
			result.Skipped++
			s.paodianLog("gm-paodian-grant-failed", "account", accountID, "error", e)
			continue
		}
		if granted {
			result.Granted++
		} else {
			result.Skipped++ // receipt already present: no second credit
		}
	}
	return result, nil
}

func paodianKey(cycleID string, cfg PaodianConfig) string {
	raw := cycleID + "|" + cfg.Currency + "|" + strconv.FormatInt(cfg.Amount, 10)
	h := sha256.Sum256([]byte(raw))
	return "paodian:" + hex.EncodeToString(h[:])
}

// grantPaodianToAccount credits one account inside the account-management
// transaction: wallet change and receipt commit together. Returns false when
// the idempotency receipt already exists (no second credit).
func grantPaodianToAccount(ctx context.Context, uow repository.AccountManagementUnitOfWork, accountID string, cfg PaodianConfig, key string) (bool, error) {
	credited := false
	err := uow.WithinAccountManagement(ctx, accountID, func(accounts repository.AccountRepository, receipts repository.ManagementReceiptStore) error {
		stored, found, e := receipts.Load(ctx, key)
		if e != nil {
			return e
		}
		if found {
			// Verify the receipt belongs to this account; a mismatched stored
			// payload is a real conflict, not a silent skip.
			var old paodianStored
			if e = json.Unmarshal(stored, &old); e != nil {
				return e
			}
			if old.AccountID != accountID {
				return fmt.Errorf("paodian receipt account mismatch")
			}
			return nil
		}
		acc, found, e := accounts.Load(ctx, accountID)
		if e != nil || !found || acc.AccountID != accountID {
			if !found {
				return fmt.Errorf("paodian account missing")
			}
			return e
		}
		acc = repository.CloneAccount(acc)
		if acc.Metadata == nil {
			acc.Metadata = make(map[string]string)
		}
		keyName := cfg.metadataKey()
		before := parsePaodianBalance(acc.Metadata[keyName])
		if before > math.MaxInt64-cfg.Amount {
			return fmt.Errorf("paodian balance overflow")
		}
		after := before + cfg.Amount
		acc.Metadata[keyName] = strconv.FormatInt(after, 10)
		acc.UpdatedAt = time.Now().UTC().Truncate(time.Microsecond)
		if cfg.Currency == "cera" {
			// cera is a bounded wallet column pair; SaveAccountCurrency also
			// requires a monotonic gm_currency_sequence_v1.
			seq := int64(0)
			if value := acc.Metadata["gm_currency_sequence_v1"]; value != "" {
				seq, e = strconv.ParseInt(value, 10, 64)
				if e != nil || seq < 0 || seq == math.MaxInt64 {
					return fmt.Errorf("invalid currency sequence")
				}
			}
			acc.Metadata["gm_currency_sequence_v1"] = strconv.FormatInt(seq+1, 10)
			writer, ok := accounts.(repository.AccountCurrencyWriter)
			if !ok {
				return errPaodianUnavailable
			}
			if e = writer.SaveAccountCurrency(ctx, acc); e != nil {
				return e
			}
		} else {
			// token/joy live in account metadata; persist only that one key so
			// unrelated metadata and receipts stay intact, in the same txn.
			if e = repository.SaveAccountMetadataEntry(ctx, accounts, acc, keyName, strconv.FormatInt(after, 10), acc.UpdatedAt); e != nil {
				return e
			}
		}
		receipt := paodianStored{AccountID: accountID, Currency: cfg.Currency, Amount: cfg.Amount, CycleAt: time.Now().UTC()}
		payload, e := json.Marshal(receipt)
		if e != nil {
			return e
		}
		if e = receipts.Append(ctx, key, payload); e != nil {
			return e
		}
		credited = true
		return nil
	})
	return credited, err
}

type paodianStored struct {
	AccountID string    `json:"accountId"`
	Currency  string    `json:"currency"`
	Amount    int64     `json:"amount"`
	CycleAt   time.Time `json:"cycleAt"`
}

func parsePaodianBalance(raw string) int64 {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return 0
	}
	n, err := strconv.ParseInt(raw, 10, 64)
	if err != nil || n < 0 {
		return 0
	}
	return n
}

func (s *Server) paodianLog(event string, fields ...any) {
	if s.cfg.Logger != nil {
		s.cfg.Logger(event, fields...)
	}
}

// paodianNoticeText builds the default system notice body. "current" is the
// per-cycle amount, not the wallet total, matching the documented wording.
func paodianNoticeText(cfg PaodianConfig, sessionTotal int64) string {
	return fmt.Sprintf("每%d秒获得%d%s，当前获得%d%s，本次登录共计获得%d%s。",
		cfg.IntervalSeconds, cfg.Amount, cfg.label(), cfg.Amount, cfg.label(), sessionTotal, cfg.label())
}

// onlineSnapshotProbe is used by tests and health checks without granting.
func (s *Server) onlineSnapshotProbe(ctx context.Context) (int, error) {
	if s.deps.Runtime == nil {
		return 0, errPaodianOnline
	}
	snapshot, err := s.deps.Runtime.OnlineSnapshot(ctx)
	if err != nil || snapshot == nil || !snapshot.Complete {
		return 0, errPaodianOnline
	}
	return len(snapshot.Members), nil
}
