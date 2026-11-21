package launcherhub

import (
	"archive/zip"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
)

var bundlePath = regexp.MustCompile(`^/launcher/(?:v2|anticheat/v2)/bundles/([a-f0-9]{64})\.zip$`)
var versionPattern = regexp.MustCompile(`^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$`)

func versionAfter(a, b string) bool {
	aa := strings.Split(a, ".")
	bb := strings.Split(b, ".")
	for i := 0; i < 3; i++ {
		av, _ := strconv.Atoi(aa[i])
		bv, _ := strconv.Atoi(bb[i])
		if av != bv {
			return av > bv
		}
	}
	return false
}
// bundleFlavor is the signed build flavor advertised to launchers so an
// anticheat core only accepts an anticheat bundle and a standard core only a
// standard bundle. The value comes from build-flavor.json in the bundle root
// (produced by build_flutter_launcher.py and checked by BundleInstaller on the
// client). A missing marker is accepted as "standard" for legacy standard
// packages only; an explicit marker must be well-formed and known.
func bundleFlavor(z *zip.Reader, seen map[string]bool) (string, error) {
	if !seen["build-flavor.json"] {
		return "standard", nil
	}
	for _, f := range z.File {
		if strings.ToLower(f.Name) != "build-flavor.json" {
			continue
		}
		if f.UncompressedSize64 > 1024 {
			return "", errors.New("build flavor marker too large")
		}
		in, e := f.Open()
		if e != nil {
			return "", e
		}
		raw, e := io.ReadAll(io.LimitReader(in, 1025))
		in.Close()
		if e != nil {
			return "", e
		}
		var marker struct {
			Format int    `json:"format"`
			Flavor string `json:"flavor"`
		}
		if e = json.Unmarshal(raw, &marker); e != nil || marker.Format != 1 ||
			(marker.Flavor != "standard" && marker.Flavor != "anticheat") {
			return "", errors.New("invalid build flavor marker")
		}
		return marker.Flavor, nil
	}
	return "standard", nil
}

