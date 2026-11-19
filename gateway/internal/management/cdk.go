// CDK 兑换码：码表存注册库（pm_cdk_codes / pm_cdk_redemptions），发货完全
// 复用 testbenefits.Deliver（点券直充 account_cera / 道具装备系统邮件附件，
// 永久回执防重发）。登录器经网关公开口调 /api/cdk/*（账号密码 bcrypt 验证），
// GM 面板经 /api/v1/cdk* 生成与管理（cookie+CSRF 走现有 v1 鉴权）。
package management

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math/big"
	"net/http"
	"regexp"
	"strconv"
	"strings"
	"time"

	"dnf90.local/gateway/internal/modules/dnf/repository"
	"dnf90.local/gateway/internal/modules/dnf/testbenefits"
	"dnf90.local/gateway/internal/services/gmtool"
	"golang.org/x/crypto/bcrypt"
)

const (
	cdkCodeTable   = "pm_cdk_codes"
	cdkRedeemTable = "pm_cdk_redemptions"
	cdkMaxCount    = 100       // 单次生成上限
	cdkMaxAmount   = 2147483647
	cdkMaxChars    = 50        // 角色列表上限
)

// 新格式 = 5 组×4 位（XXXX-XXXX-XXXX-XXXX-XXXX），旧格式 = PM-+12 位（历史码仍可兑换）。
// 字符集统一去掉易混淆的 I O 0 1。
var (
	cdkNewCodePattern    = regexp.MustCompile(`^[A-HJ-NP-Z2-9]{4}(?:-[A-HJ-NP-Z2-9]{4}){4}$`)
	cdkNewBodyPattern    = regexp.MustCompile(`^[A-HJ-NP-Z2-9]{20}$`)
	cdkLegacyCodePattern = regexp.MustCompile(`^PM-[A-HJ-NP-Z1-9]{12}$`)
	cdkLegacyBodyPattern = regexp.MustCompile(`^[A-HJ-NP-Z1-9]{12}$`)
)

// cdkCodePattern 同时接受新旧两种形态（归一化之后校验用）。
var cdkCodePattern = regexp.MustCompile(`^(?:` + cdkNewCodePattern.String()[1:len(cdkNewCodePattern.String())-1] +
	`|` + cdkLegacyCodePattern.String()[1:len(cdkLegacyCodePattern.String())-1] + `)$`)

// cdkNormalizeCode 把玩家输入规整为码表存储形态：
// 大写 → 去空白 → 去连字符 → 20 位回插连字符成 XXXX-…-XXXX；12 位旧码补 PM- 前缀
// （注意先剥 PM- 前缀再剥连字符，否则前缀里的连字符会被误剥）；其余原样返回，
// 交由 cdkCodePattern 校验后报「格式不正确」。
func cdkNormalizeCode(raw string) string {
	s := strings.ToUpper(strings.TrimSpace(raw))
	s = strings.Map(func(r rune) rune {
		if r == ' ' || r == '\t' || r == '　' {
			return -1
		}
		return r
	}, s)
	body := strings.Map(func(r rune) rune {
		if r == '-' {
			return -1
		}
		return r
	}, strings.TrimPrefix(s, "PM-"))
	switch {
	case len(body) == 20 && cdkNewBodyPattern.MatchString(body):
		var b strings.Builder
		for i := 0; i < len(body); i += 4 {
			if i > 0 {
				b.WriteByte('-')
			}
			b.WriteString(body[i : i+4])
		}
		return b.String()
	case len(body) == 12 && cdkLegacyBodyPattern.MatchString(body):
		return "PM-" + body
	default:
		return s
	}
}

// Crockford base32（去掉易混淆的 I O 0 1），20 位 → 约 32^20 空间。
const cdkAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

// CdkStore 是 CDK 兑换码的注册库 + 游戏库联合操作入口。
type CdkStore struct {
	registration *sql.DB
	repos        func() (repository.Group, bool)
	accountTable string
	log          func(string, ...any)
}

type cdkPayload struct {
	Kind      string `json:"kind"` // cera | item | equipment
	Cera      int64  `json:"cera,omitempty"`
	ItemID    int64  `json:"itemId,omitempty"`
	ItemCount int64  `json:"itemCount,omitempty"`
	Bind      bool   `json:"bind"`
}

