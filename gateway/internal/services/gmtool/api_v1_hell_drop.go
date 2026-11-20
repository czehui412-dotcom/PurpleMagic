package gmtool

import (
	"dnf90.local/gateway/internal/modules/dnf/adminapi"
	"net/http"
)

// v1HellDropRate reads the live hell epic drop rate from the game process.
func (s *Server) v1HellDropRate(w http.ResponseWriter, r *http.Request) {
	if r.URL.RawQuery != "" {
		v1Fail(w, r, 400, "invalid_query", "掉率查询不接受查询参数", false)
		return
	}
	if s.deps.Runtime == nil {
		v1Fail(w, r, 503, "hell_drop_unavailable", "当前服务未接入游戏运行时", true)
		return
	}
	result, err := s.deps.Runtime.Call(r.Context(), adminapi.Request{Action: "get_hell_drop_rate"})
	if err != nil || result.HellDropRate == nil {
		v1Fail(w, r, 503, "hell_drop_unavailable", "游戏掉率接口暂不可用，请重试", true)
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: result.HellDropRate})
}

// v1SetHellDropRate installs a runtime drop-rate override (0..1000 per-mille,
// or -1 to clear back to the PVF baseline). Takes effect on the next settlement.
func (s *Server) v1SetHellDropRate(w http.ResponseWriter, r *http.Request) {
	if r.URL.RawQuery != "" {
		v1Fail(w, r, 400, "invalid_query", "掉率设置不接受查询参数", false)
		return
	}
	var in struct {
		Rate *int `json:"rate"`
	}
	if !v1DecodeLimit(w, r, &in, 4<<10) {
		return
	}
	if in.Rate == nil || *in.Rate < -1 || *in.Rate > 1000 {
		v1Fail(w, r, 400, "invalid_rate", "掉率须为 -1（恢复 PVF）或 0–1000", false)
		return
	}
	if s.deps.Runtime == nil {
		v1Fail(w, r, 503, "hell_drop_unavailable", "当前服务未接入游戏运行时", true)
		return
	}
	result, err := s.deps.Runtime.Call(r.Context(), adminapi.Request{
		Action: "set_hell_drop_rate", HellDropRate: in.Rate,
	})
	if err != nil || result.HellDropRate == nil {
		v1Fail(w, r, 503, "hell_drop_unavailable", "游戏掉率设置失败，请重试", true)
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: result.HellDropRate})
}

// v1RiftDropRate reads the live time-rift epic drop rate from the game process.
func (s *Server) v1RiftDropRate(w http.ResponseWriter, r *http.Request) {
	if r.URL.RawQuery != "" {
		v1Fail(w, r, 400, "invalid_query", "裂缝掉率查询不接受查询参数", false)
		return
	}
	if s.deps.Runtime == nil {
		v1Fail(w, r, 503, "rift_drop_unavailable", "当前服务未接入游戏运行时", true)
		return
	}
	result, err := s.deps.Runtime.Call(r.Context(), adminapi.Request{Action: "get_rift_drop_rate"})
	if err != nil || result.RiftDropRate == nil {
		v1Fail(w, r, 503, "rift_drop_unavailable", "裂缝掉率接口暂不可用，请重试", true)
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: result.RiftDropRate})
}

// v1SetRiftDropRate installs a runtime override for the 316 rift epic-count table.
// 1000‰ keeps the PVF table always-on; -1 clears back to PVF.
func (s *Server) v1SetRiftDropRate(w http.ResponseWriter, r *http.Request) {
	if r.URL.RawQuery != "" {
		v1Fail(w, r, 400, "invalid_query", "裂缝掉率设置不接受查询参数", false)
		return
	}
	var in struct {
		Rate *int `json:"rate"`
	}
	if !v1DecodeLimit(w, r, &in, 4<<10) {
		return
	}
	if in.Rate == nil || *in.Rate < -1 || *in.Rate > 1000 {
		v1Fail(w, r, 400, "invalid_rate", "裂缝掉率须为 -1（恢复 PVF）或 0–1000", false)
		return
	}
	if s.deps.Runtime == nil {
		v1Fail(w, r, 503, "rift_drop_unavailable", "当前服务未接入游戏运行时", true)
		return
	}
	result, err := s.deps.Runtime.Call(r.Context(), adminapi.Request{
		Action: "set_rift_drop_rate", RiftDropRate: in.Rate,
	})
	if err != nil || result.RiftDropRate == nil {
		v1Fail(w, r, 503, "rift_drop_unavailable", "裂缝掉率设置失败，请重试", true)
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: result.RiftDropRate})
}

func validEpicPieceCount(count int) bool {
	return count == -1 || count == 0 || (count >= 2 && count <= 5)
}

// v1SetHellDropCount switches ordinary abyss between PVF/rate mode and the
// guaranteed 1..N pieces mode. It replaces, never stacks with, the rate tier.
func (s *Server) v1SetHellDropCount(w http.ResponseWriter, r *http.Request) {
	if r.URL.RawQuery != "" {
		v1Fail(w, r, 400, "invalid_query", "件数档设置不接受查询参数", false)
		return
	}
	var in struct {
		Count *int `json:"count"`
	}
	if !v1DecodeLimit(w, r, &in, 4<<10) {
		return
	}
	if in.Count == nil || !validEpicPieceCount(*in.Count) {
		v1Fail(w, r, 400, "invalid_count", "件数档须为 -1（退出）、0（关闭）或 2–5", false)
		return
	}
	if s.deps.Runtime == nil {
		v1Fail(w, r, 503, "hell_drop_unavailable", "当前服务未接入游戏运行时", true)
		return
	}
	result, err := s.deps.Runtime.Call(r.Context(), adminapi.Request{
		Action: "set_hell_drop_count", HellDropCount: in.Count,
	})
	if err != nil || result.HellDropRate == nil {
		v1Fail(w, r, 503, "hell_drop_unavailable", "深渊件数档设置失败，请重试", true)
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: result.HellDropRate})
}

// v1SetRiftDropCount switches rift settlements to the guaranteed 1..N tier.
func (s *Server) v1SetRiftDropCount(w http.ResponseWriter, r *http.Request) {
	if r.URL.RawQuery != "" {
		v1Fail(w, r, 400, "invalid_query", "裂缝件数档设置不接受查询参数", false)
		return
	}
	var in struct {
		Count *int `json:"count"`
	}
	if !v1DecodeLimit(w, r, &in, 4<<10) {
		return
	}
	if in.Count == nil || !validEpicPieceCount(*in.Count) {
		v1Fail(w, r, 400, "invalid_count", "件数档须为 -1（退出）、0（关闭）或 2–5", false)
		return
	}
	if s.deps.Runtime == nil {
		v1Fail(w, r, 503, "rift_drop_unavailable", "当前服务未接入游戏运行时", true)
		return
	}
	result, err := s.deps.Runtime.Call(r.Context(), adminapi.Request{
		Action: "set_rift_drop_count", RiftDropCount: in.Count,
	})
	if err != nil || result.RiftDropRate == nil {
		v1Fail(w, r, 503, "rift_drop_unavailable", "裂缝件数档设置失败，请重试", true)
		return
	}
	v1Reply(w, r, 200, v1Envelope{Data: result.RiftDropRate})
}
