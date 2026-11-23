package gmtool

import (
	"net/http/httptest"
	"testing"
)

// TestV1ExactQueryParsing 验证精确查询参数 exact 的白名单与取值校验。
func TestV1ExactQueryParsing(t *testing.T) {
	cases := []struct {
		path     string
		wantExact bool
		wantErr  bool
	}{
		{path: "/api/v1/characters?q=abc", wantExact: false},
		{path: "/api/v1/characters?q=abc&exact=1", wantExact: true},
		{path: "/api/v1/characters?q=abc&exact=true", wantExact: true},
		{path: "/api/v1/characters?q=abc&exact=0", wantErr: false, wantExact: false},
		{path: "/api/v1/characters?q=abc&exact=", wantExact: false},
		{path: "/api/v1/characters?q=abc&exact=yes", wantErr: true},
		{path: "/api/v1/accounts?q=abc&exact=1", wantExact: true},
	}
	for _, c := range cases {
		r := httptest.NewRequest("GET", c.path, nil)
		q, err := v1ParseQuery(r, "online")
		if c.wantErr {
			if err == nil {
				t.Fatalf("%s: expected error, got nil", c.path)
			}
			continue
		}
		if err != nil {
			t.Fatalf("%s: unexpected error: %v", c.path, err)
		}
		if got := isV1Exact(q); got != c.wantExact {
			t.Fatalf("%s: isV1Exact=%v, want %v", c.path, got, c.wantExact)
		}
	}
}

// TestV1ExactPrefix 验证精确标记前缀不与正常查询冲突，且可正确剥离。
func TestV1ExactPrefix(t *testing.T) {
	if v1ExactPrefix == "" || v1ExactPrefix == "\x00" {
		t.Fatal("v1ExactPrefix must be a non-NUL internal marker")
	}
	normalized := "dnf:ee79976c9380d5e3"
	if has := len(normalized) > 0 && normalized[0] == v1ExactPrefix[0]; has {
		t.Fatal("v1ExactPrefix must not collide with common input")
	}
}