// EnsureCdkSchema 幂等建 CDK 码表与核销记录表（注册库）。
func EnsureCdkSchema(ctx context.Context, db *sql.DB) error {
	for _, ddl := range []string{
		"CREATE TABLE IF NOT EXISTS " + cdkCodeTable + " (" +
			"code VARCHAR(32) NOT NULL PRIMARY KEY," +
			"payload_json LONGTEXT NOT NULL," +
			"max_uses INT NOT NULL DEFAULT 1," +
			"used_count INT NOT NULL DEFAULT 0," +
			"expires_at DATETIME NULL," +
			"note VARCHAR(120) NOT NULL DEFAULT ''," +
			"created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP" +
			") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",
		"CREATE TABLE IF NOT EXISTS " + cdkRedeemTable + " (" +
			"id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY," +
			"code VARCHAR(32) NOT NULL," +
			"account_id VARCHAR(128) NOT NULL," +
			"character_id VARCHAR(64) NOT NULL," +
			"payload_json LONGTEXT NOT NULL," +
			"redeemed_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP," +
			"UNIQUE KEY uq_cdk_code_account (code, account_id)," +
			"KEY idx_cdk_account (account_id)" +
			") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",
		// 旧库 code 列曾是 VARCHAR(15)：新格式连字符共 24 字符放不下，幂等放宽。
		"ALTER TABLE " + cdkCodeTable + " MODIFY code VARCHAR(32) NOT NULL",
		"ALTER TABLE " + cdkRedeemTable + " MODIFY code VARCHAR(32) NOT NULL",
	} {
		if _, err := db.ExecContext(ctx, ddl); err != nil {
			// ALTER 的唯一可能失败是表刚由上面 CREATE 建好（新列宽已是 32），
			// 或库尚不可用——后者会在 NewCdkStore 的后续查询暴露，这里不硬失败。
			if strings.Contains(err.Error(), "Unknown column") || strings.Contains(err.Error(), "Duplicate column") ||
				strings.Contains(err.Error(), "doesn't exist") {
				continue
			}
			return fmt.Errorf("cdk schema: %w", err)
		}
	}
	return nil
}

// NewCdkStore 校验表可用并返回操作入口。
func NewCdkStore(ctx context.Context, registration *sql.DB, repos func() (repository.Group, bool), accountTable string, log func(string, ...any)) (*CdkStore, error) {
	if log == nil {
		log = func(string, ...any) {}
	}
	if err := EnsureCdkSchema(ctx, registration); err != nil {
		return nil, err
	}
	return &CdkStore{registration: registration, repos: repos, accountTable: accountTable, log: log}, nil
}

// cdkFail 是面向登录器的 JSON 错误响应。
type cdkReply struct {
	OK        bool   `json:"ok"`
	Message   string `json:"message,omitempty"`
	Kind      string `json:"kind,omitempty"`
	Cera      int64  `json:"cera,omitempty"`
	ItemID    int64  `json:"itemId,omitempty"`
	ItemCount int64  `json:"itemCount,omitempty"`
	CharacterID string `json:"characterId,omitempty"`
}

type cdkCharacterView struct {
	CharacterID string `json:"characterId"`
	Name        string `json:"name"`
	Level       int    `json:"level"`
	Job         string `json:"job"`
}

type cdkCharactersReply struct {
	OK         bool               `json:"ok"`
	Message    string             `json:"message,omitempty"`
	Characters []cdkCharacterView `json:"characters"`
}

type cdkRequestBody struct {
	Account     string `json:"account"`
	Password    string `json:"password"`
	Code        string `json:"code"`
	CharacterID string `json:"characterId"`
}

func writeCdkJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}

func (s *CdkStore) decodeBody(w http.ResponseWriter, r *http.Request) (cdkRequestBody, bool) {
	var body cdkRequestBody
	dec := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096))
	dec.DisallowUnknownFields()
	if err := dec.Decode(&body); err != nil {
		writeCdkJSON(w, http.StatusBadRequest, cdkReply{OK: false, Message: "请求格式无效。"})
		return body, false
	}
	return body, true
}

