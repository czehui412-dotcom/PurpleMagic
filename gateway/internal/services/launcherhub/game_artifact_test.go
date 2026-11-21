package launcherhub

import (
	"encoding/base64"
	"encoding/json"
	"testing"
)

// format=2 only: the signed bootstrap carries a single Game artifact and no
// legacy Content/Archive fields. The channel is selected by Delivery.Strategy.
func TestGameArtifactDerivationAndSigning(t *testing.T) {
	h, _ := fixture(t)

	before, _ := json.Marshal(h.state.Published)
	_, e := New(h.cfg)
	if e != nil { t.Fatal(e) }
	after, _ := json.Marshal(h.state.Published)
	if string(before) != string(after) { t.Fatal("restart changed payload with no game channel") }

	arch := &GameArchive{
		PrimaryURL: "http://cdn.example/a.zip", FallbackURL: "http://cdn.example/b.zip",
		SHA256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
		Size:   150130232,
		Files: []ArchiveFile{
			{Path: "DNF.exe", Length: 50000000, SHA256: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},
			{Path: "Script.pvf", Length: 100000000, SHA256: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"},
		},
	}
	h.state.Published.Game = gameArtifactFromArchive(arch)
	g := h.state.Published.Game
	if g == nil || g.Delivery.Strategy != "archive" || g.Delivery.URLs[0] != arch.PrimaryURL ||
		g.Delivery.PackageSHA256 != arch.SHA256 || g.Delivery.PackageSize != arch.Size || len(g.Files) != 2 {
		t.Fatalf("archive-derived Game wrong: %+v", g)
	}
	if g.Files[0].SHA256 != arch.Files[0].SHA256 || h.state.Published.Format != 2 {
		t.Fatal("Game file digests or format=2 not set")
	}

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
		Content *json.RawMessage `json:"content"`
		Archive *json.RawMessage `json:"archive"`
	}
	if e = json.Unmarshal(decoded, &payload); e != nil { t.Fatal(e) }
	if payload.Format != 2 || payload.Game.Delivery.Strategy != "archive" || len(payload.Game.Files) != 2 {
		t.Fatalf("signed bootstrap missing format=2 game: format=%d game=%+v", payload.Format, payload.Game)
	}
	if payload.Content != nil || payload.Archive != nil { t.Fatal("legacy content/archive fields must not be published") }

	// A blob-strategy Game is the same artifact with a different delivery.
	h.state.Published.Game = &GameArtifact{Version: "1.2.27", SourceID: "src", Sequence: 32,
		Files: []ArtifactFile{{Path: "Script.pvf", Length: 10, SHA256: "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}},
		Delivery: GameDelivery{Strategy: "blob", Endpoint: "https://example.test/blob"}}
	g = h.state.Published.Game
	if g.Delivery.Strategy != "blob" || g.Delivery.Endpoint != "https://example.test/blob" || g.Version != "1.2.27" {
		t.Fatalf("blob Game wrong: %+v", g)
	}
}
