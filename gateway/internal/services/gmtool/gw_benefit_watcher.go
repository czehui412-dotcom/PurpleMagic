package gmtool

import (
	"context"
	"time"

	"dnf90.local/gateway/internal/diagnostics"
)

// directBenefitWatcherInterval 是注册/建角色即时福利的增量轮询间隔。
// 相比 3 小时的兜底扫描轮，这里只看"水位之后"的新账号与新角色，成本极低。
const directBenefitWatcherInterval = 15 * time.Second

// directBenefitWatcherStartupLookback 是进程重启时的时间回看窗口：
// 重启期间创建的角色也能补投递（回执幂等，重复触发不会重复发放）。
const directBenefitWatcherStartupLookback = 10 * time.Minute

// RunDirectBenefitWatcher 在 BenefitDeliverSecret 配置启用时，周期性把
// 新注册账号与新创建角色送进 deliverBenefitsForAccount：
//   - 新账号（web_accounts 自增 id 水位）：注册瞬间通常还没有角色，
//     本次调用是 no-op；首个角色创建后由下面的新角色分支补发账号级规则。
//   - 新角色（dnf_characters.created_at 水位）：立即对角色跑账号级+角色级投递。
//
// 与 scanBenefitPage 语义一致：PVF 代次核对、回执预检、投递失败留给兜底扫描轮重试。
func (s *Server) RunDirectBenefitWatcher(ctx context.Context, logf func(string, ...any)) error {
	if s.cfg.BenefitDeliverSecret == "" || s.db == nil {
		<-ctx.Done()
		return ctx.Err()
	}
	var accountWatermark int64
	if err := s.db.QueryRowContext(ctx,
		"SELECT COALESCE(MAX(id), 0) FROM "+quoteIdentifier(s.cfg.Table)).Scan(&accountWatermark); err != nil {
		return err
	}
	characterWatermark := time.Now().Add(-directBenefitWatcherStartupLookback)
	if logf != nil {
		logf("benefit-direct-watcher-start", "account_watermark", accountWatermark,
			"character_watermark", characterWatermark.Format(time.RFC3339))
	}
	ticker := time.NewTicker(directBenefitWatcherInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-ticker.C:
		}
		s.stepDirectBenefitWatcher(ctx, &accountWatermark, &characterWatermark, logf)
	}
}

func (s *Server) stepDirectBenefitWatcher(ctx context.Context, accountWatermark *int64, characterWatermark *time.Time, logf func(string, ...any)) {
	// 新注册账号：无角色时 deliver 内部直接跳过，等首个角色触发。
	rows, err := s.db.QueryContext(ctx,
		"SELECT id, account_id FROM "+quoteIdentifier(s.cfg.Table)+" WHERE id>? ORDER BY id LIMIT 50", *accountWatermark)
	if err == nil {
		type pending struct {
			id  int64
			key string
		}
		var accounts []pending
		for rows.Next() {
			var a pending
			if e := rows.Scan(&a.id, &a.key); e != nil {
				break
			}
			accounts = append(accounts, a)
		}
		rows.Close()
		for _, a := range accounts {
			deliveryCtx, stop := context.WithTimeout(ctx, 8*time.Second)
			_, _, e := s.deliverBenefitsForAccount(deliveryCtx, a.key, "")
			stop()
			if e != nil {
				diagnostics.Log(logf, ctx, "benefit-direct-watcher-delivery-retry", e, "account", a.key)
			}
			if a.id > *accountWatermark {
				*accountWatermark = a.id
			}
		}
		if len(accounts) > 0 && logf != nil {
			logf("benefit-direct-watcher-accounts", "count", len(accounts), "watermark", *accountWatermark)
		}
	} else {
		rows.Close()
	}

	// 新建角色：立即投递（账号级 + 角色级规则一次全覆盖）。
	charRows, err := s.db.QueryContext(ctx,
		"SELECT account_id, character_id FROM dnf_characters WHERE created_at>? ORDER BY created_at LIMIT 50", *characterWatermark)
	if err != nil {
		return
	}
	type charPending struct {
		account   string
		character string
	}
	var chars []charPending
	for charRows.Next() {
		var c charPending
		if e := charRows.Scan(&c.account, &c.character); e != nil {
			break
		}
		chars = append(chars, c)
	}
	charRows.Close()
	delivered, failed := 0, 0
	for _, c := range chars {
		deliveryCtx, stop := context.WithTimeout(ctx, 8*time.Second)
		_, sent, e := s.deliverBenefitsForAccount(deliveryCtx, c.account, c.character)
		stop()
		if e != nil {
			failed++
			diagnostics.Log(logf, ctx, "benefit-direct-watcher-delivery-retry", e,
				"account", c.account, "character", c.character)
			continue
		}
		delivered += sent
	}
	if len(chars) > 0 && logf != nil {
		logf("benefit-direct-watcher-characters", "count", len(chars), "delivered", delivered, "failed", failed)
	}
	if len(chars) == 50 {
		// 一批打满时推进时间水位，避免下一轮重复扫同一批（回执幂等，重复无害但浪费）。
		*characterWatermark = time.Now().Add(-directBenefitWatcherStartupLookback)
	}
}