// verify 用注册库 bcrypt 校验账号密码，返回游戏账号 ID（dnf:…）。
func (s *CdkStore) verify(ctx context.Context, account, password string) (string, error) {
	account = strings.ToLower(strings.TrimSpace(account))
	if account == "" || password == "" {
		return "", errors.New("请输入账号和密码。")
	}
	deadline, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	var hash, id string
	err := s.registration.QueryRowContext(deadline,
		"SELECT password_hash, account_id FROM "+s.accountTable+" WHERE account=?", account,
	).Scan(&hash, &id)
	if errors.Is(err, sql.ErrNoRows) || (err == nil && bcrypt.CompareHashAndPassword([]byte(hash), []byte(password)) != nil) {
		return "", errors.New("账号或密码不正确。")
	}
	if err != nil {
		s.log("cdk-verify-db-error", "error", err)
		return "", errors.New("账号服务暂不可用，请稍后再试。")
	}
	return strings.TrimSpace(id), nil
}

// summaries 查一个游戏账号名下的角色（跨分片只读基表，见 CharacterSummaryReader）。
func (s *CdkStore) summaries(ctx context.Context, accountID string) ([]repository.CharacterSummary, error) {
	group, ok := s.repos()
	if !ok || group.Character == nil {
		return nil, errors.New("角色服务暂不可用，请稍后再试。")
	}
	reader, ok := group.Character.(repository.CharacterSummaryReader)
	if !ok {
		return nil, errors.New("角色服务暂不可用，请稍后再试。")
	}
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	return reader.SummariesForAccounts(ctx, []string{accountID})
}

// Characters 登录器「查询角色」：验密后返回名下角色。
func (s *CdkStore) Characters(ctx context.Context, account, password string) cdkCharactersReply {
	accountID, err := s.verify(ctx, account, password)
	if err != nil {
		return cdkCharactersReply{OK: false, Message: err.Error()}
	}
	summaries, err := s.summaries(ctx, accountID)
	if err != nil {
		return cdkCharactersReply{OK: false, Message: err.Error()}
	}
	if len(summaries) == 0 {
		return cdkCharactersReply{OK: false, Message: "该账号暂无角色，请先创建角色。"}
	}
	if len(summaries) > cdkMaxChars {
		summaries = summaries[:cdkMaxChars]
	}
	views := make([]cdkCharacterView, 0, len(summaries))
	for _, c := range summaries {
		views = append(views, cdkCharacterView{CharacterID: c.CharacterID, Name: c.Name, Level: c.Level, Job: c.Job})
	}
	return cdkCharactersReply{OK: true, Characters: views}
}

// cdkReceiptID 用「码+账号」派生确定性投递 ID：同账号重放同一码时命中
// testbenefits 的永久回执，绝不重复发货。
func cdkReceiptID(code, accountID string) string {
	sum := sha256.Sum256([]byte("cdk-v1:" + code + ":" + accountID))
	return hex.EncodeToString(sum[:16])
}