func validateBundle(file *os.File, size int64) (string, error) {
	z, e := zip.NewReader(file, size)
	if e != nil {
		return "", e
	}
	if len(z.File) < 5 || len(z.File) > 2000 {
		return "", errors.New("invalid bundle file count")
	}
	seen := map[string]bool{}
	var total int64
	for _, f := range z.File {
		if strings.HasSuffix(f.Name, "/") {
			continue
		}
		lower := strings.ToLower(f.Name)
		if !safePath(f.Name) || seen[lower] || f.Mode()&os.ModeSymlink != 0 || f.UncompressedSize64 > 500<<20 {
			return "", errors.New("invalid bundle path/type/size")
		}
		seen[lower] = true
		total += int64(f.UncompressedSize64)
		if total > 500<<20 {
			return "", errors.New("bundle too large")
		}
		// Read each member to validate CRC and bound the actual decompressed bytes.
		in, e := f.Open()
		if e != nil {
			return "", e
		}
		n, e := io.Copy(io.Discard, io.LimitReader(in, int64(f.UncompressedSize64)+1))
		in.Close()
		if e != nil || n != int64(f.UncompressedSize64) {
			return "", errors.New("corrupt bundle entry")
		}
	}
	for _, name := range []string{"dnf90.exe", "dnf90.core.exe", "flutter_windows.dll", "data/app.so", "data/icudtl.dat"} {
		if !seen[name] {
			return "", fmt.Errorf("bundle missing %s", name)
		}
	}
	flavor, e := bundleFlavor(z, seen)
	if e != nil {
		return "", e
	}
	return flavor, nil
}
func (h *Hub) uploadBundle(w http.ResponseWriter, r *http.Request) {
	version := strings.TrimPrefix(r.URL.Path, "/admin/api/bundle/")
	if !versionPattern.MatchString(version) {
		failure(w, 400, "版本格式应为 x.y.z")
		return
	}
	dir := filepath.Join(h.cfg.DataDir, "bundles")
	if e := os.MkdirAll(dir, 0700); e != nil {
		failure(w, 500, "无法创建发布目录")
		return
	}
	f, e := os.CreateTemp(dir, ".upload-")
	if e != nil {
		failure(w, 500, "无法暂存发布包")
		return
	}
	defer os.Remove(f.Name())
	defer f.Close()
	hash := sha256.New()
	size, e := io.Copy(io.MultiWriter(f, hash), http.MaxBytesReader(w, r.Body, 300<<20))
	if e != nil {
		failure(w, 400, "上传失败或超过 300 MiB 限制")
		return
	}
	flavor, e := validateBundle(f, size)
	if e != nil {
		failure(w, 400, e.Error())
		return
	}
	if e = f.Sync(); e != nil {
		failure(w, 500, "写入失败")
		return
	}
	f.Close()
	sha := hex.EncodeToString(hash.Sum(nil))
	target := filepath.Join(dir, sha+".zip")
	if e = os.Rename(f.Name(), target); e != nil {
		failure(w, 500, "发布文件保存失败")
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	previous := h.state.Published
	if old := previous.Launcher; old != nil && !versionAfter(version, old.Version) {
		failure(w, 409, "登录器版本必须递增")
		return
	}
	h.state.Published.Launcher = &Bundle{Version: version, URL: strings.TrimRight(h.profile.ApiURL, "/") + "/launcher/anticheat/v2/bundles/" + sha + ".zip", Size: size, SHA256: sha, BuildFlavor: flavor}
	// Publish the compiled account-binding policy with the new launcher, while
	// preserving the current game endpoints and signed content compatibility.
	h.state.Published.Profile.EnableQQBinding = h.profile.EnableQQBinding
	h.state.Published.Profile.RequireQQBinding = h.profile.RequireQQBinding
	h.state.Published.Sequence++
	if e = h.save(); e != nil {
		h.state.Published = previous
		failure(w, 500, "签名发布失败")
		return
	}
	reply(w, 200, h.state.Published.Launcher)
}
// bundleFlavorOnDisk reads the build flavor marker from an already published
// bundle. It is used to backfill legacy state that was signed before the
// buildFlavor field existed. Returns "" when the marker is absent/invalid or
// the package is not available, so the caller can fall back to URL inference.
func (h *Hub) bundleFlavorOnDisk(sha string) string {
	if !digestPattern.MatchString(sha) {
		return ""
	}
	path := filepath.Join(h.cfg.DataDir, "bundles", sha+".zip")
	f, e := os.Open(path)
	if e != nil {
		return ""
	}
	defer f.Close()
	info, e := f.Stat()
	if e != nil || info.Size() > 300<<20 {
		return ""
	}
	z, e := zip.NewReader(f, info.Size())
	if e != nil {
		return ""
	}
	for _, entry := range z.File {
		if strings.ToLower(entry.Name) != "build-flavor.json" || entry.UncompressedSize64 > 1024 {
			continue
		}
		in, e := entry.Open()
		if e != nil {
			return ""
		}
		raw, e := io.ReadAll(io.LimitReader(in, 1025))
		in.Close()
		if e != nil {
			return ""
		}
		var marker struct {
			Format int    `json:"format"`
			Flavor string `json:"flavor"`
		}
		if e = json.Unmarshal(raw, &marker); e != nil || marker.Format != 1 {
			return ""
		}
		if marker.Flavor == "standard" || marker.Flavor == "anticheat" {
			return marker.Flavor
		}
	}
	return ""
}

func (h *Hub) serveBundle(w http.ResponseWriter, r *http.Request) bool {
	match := bundlePath.FindStringSubmatch(r.URL.Path)
	if match == nil {
		return false
	}
	path := filepath.Join(h.cfg.DataDir, "bundles", match[1]+".zip")
	info, e := os.Lstat(path)
	if e != nil || !info.Mode().IsRegular() {
		http.NotFound(w, r)
		return true
	}
	if h.openlist.Redirect(w, r, match[1], info.Size()) || h.baidu.Redirect(w, r, match[1], info.Size()) {
		return true
	}
	w.Header().Set("Cache-Control", "public, max-age=31536000, immutable")
	w.Header().Set("ETag", `"`+match[1]+`"`)
	http.ServeFile(w, r, path)
	return true
}
