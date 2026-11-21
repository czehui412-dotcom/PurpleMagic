package launcherhub

import (
	"encoding/json"
	"net/http"
	"net/url"
	"regexp"
	"strings"
)

// Only the whole-archive delivery metadata is editable here. Trust roots,
// account endpoints and the per-file blob channel retain their values.
// The operator supplies the two direct-link URLs; only the signed digests are
// authoritative, so an HTTP link cannot substitute altered content.

var archivePathPart = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]*$`)

func archiveRevision(a *GameArchive) string {
	if a == nil {
		return digest([]byte("empty"))
	}
	raw, _ := json.Marshal(a)
	return digest(raw)
}

// validArchiveURL accepts the direct-link shapes produced by share-link
// resolvers: absolute http(s) URL with a host, optional path and query (the
// resolver token lives in the query), but no userinfo or fragment.
func validArchiveURL(raw string) bool {
	if raw == "" {
		return true // optional (e.g. fallback not configured yet)
	}
	u, err := url.Parse(raw)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" ||
		u.User != nil || u.Fragment != "" || len(raw) > 2048 {
		return false
	}
	return true
}

func validArchive(a *GameArchive) error {
	if a == nil || !digestPattern.MatchString(a.SHA256) || a.Size < 1 || a.Size > 8<<30 {
		return errArchive("资源包摘要或大小无效")
	}
	if !validArchiveURL(a.PrimaryURL) || !validArchiveURL(a.FallbackURL) {
		return errArchive("直链地址无效：须为 http(s) 绝对地址，不含用户名或片段")
	}
	// Both URLs empty is the "disabled" sentinel; the launcher then keeps using
	// the per-file blob channel. Only reject equal non-empty URLs.
	if a.PrimaryURL != "" && a.PrimaryURL == a.FallbackURL {
		return errArchive("主备直链不能相同")
	}
	if len(a.Files) < 1 || len(a.Files) > 2000 {
		return errArchive("资源包文件清单数量无效")
	}
	seen := map[string]bool{}
	var total int64
	for _, f := range a.Files {
		if f.Path == "" || len(f.Path) > 240 || strings.HasPrefix(f.Path, "/") ||
			strings.Contains(f.Path, "\\") || strings.Contains(f.Path, ":") {
			return errArchive("资源包文件路径无效")
		}
		for _, part := range strings.Split(f.Path, "/") {
			if part == "" || part == "." || part == ".." || !archivePathPart.MatchString(part) ||
				strings.HasPrefix(part, ".dnf90") {
				return errArchive("资源包文件路径含非法片段")
			}
		}
		if !digestPattern.MatchString(f.SHA256) || f.Length < 1 || f.Length > 8<<30 {
			return errArchive("资源包文件摘要或长度无效")
		}
		key := strings.ToLower(f.Path)
		if seen[key] {
			return errArchive("资源包文件清单存在重复路径")
		}
		seen[key] = true
		total += f.Length
	}
	if total == 0 {
		return errArchive("资源包文件清单为空")
	}
	return nil
}

type archiveError string

func (e archiveError) Error() string { return string(e) }
func errArchive(msg string) error    { return archiveError(msg) }

func (h *Hub) publishArchive(w http.ResponseWriter, r *http.Request) {
	var input GameArchive
	if err := readBody(w, r, &input); err != nil {
		failure(w, 400, err.Error())
		return
	}
	if err := validArchive(&input); err != nil {
		failure(w, 400, err.Error())
		return
	}
	// EXE/PVF delivered by the archive must match the fixed trust root in the
	// profile, otherwise GameContract.VerifyClient would reject the player.
	// Skip this when disabling (both URLs empty).
	h.mu.Lock()
	defer h.mu.Unlock()
	if input.PrimaryURL != "" || input.FallbackURL != "" {
		for _, f := range input.Files {
			base := f.Path[strings.LastIndex(f.Path, "/")+1:]
			var expected string
			switch base {
			case "DNF.exe":
				expected = h.profile.ExeSHA256
			case "Script.pvf":
				expected = h.profile.PvfSHA256
			}
			if expected != "" && !strings.EqualFold(f.SHA256, expected) {
				failure(w, 400, "资源包内 "+base+" 的哈希与当前信任根不一致，请先用发布脚本更新 profile")
				return
			}
		}
	}
	before := archiveRevision(h.state.Published.Archive)
	if r.Header.Get("If-Match") == "" {
		failure(w, 428, "请先读取当前资源包配置")
		return
	}
	if r.Header.Get("If-Match") != `"`+before+`"` {
		failure(w, 409, "游戏资源直链已被其他窗口修改，请重新载入")
		return
	}
	previous := h.state.Published
	// Both URLs empty disables the channel: publish nil so the signed bootstrap
	// carries no archive and the launcher keeps its per-file channel.
	if input.PrimaryURL == "" && input.FallbackURL == "" {
		h.state.Published.Archive = nil
	} else {
		h.state.Published.Archive = &input
	}
	h.state.Published.Sequence++
	if err := h.save(); err != nil {
		h.state.Published = previous
		failure(w, 500, "发布游戏资源直链失败")
		return
	}
	reply(w, 200, map[string]any{"ok": true, "archiveRevision": archiveRevision(&input), "sequence": h.state.Published.Sequence})
}