// Redeem 登录器兑换：验密 → 锁码核销 → 角色归属校验 → 复用 benefits 投递。
func (s *CdkStore) Redeem(ctx context.Context, account, password, code, characterID string) cdkReply {
	accountID, err := s.verify(ctx, account, password)
	if err != nil {
		return cdkReply{OK: false, Message: err.Error()}
	}
	code = cdkNormalizeCode(code)
	if !cdkCodePattern.MatchString(code) {
		return cdkReply{OK: false, Message: "兑换码格式不正确。"}
	}
	characterID = strings.TrimSpace(characterID)

	tx, err := s.registration.BeginTx(ctx, nil)
	if err != nil {
		return cdkReply{OK: false, Message: "服务繁忙，请稍后再试。"}
	}
	defer func() { _ = tx.Rollback() }()

	var payloadRaw string
	var maxUses, usedCount int
	var expires sql.NullTime
	scanErr := tx.QueryRowContext(ctx,
		"SELECT payload_json, max_uses, used_count, expires_at FROM "+cdkCodeTable+" WHERE code=? FOR UPDATE", code,
	).Scan(&payloadRaw, &maxUses, &usedCount, &expires)
	if errors.Is(scanErr, sql.ErrNoRows) {
		return cdkReply{OK: false, Message: "兑换码不存在。"}
	}
	if scanErr != nil {
		s.log("cdk-redeem-db-error", "error", scanErr)
		return cdkReply{OK: false, Message: "服务繁忙，请稍后再试。"}
	}
	if expires.Valid && time.Now().After(expires.Time) {
		return cdkReply{OK: false, Message: "兑换码已过期。"}
	}
	if usedCount >= maxUses {
		return cdkReply{OK: false, Message: "兑换码已兑换完毕。"}
	}
	var already string
	dupErr := tx.QueryRowContext(ctx,
		"SELECT code FROM "+cdkRedeemTable+" WHERE code=? AND account_id=?", code, accountID,
	).Scan(&already)
	if dupErr == nil {
		return cdkReply{OK: false, Message: "该账号已兑换过此码。"}
	}
	if !errors.Is(dupErr, sql.ErrNoRows) {
		s.log("cdk-redeem-db-error", "error", dupErr)
		return cdkReply{OK: false, Message: "服务繁忙，请稍后再试。"}
	}

	var payload cdkPayload
	if err := json.Unmarshal([]byte(payloadRaw), &payload); err != nil {
		s.log("cdk-payload-invalid", "code", code, "error", err)
		return cdkReply{OK: false, Message: "兑换码配置异常，请联系管理员。"}
	}

	summaries, err := s.summaries(ctx, accountID)
	if err != nil {
		return cdkReply{OK: false, Message: err.Error()}
	}
	var chosen *repository.CharacterSummary
	for i := range summaries {
		if summaries[i].CharacterID == characterID {
			chosen = &summaries[i]
			break
		}
	}
	if chosen == nil {
		return cdkReply{OK: false, Message: "兑换角色不存在或不属于该账号，请重新查询。"}
	}

	group, ok := s.repos()
	if !ok || group.Character == nil {
		return cdkReply{OK: false, Message: "服务繁忙，请稍后再试。"}
	}
	job := testbenefits.Job{
		ID:    cdkReceiptID(code, accountID),
		Title: "CDK兑换",
		Body:  "兑换码 " + code + " 兑换成功，感谢支持！",
		Scope: testbenefits.ScopeCharacter,
	}
	reply := cdkReply{OK: true, Kind: payload.Kind, CharacterID: characterID}
	switch payload.Kind {
	case "cera":
		if payload.Cera <= 0 || payload.Cera > cdkMaxAmount {
			return cdkReply{OK: false, Message: "兑换码配置异常，请联系管理员。"}
		}
		job.Cera = payload.Cera
		job.Scope = testbenefits.ScopeAccount
		reply.Cera = payload.Cera
	case "item", "equipment":
		if payload.ItemID <= 0 || payload.ItemID > cdkMaxAmount || payload.ItemCount <= 0 || payload.ItemCount > cdkMaxAmount {
			return cdkReply{OK: false, Message: "兑换码配置异常，请联系管理员。"}
		}
		job.Attachments = []repository.MailAttachment{{ItemID: payload.ItemID, Count: payload.ItemCount, Bind: payload.Bind}}
		reply.ItemID = payload.ItemID
		reply.ItemCount = payload.ItemCount
	default:
		return cdkReply{OK: false, Message: "兑换码配置异常，请联系管理员。"}
	}
	if _, err := testbenefits.Deliver(ctx, group, job, testbenefits.Recipient{
		AccountID: accountID, CharacterID: characterID, Name: chosen.Name, CreatedAt: chosen.CreatedAt,
	}); err != nil {
		s.log("cdk-deliver-error", "code", code, "account", accountID, "error", err)
		return cdkReply{OK: false, Message: "发货失败： " + err.Error()}
	}

	payloadSnapshot, _ := json.Marshal(payload)
	if _, err := tx.ExecContext(ctx,
		"INSERT INTO "+cdkRedeemTable+" (code, account_id, character_id, payload_json) VALUES (?,?,?,?)",
		code, accountID, characterID, string(payloadSnapshot),
	); err != nil {
		// 唯一键兜底：并发下同账号二次兑换到这里必然冲突。
		return cdkReply{OK: false, Message: "该账号已兑换过此码。"}
	}
	if _, err := tx.ExecContext(ctx,
		"UPDATE "+cdkCodeTable+" SET used_count=used_count+1 WHERE code=?", code,
	); err != nil {
		s.log("cdk-redeem-db-error", "error", err)
		return cdkReply{OK: false, Message: "服务繁忙，请稍后再试。"}
	}
	if err := tx.Commit(); err != nil {
		s.log("cdk-redeem-commit-error", "error", err)
		return cdkReply{OK: false, Message: "服务繁忙，请稍后再试。"}
	}
	return reply
}

