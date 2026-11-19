package gmtool

// 注册限制管理 v1 路由（邀请码 / 注册记录 / 机器码与 IP 解绑）。
// 数据与操作在游戏服 dnfbridge（registration_* action，经 Runtime 管理通道），
// 网关只做转发、鉴权与封装；鉴权与写保护复用 v1 层（cookie + CSRF）。

import (
	"errors"
	"net/http"
	"strconv"
	"strings"

	"dnf90.local/gateway/internal/modules/dnf/adminapi"
)

func (s *Server) v1RegistrationReady(w http.ResponseWriter, r *http.Request) bool {
	if s.deps.Runtime == nil {
		s.v1Unavailable(w, r, errors.New("游戏运行时通道不可用"))
		return false
	}
	return true
}

// registrationPage 解析 page/pageSize（与 CDK 列表同口径）。
func registrationPage(r *http.Request) adminapi.RegistrationQuery {
	q := adminapi.RegistrationQuery{Limit: 50}
	if raw := r.URL.Query().Get("page"); raw != "" {
		if v, err := strconv.Atoi(raw); err == nil && v > 1 {
			q.Offset = (v - 1) * q.Limit
		}
	}
	if raw := r.URL.Query().Get("pageSize"); raw != "" {
		if v, err := strconv.Atoi(raw); err == nil && v >= 1 && v <= 200 {
			q.Limit = v
		}
	}
	return q
}

// registrationCall 统一调用游戏服管理通道并映射错误。
// 注意 adminapi.Client.Call 在 ok:false 时返回 (reply, err)：带本地化 Error 的
// 属业务拒绝（400），其余才是通道故障（502）。
func (s *Server) registrationCall(w http.ResponseWriter, r *http.Request, req adminapi.Request) (adminapi.Response, bool) {
	reply, err := s.deps.Runtime.Call(r.Context(), req)
	if err != nil {
		if !reply.OK && reply.Error != "" {
			v1Fail(w, r, 400, "registration_rejected", reply.Error, false)
			return reply, false
		}
		v1Fail(w, r, 502, "registration_runtime_failed", "游戏服务暂时无法访问", false)
		return reply, false
	}
	if !reply.OK {
		v1Fail(w, r, 400, "registration_rejected", reply.Error, false)
		return reply, false
	}
	return reply, true
}

func (s *Server) v1RegistrationInvites(w http.ResponseWriter, r *http.Request) {
	if !s.v1RegistrationReady(w, r) {
		return
	}
	q := registrationPage(r)
	page := 1
	if q.Offset > 0 && q.Limit > 0 {
		page = q.Offset/q.Limit + 1
	}
	reply, ok := s.registrationCall(w, r, adminapi.Request{Action: "registration_invites", RegistrationQuery: &q})
	if !ok {
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: map[string]any{
		"rows":  reply.RegistrationInvites,
		"total": reply.RegistrationTotal,
		"page":  page,
	}})
}

type registrationInviteRequest struct {
	Op          string `json:"op"`
	Code        string `json:"code"`
	Count       int    `json:"count"`
	MaxUses     int    `json:"maxUses"`
	ExpiresDays int    `json:"expiresDays"`
	Note        string `json:"note"`
}

func (s *Server) v1RegistrationInviteSave(w http.ResponseWriter, r *http.Request) {
	if !s.v1RegistrationReady(w, r) {
		return
	}
	var body registrationInviteRequest
	if !v1Decode(w, r, &body) {
		return
	}
	switch strings.ToLower(strings.TrimSpace(body.Op)) {
	case "create":
	case "revoke":
		if strings.TrimSpace(body.Code) == "" {
			v1Fail(w, r, 400, "invalid_invite", "请填写要作废的邀请码", false)
			return
		}
	default:
		v1Fail(w, r, 400, "invalid_invite", "op 必须是 create 或 revoke", false)
		return
	}
	reply, ok := s.registrationCall(w, r, adminapi.Request{
		Action: "registration_invite_save",
		RegistrationInvite: &adminapi.RegistrationInviteChange{
			Op:          strings.ToLower(strings.TrimSpace(body.Op)),
			Code:        strings.ToUpper(strings.TrimSpace(body.Code)),
			Count:       body.Count,
			MaxUses:     body.MaxUses,
			ExpiresDays: body.ExpiresDays,
			Note:        strings.TrimSpace(body.Note),
		},
	})
	if !ok {
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: map[string]any{
		"codes":   reply.RegistrationCodes,
		"changed": reply.RegistrationChanged,
	}})
}

func (s *Server) v1RegistrationRecords(w http.ResponseWriter, r *http.Request) {
	if !s.v1RegistrationReady(w, r) {
		return
	}
	q := registrationPage(r)
	page := 1
	if q.Offset > 0 && q.Limit > 0 {
		page = q.Offset/q.Limit + 1
	}
	reply, ok := s.registrationCall(w, r, adminapi.Request{Action: "registration_records", RegistrationQuery: &q})
	if !ok {
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: map[string]any{
		"rows":  reply.RegistrationRecords,
		"total": reply.RegistrationTotal,
		"page":  page,
	}})
}

type registrationUnbindRequest struct {
	Kind  string `json:"kind"`
	Value string `json:"value"`
}

func (s *Server) v1RegistrationUnbind(w http.ResponseWriter, r *http.Request) {
	if !s.v1RegistrationReady(w, r) {
		return
	}
	var body registrationUnbindRequest
	if !v1Decode(w, r, &body) {
		return
	}
	kind := strings.ToLower(strings.TrimSpace(body.Kind))
	if kind != "device" && kind != "ip" {
		v1Fail(w, r, 400, "invalid_unbind", "kind 必须是 device 或 ip", false)
		return
	}
	if strings.TrimSpace(body.Value) == "" {
		v1Fail(w, r, 400, "invalid_unbind", "请填写要解绑的机器码或 IP", false)
		return
	}
	reply, ok := s.registrationCall(w, r, adminapi.Request{
		Action:             "registration_unbind",
		RegistrationUnbind: &adminapi.RegistrationUnbind{Kind: kind, Value: strings.TrimSpace(body.Value)},
	})
	if !ok {
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: map[string]any{"changed": reply.RegistrationChanged}})
}
