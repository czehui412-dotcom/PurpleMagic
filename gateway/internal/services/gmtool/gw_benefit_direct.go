package gmtool

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"net/http"
	"sort"
	"strings"
	"time"

	"dnf90.local/gateway/internal/diagnostics"
	"dnf90.local/gateway/internal/modules/dnf/testbenefits"
)

// directBenefitDeliverPath 是注册账号 / 创建角色后的即时福利投递入口。
// 它挂在网关私有管理口上，但**不经过 servicehttp.Protect**（那要求
// X-PVF-SHA256，游戏服侧没有该值），改用独立 Bearer secret 校验。
const directBenefitDeliverPath = "/api/internal/gateway/v1/benefit-deliver"

// handleDirectBenefitDelivery 对某个账号（可限定角色）立即跑一次福利投递。
// 请求体：{"account":"dnf:13879","character":"10964"}，character 可省略（投递该账号全部角色）。
// 语义与 scanBenefitPage 的单账号分支一致：PVF 代次核对、回执预检、
// 账号级规则回执去重、单角色投递失败不影响其他规则。
func (s *Server) handleDirectBenefitDelivery(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	expected := s.cfg.BenefitDeliverSecret
	provided := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
	if len(expected) < 16 || len(provided) < 16 ||
		subtle.ConstantTimeCompare([]byte(expected), []byte(provided)) != 1 {
		http.Error(w, "forbidden", http.StatusForbidden)
		return
	}
	var in struct {
		Account   string `json:"account"`
		Character string `json:"character,omitempty"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&in); err != nil ||
		in.Account == "" || len(in.Account) > 128 || len(in.Character) > 128 {
		http.Error(w, "bad request", http.StatusBadRequest)
		return
	}
	ctx, stop := context.WithTimeout(r.Context(), 10*time.Second)
	defer stop()
	checked, delivered, err := s.deliverBenefitsForAccount(ctx, in.Account, in.Character)
	out := struct {
		Checked   int    `json:"checked"`
		Delivered int    `json:"delivered"`
		Error     string `json:"error,omitempty"`
	}{Checked: checked, Delivered: delivered}
	if err != nil {
		out.Error = err.Error()
		diagnostics.Log(s.cfg.Logger, ctx, "benefit-direct-delivery-retry", err,
			"account", in.Account, "character", in.Character)
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(out)
}

// deliverBenefitsForAccount 是扫描轮次的单账号即时版本。无角色或账号不存在时
// 返回 0/0 不报错（注册瞬间还没有角色的情况是正常的，建角色会再次触发）。
func (s *Server) deliverBenefitsForAccount(ctx context.Context, accountKey, characterID string) (checked, delivered int, resultErr error) {
	if s.deps.Benefits == nil {
		return 0, 0, errors.New("benefit store unavailable")
	}
	campaigns, err := s.deps.Benefits.Campaigns(ctx)
	if err != nil {
		return 0, 0, err
	}
	sort.Slice(campaigns, func(i, j int) bool { return campaigns[i].ID < campaigns[j].ID })
	active := false
	blocked := map[string]bool{}
	for i, c := range campaigns {
		active = active || (c.Enabled && (c.AutoExistingCharacters || c.AutoNewCharacters))
		if c.Enabled && (c.AutoExistingCharacters || c.AutoNewCharacters) &&
			c.Reward.PVFChecksum != "" && c.Reward.PVFChecksum != s.v1PVFChecksum() {
			updated, e := s.reconcileCampaignPVF(ctx, c)
			if e == nil {
				campaigns[i] = updated
			} else {
				blocked[c.ID] = true
			}
		}
	}
	if !active {
		return 0, 0, nil
	}
	group, err := s.repositories()
	if err != nil {
		return 0, 0, err
	}
	characters, err := testbenefits.CharactersForAccount(ctx, group, accountKey)
	if err != nil {
		return 0, 0, err
	}
	if len(characters) == 0 {
		return 0, 0, nil
	}
	account, found, err := group.Account.Load(ctx, accountKey)
	if err != nil || !found {
		return 0, 0, errors.New("benefit account unavailable")
	}
	accountSent := map[string]bool{}
	var failures []error
	attempts := 0
	for _, ch := range characters {
		if characterID != "" && ch.CharacterID != characterID {
			continue
		}
		for _, c := range campaigns {
			if !testbenefits.EligibleNewCharacter(c, ch) {
				continue
			}
			if blocked[c.ID] || accountSent[c.ID] {
				continue
			}
			receipt, e := testbenefits.PreviousDeliveryReceipt(account, c.Reward, testbenefits.Recipient{
				AccountID: ch.AccountID, CharacterID: ch.CharacterID, CreatedAt: ch.CreatedAt,
			})
			if e == nil && receipt.MailID != "" {
				checked++
				if c.Reward.Scope.Effective() == testbenefits.ScopeAccount {
					accountSent[c.ID] = true
				}
				continue
			}
			if attempts >= 10 || ctx.Err() != nil {
				return checked, delivered, errors.Join(append(failures, ctx.Err())...)
			}
			e = s.deps.Benefits.DeliverCampaignToNewCharacter(ctx, group, c.ID, ch, s.v1ProjectCampaignDelivery)
			attempts++
			checked++
			if e != nil {
				failures = append(failures, e)
				continue
			}
			delivered++
			if c.Reward.Scope.Effective() == testbenefits.ScopeAccount {
				accountSent[c.ID] = true
			}
		}
	}
	return checked, delivered, errors.Join(failures...)
}