// PublicHandler 暴露登录器用的两条免 token 路由（网关 public 分支转交）。
func (s *CdkStore) PublicHandler() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/api/cdk/redeem":
			if r.Method != http.MethodPost {
				w.WriteHeader(http.StatusMethodNotAllowed)
				return
			}
			body, ok := s.decodeBody(w, r)
			if !ok {
				return
			}
			writeCdkJSON(w, http.StatusOK, s.Redeem(r.Context(), body.Account, body.Password, body.Code, body.CharacterID))
		case "/api/cdk/characters":
			if r.Method != http.MethodPost {
				w.WriteHeader(http.StatusMethodNotAllowed)
				return
			}
			body, ok := s.decodeBody(w, r)
			if !ok {
				return
			}
			writeCdkJSON(w, http.StatusOK, s.Characters(r.Context(), body.Account, body.Password))
		default:
			http.NotFound(w, r)
		}
	})
}

// ---- GM 面板侧：生成与列表（经 /api/v1/cdk*，走现有 v1 鉴权） ----
// DTO 直接使用 gmtool.CdkGenerateRequest / gmtool.CdkCodeRow（满足 gmtool.CdkAdmin 接口）。

func newCdkCode() string {
	max := big.NewInt(int64(len(cdkAlphabet)))
	raw := make([]byte, 20)
	for i := range raw {
		n, err := rand.Int(rand.Reader, max)
		if err != nil {
			panic(err)
		}
		raw[i] = cdkAlphabet[n.Int64()]
	}
	// 5 组×4 位，连字符分隔：XXXX-XXXX-XXXX-XXXX-XXXX
	var b strings.Builder
	for i := 0; i < len(raw); i += 4 {
		if i > 0 {
			b.WriteByte('-')
		}
		b.Write(raw[i : i+4])
	}
	return b.String()
}

func validateCdkGenerate(r gmtool.CdkGenerateRequest) (cdkPayload, error) {
	// 统一不绑定：道具/装备一律通过邮件发放为可交易形态，不提供绑定选项。
	payload := cdkPayload{Kind: r.Kind, Cera: r.Cera, ItemID: r.ItemID, ItemCount: r.ItemCount, Bind: false}
	switch r.Kind {
	case "cera":
		if r.Cera <= 0 || r.Cera > cdkMaxAmount {
			return payload, errors.New("点券面额须为 1 至 2147483647")
		}
	case "item", "equipment":
		if r.ItemID <= 0 || r.ItemID > cdkMaxAmount {
			return payload, errors.New("物品 ID 须为 1 至 2147483647")
		}
		if r.ItemCount <= 0 || r.ItemCount > cdkMaxAmount {
			return payload, errors.New("物品数量须为 1 至 2147483647")
		}
	default:
		return payload, errors.New("类型必须为 cera、item 或 equipment")
	}
	if r.Count < 1 || r.Count > cdkMaxCount {
		return payload, errors.New("生成个数须为 1 至 100")
	}
	if r.MaxUses < 1 || r.MaxUses > cdkMaxCount {
		return payload, errors.New("每人限领须为 1 至 100（同一账号只可兑换一次）")
	}
	if r.ExpiresDays < 0 || r.ExpiresDays > 3650 {
		return payload, errors.New("有效期为 0（永不过期）至 3650 天")
	}
	if len(r.Note) > 120 {
		return payload, errors.New("备注最多 120 字")
	}
	return payload, nil
}

