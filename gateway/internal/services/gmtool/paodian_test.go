package gmtool

import (
	"context"
	"sync"
	"testing"

	dnfrepo "dnf90.local/gateway/internal/modules/dnf/repository"
	"dnf90.local/gateway/internal/modules/dnf/repository/memory"
)

// memoryReceiptUOW adapts the in-memory repository with a receipt store so the
// grant logic can be exercised without MySQL/sqlmock. Production still uses the
// MySQL UOW where the receipt and wallet commit in one transaction.
type memoryReceipts struct {
	mu       sync.Mutex
	receipts map[string][]byte
}

func (u *memoryReceipts) Load(_ context.Context, key string) ([]byte, bool, error) {
	u.mu.Lock()
	defer u.mu.Unlock()
	raw, ok := u.receipts[key]
	return raw, ok, nil
}
func (u *memoryReceipts) Append(_ context.Context, key string, raw []byte) error {
	u.mu.Lock()
	defer u.mu.Unlock()
	if u.receipts == nil {
		u.receipts = make(map[string][]byte)
	}
	u.receipts[key] = raw
	return nil
}

// currencyAccountRepository adds the AccountCurrencyWriter capability that
// MySQL's transaction-scoped account repo provides, so the cera path is tested.
type currencyAccountRepository struct{ dnfrepo.AccountRepository }

func (r *currencyAccountRepository) SaveAccountCurrency(ctx context.Context, a dnfrepo.AccountRecord) error {
	return r.Save(ctx, a)
}

type memoryReceiptUOW struct {
	dnfrepo.AccountRepository
	*memoryReceipts
}

func (u *memoryReceiptUOW) WithinAccountManagement(_ context.Context, _ string, fn func(dnfrepo.AccountRepository, dnfrepo.ManagementReceiptStore) error) error {
	return fn(&currencyAccountRepository{u.AccountRepository}, u.memoryReceipts)
}
func (u *memoryReceiptUOW) AccountManagementLog(context.Context, string, int64, int) ([]dnfrepo.ManagementLogRow, error) {
	return nil, nil
}

func paodianFixture(t *testing.T) (context.Context, dnfrepo.AccountRepository, *memoryReceiptUOW, string) {
	t.Helper()
	group := memory.NewMemoryGroup()
	ctx := context.Background()
	const accountID = "dnf:paodian-test-account"
	if err := group.Account.Save(ctx, dnfrepo.AccountRecord{
		AccountID: accountID,
		Metadata: map[string]string{
			"account_cera":                 "100",
			"gm_currency_sequence_v1":      "1",
			"account_token_cera":           "7",
			"account_happy_token_cera":     "3",
			"unrelated_metadata_preserved": "yes",
		},
	}); err != nil {
		t.Fatal(err)
	}
	uow := &memoryReceiptUOW{AccountRepository: group.Account, memoryReceipts: &memoryReceipts{}}
	return ctx, group.Account, uow, accountID
}

func TestPaodianGrantsEachCurrencyOnce(t *testing.T) {
	cases := []struct {
		currency string
		key      string
		before   int64
		want     int64
	}{
		{"cera", "account_cera", 100, 130},
		{"token", "account_token_cera", 7, 37},
		{"joytoken", "account_happy_token_cera", 3, 33},
	}
	for _, tc := range cases {
		t.Run(tc.currency, func(t *testing.T) {
			ctx, accounts, uow, accountID := paodianFixture(t)
			cfg := PaodianConfig{Enabled: true, IntervalSeconds: 60, Amount: 30, Currency: tc.currency}
			key := paodianKey("cycle-1", cfg)
			granted, err := grantPaodianToAccount(ctx, uow, accountID, cfg, key)
			if err != nil || !granted {
				t.Fatalf("grant: granted=%t err=%v", granted, err)
			}
			acc, found, err := accounts.Load(ctx, accountID)
			if err != nil || !found {
				t.Fatalf("load account: found=%t err=%v", found, err)
			}
			if got := parsePaodianBalance(acc.Metadata[tc.key]); got != tc.want {
				t.Fatalf("%s balance = %d, want %d", tc.key, got, tc.want)
			}
			if acc.Metadata["unrelated_metadata_preserved"] != "yes" {
				t.Fatal("unrelated metadata was overwritten")
			}
			granted, err = grantPaodianToAccount(ctx, uow, accountID, cfg, key)
			if err != nil || granted {
				t.Fatalf("second grant: granted=%t err=%v (want false,nil)", granted, err)
			}
			acc, _, _ = accounts.Load(ctx, accountID)
			if got := parsePaodianBalance(acc.Metadata[tc.key]); got != tc.want {
				t.Fatalf("receipt did not prevent double credit: %s=%d", tc.key, got)
			}
		})
	}
}

func TestPaodianConfigValidation(t *testing.T) {
	good := PaodianConfig{Enabled: true, IntervalSeconds: 60, Amount: 30, Currency: "token"}
	if !good.valid() {
		t.Fatal("default config should be valid")
	}
	for _, bad := range []PaodianConfig{
		{IntervalSeconds: 5, Amount: 30, Currency: "token"},
		{IntervalSeconds: 60, Amount: 0, Currency: "token"},
		{IntervalSeconds: 60, Amount: 30, Currency: "unknown"},
	} {
		if bad.valid() {
			t.Fatalf("invalid config accepted: %+v", bad)
		}
	}
}
