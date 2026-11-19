package gmtool

import (
	"context"
	"errors"
)

// CdkAdmin 是 CDK 兑换码的管理能力（生成/列表）。实现在 management.CdkStore，
// 经 Deps.Cdk 注入——接口定义在这里避免 gmtool ↔ management 循环依赖。
// 生成/列表走 /api/v1/cdk*，鉴权与写保护复用 v1 层（cookie+CSRF+幂等键）。
type CdkAdmin interface {
	GenerateCdk(ctx context.Context, req CdkGenerateRequest) ([]CdkCodeRow, error)
	ListCdk(ctx context.Context, filter CdkListFilter, page, pageSize int) ([]CdkCodeRow, int, error)
}

// CdkListFilter 是管理端查询过滤：Mode=all 全部；code 按码；note 按名称（备注）；
// character 按兑换角色（子查询核销记录）。Query/CharacterID 至少其一参与过滤。
type CdkListFilter struct {
	Mode        string `json:"mode"`
	Query       string `json:"query"`
	CharacterID string `json:"characterId"`
}

type CdkGenerateRequest struct {
	Kind        string `json:"kind"`
	Cera        int64  `json:"cera"`
	ItemID      int64  `json:"itemId"`
	ItemCount   int64  `json:"itemCount"`
	Bind        bool   `json:"bind"`
	MaxUses     int    `json:"maxUses"`
	ExpiresDays int    `json:"expiresDays"`
	Count       int    `json:"count"`
	Note        string `json:"note"`
}

type CdkCodeRow struct {
	Code      string `json:"code"`
	Kind      string `json:"kind"`
	Summary   string `json:"summary"`
	MaxUses   int    `json:"maxUses"`
	UsedCount int    `json:"usedCount"`
	// Status：未使用 | 部分使用 | 已用完 | 已过期（由 used_count/expires_at 推导）。
	Status string `json:"status"`
	// Redeemed 是已兑换角色（名称优先，顿号分隔；角色已删/服务不可用时保留 ID，
	// 空串表示无人兑换）。按角色 ID 过滤仍走核销表的 character_id 子查询。
	Redeemed  string `json:"redeemed,omitempty"`
	ExpiresAt string `json:"expiresAt,omitempty"`
	Note      string `json:"note,omitempty"`
	CreatedAt string `json:"createdAt"`
}

// 编译期保证 DTO 与 management 实现的字段形态一致由各仓库测试覆盖；
// 这里只保留最小转换入口。
var errCdkUnavailable = errors.New("CDK 模块未启用")