func cdkPayloadSummary(payload cdkPayload) string {
	switch payload.Kind {
	case "cera":
		return fmt.Sprintf("点券 %d", payload.Cera)
	case "equipment":
		if payload.Bind {
			return fmt.Sprintf("装备 %d（绑定）", payload.ItemID)
		}
		return fmt.Sprintf("装备 %d", payload.ItemID)
	default:
		if payload.Bind {
			return fmt.Sprintf("道具 %d ×%d（绑定）", payload.ItemID, payload.ItemCount)
		}
		return fmt.Sprintf("道具 %d ×%d", payload.ItemID, payload.ItemCount)
	}
}

// GenerateCdk 生成一批兑换码（同一事务内逐行插入，冲突重试换码）。
func (s *CdkStore) GenerateCdk(ctx context.Context, req gmtool.CdkGenerateRequest) ([]gmtool.CdkCodeRow, error) {
	payload, err := validateCdkGenerate(req)
	if err != nil {
		return nil, err
	}
	payloadJSON, err := json.Marshal(payload)
	if err != nil {
		return nil, err
	}
	var expires any
	if req.ExpiresDays > 0 {
		expires = time.Now().AddDate(0, 0, req.ExpiresDays).UTC()
	}
	summary := cdkPayloadSummary(payload)
	rows := make([]gmtool.CdkCodeRow, 0, req.Count)
	tx, err := s.registration.BeginTx(ctx, nil)
	if err != nil {
		return nil, err
	}
	defer func() { _ = tx.Rollback() }()
	for i := 0; i < req.Count; i++ {
		var code string
		for attempt := 0; attempt < 5; attempt++ {
			code = newCdkCode()
			_, err = tx.ExecContext(ctx,
				"INSERT INTO "+cdkCodeTable+" (code, payload_json, max_uses, expires_at, note) VALUES (?,?,?,?,?)",
				code, string(payloadJSON), req.MaxUses, expires, req.Note)
			if err == nil {
				rows = append(rows, gmtool.CdkCodeRow{
					Code: code, Kind: payload.Kind, Summary: summary, MaxUses: req.MaxUses,
					Note: req.Note, CreatedAt: time.Now().UTC().Format(time.RFC3339),
				})
				break
			}
			if isDuplicateKey(err) && attempt < 4 {
				continue
			}
			_ = tx.Rollback()
			return nil, err
		}
		if len(rows) == i {
			return nil, errors.New("生成兑换码失败，请重试")
		}
	}
	if err := tx.Commit(); err != nil {
		return nil, err
	}
	s.log("cdk-generated", "count", req.Count, "kind", payload.Kind, "note", req.Note)
	return rows, nil
}

func isDuplicateKey(err error) bool {
	return err != nil && strings.Contains(err.Error(), "Duplicate entry")
}

