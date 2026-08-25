package auth

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestSteamVerifierFallsBackAfterPublisherForbidden(t *testing.T) {
	forbidden := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		http.Error(w, "forbidden", http.StatusForbidden)
	}))
	defer forbidden.Close()
	public := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("key") != "web-key" || r.URL.Query().Get("appid") != "2868840" || r.URL.Query().Get("ticket") != "ticket" {
			t.Fatal("Steam authentication query was not forwarded")
		}
		_, _ = w.Write([]byte(`{"response":{"params":{"result":"OK","steamid":"76561199871087714"}}}`))
	}))
	defer public.Close()

	verifier := SteamVerifier{
		APIKey: "web-key", AppID: "2868840", Client: forbidden.Client(),
		Endpoints: []string{forbidden.URL + "/", public.URL + "/"},
	}
	if err := verifier.Verify(context.Background(), "76561199871087714", "ticket"); err != nil {
		t.Fatal(err)
	}
}

func TestSteamVerifierDoesNotFallbackAfterTicketRejection(t *testing.T) {
	rejected := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write([]byte(`{"response":{"error":{"errorcode":101,"errordesc":"Invalid ticket"}}}`))
	}))
	defer rejected.Close()
	fallbackCalled := false
	fallback := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		fallbackCalled = true
		w.WriteHeader(http.StatusOK)
	}))
	defer fallback.Close()

	verifier := SteamVerifier{
		APIKey: "web-key", AppID: "2868840", Client: rejected.Client(),
		Endpoints: []string{rejected.URL + "/", fallback.URL + "/"},
	}
	if err := verifier.Verify(context.Background(), "76561199871087714", "ticket"); err == nil {
		t.Fatal("expected Steam ticket rejection")
	}
	if fallbackCalled {
		t.Fatal("ticket rejection must not fall back to another endpoint")
	}
}
