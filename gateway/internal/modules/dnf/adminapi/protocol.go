// Package adminapi defines the private game-runtime management contract.
// It contains no web panel, database connection, scheduler or game transport.
package adminapi

import (
	"dnf90.local/gateway/internal/modules/dnf/moderation"
	"dnf90.local/gateway/internal/modules/dnf/onlineevent"
	"dnf90.local/gateway/internal/modules/dnf/qqbinding"
	"dnf90.local/gateway/internal/modules/dnf/repository"
)

const Version = 1
const Path = "/api/game/admin"

// 注册限制管理（registration_* action）：邀请码增删查 + 注册记录 + 机器码/IP 解绑。
// 字段与 ServerS5A3 的 adminapi 协议保持同步。
type RegistrationQuery struct {
	Limit  int `json:"limit,omitempty"`
	Offset int `json:"offset,omitempty"`
}

type RegistrationInviteChange struct {
	Op          string `json:"op,omitempty"`
	Code        string `json:"code,omitempty"`
	Count       int    `json:"count,omitempty"`
	MaxUses     int    `json:"maxUses,omitempty"`
	ExpiresDays int    `json:"expiresDays,omitempty"`
	Note        string `json:"note,omitempty"`
}

type RegistrationUnbind struct {
	Kind  string `json:"kind,omitempty"`
	Value string `json:"value,omitempty"`
}

type RegistrationInvite struct {
	Code      string `json:"code"`
	MaxUses   int    `json:"maxUses"`
	Used      int    `json:"used"`
	ExpiresAt string `json:"expiresAt,omitempty"`
	Revoked   bool   `json:"revoked,omitempty"`
	Note      string `json:"note,omitempty"`
	CreatedAt string `json:"createdAt"`
}

type RegistrationRecord struct {
	AccountID string `json:"accountId"`
	Account   string `json:"account,omitempty"`
	IP        string `json:"ip,omitempty"`
	Device    string `json:"device,omitempty"`
	Invite    string `json:"invite,omitempty"`
	CreatedAt string `json:"createdAt,omitempty"`
}

type Request struct {
	InventoryEdit   *InventoryEditRequest       `json:"inventoryEdit,omitempty"`
	QQChange        *qqbinding.Change           `json:"qqChange,omitempty"`
	QQCheck         *qqbinding.CheckResult      `json:"qqCheck,omitempty"`
	PasswordReset   *PasswordReset              `json:"passwordReset,omitempty"`
	QQAck           *moderation.QQAck           `json:"qqAck,omitempty"`
	QQ              string                      `json:"qq,omitempty"`
	QQSettings      *qqbinding.Settings         `json:"qqSettings,omitempty"`
	QQComplete      *qqbinding.Complete         `json:"qqComplete,omitempty"`
	QQQuery         *qqbinding.Query            `json:"qqQuery,omitempty"`
	ModerationQuery *moderation.Query           `json:"moderationQuery,omitempty"`
	BanChange       *moderation.BanChange       `json:"banChange,omitempty"`
	AccountID       string                      `json:"accountId,omitempty"`
	Version         int                         `json:"version"`
	Action          string                      `json:"action"`
	CharacterID     uint16                      `json:"characterId,omitempty"`
	Revision        uint64                      `json:"revision,omitempty"`
	Changes         []onlineevent.ControlChange `json:"changes,omitempty"`
	ListType        byte                        `json:"listType,omitempty"`
	Slot            int16                       `json:"slot,omitempty"`
	Stack           *repository.ItemStack       `json:"stack,omitempty"`
	// 注册限制管理（registration_* action）。
	RegistrationQuery  *RegistrationQuery        `json:"registrationQuery,omitempty"`
	RegistrationInvite *RegistrationInviteChange `json:"registrationInvite,omitempty"`
	RegistrationUnbind *RegistrationUnbind       `json:"registrationUnbind,omitempty"`
	// 深渊史诗掉率：nil=读取；非 nil=设置（0..1000，-1 清除回退 PVF）。
	HellDropRate *int `json:"hellDropRate,omitempty"`
	// 深渊保底件数档：-1=退出件数档；0=关闭；2..5=每房 1..N 件。
	HellDropCount *int `json:"hellDropCount,omitempty"`
	// 时空裂缝史诗掉率：-1=PVF 原行为；0..1000=触发 PVF 件数表的千分比。
	RiftDropRate *int `json:"riftDropRate,omitempty"`
	// 时空裂缝保底件数档：-1=退出件数档；0=关闭；2..5=每次 1..N 件。
	RiftDropCount *int `json:"riftDropCount,omitempty"`
}

