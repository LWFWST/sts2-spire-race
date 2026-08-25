package httpapi

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/mcc/sts2-spire-race/server/internal/auth"
)

func TestOfficialAuthRejectsSteamIDOutsideAllowlist(t *testing.T) {
	server := &Server{
		Steam:          auth.SteamVerifier{AllowDev: true},
		officialServer: true,
		steamAllowlist: map[string]bool{"76561199871087714": true},
	}
	body, _ := json.Marshal(map[string]string{"steam_id": "76561199830377452", "display_name": "test", "ticket": "development"})
	request := httptest.NewRequest(http.MethodPost, "/v1/auth/steam", bytes.NewReader(body))
	response := httptest.NewRecorder()
	server.steamAuth(response, request)
	if response.Code != http.StatusForbidden { t.Fatalf("expected 403, got %d", response.Code) }
	var payload map[string]string
	if err := json.Unmarshal(response.Body.Bytes(), &payload); err != nil { t.Fatal(err) }
	if payload["code"] != "beta_access_required" { t.Fatalf("unexpected error code: %q", payload["code"]) }
}
