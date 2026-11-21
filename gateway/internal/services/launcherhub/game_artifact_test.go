package launcherhub

import (
	"encoding/base64"
	"encoding/json"
	"testing"
)

// P4: the unified Game artifact must be derived from both legacy channels and
// carried in the signed format=2 bootstrap for the new launcher to consume.
func TestGameArtifactDerivationAndSigning(t *testing.T) {
	h, _ := fixture(t)

	// Restart must never silently change a signed payload (no game channel yet).
	before, _ := json.Marshal(h.state.Published)
	_, e := New(h.cfg)
	if e != nil { t.Fatal(e) }
	after, _ := json.Marshal(h.state.Published)
	if string(before) != string(after) { t.Fatal("restart changed payload with no game channel") }

	// Publish an archive channel directly and sync the unified artifact.
	arch := &GameArchive{
		PrimaryURL: "http://cdn.example/a.zip", FallbackURL: "http://cdn.example/b.zip",
		SHA256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
		Size:   150130232,
		Files: []ArchiveFile{
			{Path: "DNF.exe", Length: 50000000, SHA256: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},
			{Path: "Script.pvf", Length: 100000000, SHA256: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"},
		},
	}
	h.state.Published.Archive = arch
	if !h.syncGameArtifact() { t.Fatal("sync should derive Game from archive") }
	g := h.state.Published.Game
	if g == nil || g.Delivery.Strategy != "archive" || g.Delivery.URLs[0] != arch.PrimaryURL ||
		g.Delivery.PackageSHA256 != arch.SHA256 || g.Delivery.PackageSize != arch.Size || len(g.Files) != 2 {
		t.Fatalf("archive-derived Game wrong: %+v", g)
	}
	if g.Files[0].SHA256 != arch.Files[0].SHA256 || h.state.Published.Format != 2 {
		t.Fatal("Game file digests or format=2 not set")
	}
	// Idempotent: a second sync changes nothing.
	if h.syncGameArtifact() { t.Fatal("second sync must be a no-op") }

	// The signed bootstrap must carry game for format=2 clients.
	raw := call(h.PublicHandler(), "GET", "/launcher/anticheat/v2/bootstrap.json", "", nil, "", "").Body.Bytes()
	var env struct{ Payload string `json:"payload"` }
	if e := json.Unmarshal(raw, &env); e != nil { t.Fatal(e) }
	decoded, e := base64.StdEncoding.DecodeString(env.Payload)
	if e != nil { t.Fatal(e) }
	var payload struct {
		Format int `json:"format"`
		Game   struct {
			Delivery struct{ Strategy string `json:"strategy"` } `json:"delivery"`
			Files    []struct{ Path string `json:"path"` } `json:"files"`
		} `json:"game"`
		Archive *GameArchive `json:"archive"`
	}
	if e = json.Unmarshal(decoded, &payload); e != nil { t.Fatal(e) }
	if payload.Format != 2 || payload.Game.Delivery.Strategy != "archive" || len(payload.Game.Files) != 2 {
		t.Fatalf("signed bootstrap missing format=2 game: format=%d game=%+v", payload.Format, payload.Game)
	}
	if payload.Archive == nil { t.Fatal("legacy archive field must be kept for one transition version") }

	// Disabling archive falls back to content (blob strategy) if present.
	h.state.Published.Archive = nil
	h.state.Published.Content = &Content{Version: "1.2.27", SourceID: "src", SourceSequence: 32,
		Endpoint: "https://example.test/blob", Files: []File{{Path: "Script.pvf", Length: 10, Sha256: "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}}}
	h.syncGameArtifact()
	g = h.state.Published.Game
	if g == nil || g.Delivery.Strategy != "blob" || g.Delivery.Endpoint != "https://example.test/blob" || g.Version != "1.2.27" {
		t.Fatalf("blob-derived Game wrong: %+v", g)
	}
}