// HellDropRateStatus 与游戏服协议字段保持一致。
// Mode: pvf / rate / count / off。
type HellDropRateStatus struct {
	Rate        int    `json:"rate"`
	Override    bool   `json:"override"`
	PVFBaseline int    `json:"pvfBaseline"`
	Scale       int    `json:"scale"`
	Mode        string `json:"mode"`
	MinPieces   int    `json:"minPieces,omitempty"`
	MaxPieces   int    `json:"maxPieces,omitempty"`
}

// RiftDropRateStatus 与 HellDropRateStatus 共用千分比状态结构。
type RiftDropRateStatus = HellDropRateStatus

type Response struct {
	InventoryEdit      *InventoryEditStatus         `json:"inventoryEdit,omitempty"`
	QQChecks           []qqbinding.Check            `json:"qqChecks,omitempty"`
	QQChanged          bool                         `json:"qqChanged,omitempty"`
	PasswordReset      *PasswordResetResult         `json:"passwordReset,omitempty"`
	ErrorCode          string                       `json:"errorCode,omitempty"`
	BanTargets         *moderation.BanTargets       `json:"banTargets,omitempty"`
	QQActions          []moderation.QQAction        `json:"qqActions,omitempty"`
	QQBlocked          bool                         `json:"qqBlocked,omitempty"`
	QQSettings         *qqbinding.Settings          `json:"qqSettings,omitempty"`
	QQBinding          *qqbinding.Binding           `json:"qqBinding,omitempty"`
	QQBindings         *qqbinding.Page              `json:"qqBindings,omitempty"`
	Clears             *moderation.ClearPage        `json:"clears,omitempty"`
	Bans               *moderation.BanPage          `json:"bans,omitempty"`
	Ban                *moderation.Ban              `json:"ban,omitempty"`
	ModerationAccounts *moderation.AccountPage      `json:"moderationAccounts,omitempty"`
	CharacterEdit      *CharacterEditStatus         `json:"characterEdit,omitempty"`
	AccountEdit        *AccountEditStatus           `json:"accountEdit,omitempty"`
	OnlineSnapshot     *OnlineSnapshot              `json:"onlineSnapshot,omitempty"`
	Version            int                          `json:"version"`
	OK                 bool                         `json:"ok"`
	Error              string                       `json:"error,omitempty"`
	Online             bool                         `json:"online"`
	Activities         *onlineevent.ControlSnapshot `json:"activities,omitempty"`
	PremiumTypes       []int64                      `json:"premiumTypes,omitempty"`
	CrystalCubeItemIDs [6]int64                     `json:"crystalCubeItemIds,omitempty"`
	StackRow           []byte                       `json:"stackRow,omitempty"`
	// 注册限制管理结果。
	RegistrationInvites []RegistrationInvite `json:"registrationInvites,omitempty"`
	RegistrationRecords []RegistrationRecord `json:"registrationRecords,omitempty"`
	RegistrationCodes   []string             `json:"registrationCodes,omitempty"`
	RegistrationChanged bool                 `json:"registrationChanged,omitempty"`
	RegistrationTotal   int                  `json:"registrationTotal,omitempty"`
	HellDropRate        *HellDropRateStatus  `json:"hellDropRate,omitempty"`
	RiftDropRate        *RiftDropRateStatus  `json:"riftDropRate,omitempty"`
}