// ListCdk 按过滤条件分页查询码表（管理端查询展示用）。
// mode：all 全部 / code 按码 / note 按名称（备注） / character 按兑换角色。
func (s *CdkStore) ListCdk(ctx context.Context, filter gmtool.CdkListFilter, page, pageSize int) ([]gmtool.CdkCodeRow, int, error) {
	if page < 1 {
		page = 1
	}
	if pageSize < 1 || pageSize > 200 {
		pageSize = 50
	}
	query := strings.TrimSpace(filter.Query)
	characterID := strings.TrimSpace(filter.CharacterID)
	where := " WHERE 1=1"
	var args []any
	switch filter.Mode {
	case "code":
		if query != "" {
			where += " AND code LIKE ?"
			args = append(args, "%"+strings.ToUpper(query)+"%")
		}
	case "note":
		if query != "" {
			where += " AND note LIKE ?"
			args = append(args, "%"+query+"%")
		}
	case "character":
		if characterID == "" && query != "" {
			characterID = query
		}
		if characterID != "" {
			where += " AND code IN (SELECT code FROM " + cdkRedeemTable + " WHERE character_id LIKE ?)"
			args = append(args, "%"+characterID+"%")
		}
	default: // all
		// 支持一个通用搜索词：优先按码匹配，其次备注与兑换角色。
		if query != "" {
			where += " AND (code LIKE ? OR note LIKE ? OR code IN (SELECT code FROM " + cdkRedeemTable +
				" WHERE character_id LIKE ?))"
			pattern := "%" + query + "%"
			args = append(args, pattern, "%"+query+"%", pattern)
		}
	}
	var total int
	if err := s.registration.QueryRowContext(ctx,
		"SELECT COUNT(*) FROM "+cdkCodeTable+where, args...).Scan(&total); err != nil {
		return nil, 0, err
	}
	rows, err := s.registration.QueryContext(ctx,
		"SELECT code, payload_json, max_uses, used_count, expires_at, note, created_at,"+
			" COALESCE((SELECT GROUP_CONCAT(DISTINCT r.character_id ORDER BY r.character_id SEPARATOR '、')"+
			" FROM "+cdkRedeemTable+" r WHERE r.code=c.code),'')"+
			" FROM "+cdkCodeTable+" c"+where+
			" ORDER BY created_at DESC, code DESC LIMIT ? OFFSET ?",
		append(args, pageSize, (page-1)*pageSize)...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	out := []gmtool.CdkCodeRow{}
	now := time.Now()
	for rows.Next() {
		var row gmtool.CdkCodeRow
		var payloadRaw, redeemed string
		var expires, created sql.NullTime
		if err := rows.Scan(&row.Code, &payloadRaw, &row.MaxUses, &row.UsedCount, &expires, &row.Note, &created, &redeemed); err != nil {
			return nil, 0, err
		}
		var payload cdkPayload
		if err := json.Unmarshal([]byte(payloadRaw), &payload); err == nil {
			row.Kind = payload.Kind
			row.Summary = cdkPayloadSummary(payload)
		}
		switch {
		case expires.Valid && now.After(expires.Time):
			row.Status = "已过期"
		case row.UsedCount >= row.MaxUses:
			row.Status = "已用完"
		case row.UsedCount <= 0:
			row.Status = "未使用"
		default:
			row.Status = "部分使用"
		}
		row.Redeemed = redeemed
		if expires.Valid {
			row.ExpiresAt = expires.Time.Format("2006-01-02 15:04")
		}
		if created.Valid {
			row.CreatedAt = created.Time.Format("2006-01-02 15:04")
		}
		out = append(out, row)
	}
	// 兑换角色显示名称：把核销记录里的角色 ID 批量解析成角色名回写；
	// 解析失败（服务不可用/角色已删）保留 ID 原样，不阻塞列表查询。
	if len(out) > 0 {
		var ids []string
		for _, row := range out {
			if row.Redeemed != "" {
				ids = append(ids, strings.Split(row.Redeemed, "、")...)
			}
		}
		names := s.characterNames(ctx, ids)
		for i := range out {
			if out[i].Redeemed == "" {
				continue
			}
			parts := strings.Split(out[i].Redeemed, "、")
			for j, id := range parts {
				if n := names[id]; n != "" {
					parts[j] = n
				}
			}
			out[i].Redeemed = strings.Join(parts, "、")
		}
	}
	return out, total, rows.Err()
}

// characterNames 批量解析角色 ID → 角色名（跨分片只读基表，单批 ≤100，
// 与 SummariesForCharacters 的上限对齐）。非数字 ID 或解析失败跳过，
// 调用方对查不到的 ID 保留原样显示。
func (s *CdkStore) characterNames(ctx context.Context, ids []string) map[string]string {
	names := map[string]string{}
	if len(ids) == 0 {
		return names
	}
	group, ok := s.repos()
	if !ok || group.Character == nil {
		return names
	}
	reader, ok := group.Character.(repository.CharacterBatchSummaryReader)
	if !ok {
		return names
	}
	unique := make([]string, 0, len(ids))
	seen := map[string]bool{}
	for _, id := range ids {
		n, err := strconv.ParseUint(id, 10, 16)
		if err != nil || n == 0 || strconv.FormatUint(n, 10) != id || seen[id] {
			continue
		}
		seen[id] = true
		unique = append(unique, id)
	}
	for start := 0; start < len(unique); start += 100 {
		end := start + 100
		if end > len(unique) {
			end = len(unique)
		}
		batchCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
		summaries, err := reader.SummariesForCharacters(batchCtx, unique[start:end])
		cancel()
		if err != nil {
			s.log("cdk-character-names-error", "error", err)
			continue
		}
		for _, c := range summaries {
			if c.Name != "" {
				names[c.CharacterID] = c.Name
			}
		}
	}
	return names
}
