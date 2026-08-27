package auth

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

type Claims struct {
	PlayerID    string `json:"sub"`
	DisplayName string `json:"name"`
	ExpiresAt   int64  `json:"exp"`
	Kind        string `json:"kind"`
}

type Manager struct{ secret []byte }

func NewManager(secret string) *Manager { return &Manager{secret: []byte(secret)} }

func (m *Manager) Issue(playerID, displayName string) (string, error) {
	return m.issue(playerID, displayName, "access", 2*time.Hour)
}

func (m *Manager) IssueRefresh(playerID, displayName string) (string, error) {
	return m.issue(playerID, displayName, "refresh", 30*24*time.Hour)
}

func (m *Manager) issue(playerID, displayName, kind string, lifetime time.Duration) (string, error) {
	payload, err := json.Marshal(Claims{PlayerID: playerID, DisplayName: displayName, ExpiresAt: time.Now().Add(lifetime).Unix(), Kind: kind})
	if err != nil {
		return "", err
	}
	encoded := base64.RawURLEncoding.EncodeToString(payload)
	mac := hmac.New(sha256.New, m.secret)
	_, _ = mac.Write([]byte(encoded))
	return encoded + "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil)), nil
}

func (m *Manager) ParseRefresh(token string) (Claims, error) {
	claims, err := m.Parse(token)
	if err != nil {
		return Claims{}, err
	}
	if claims.Kind != "refresh" {
		return Claims{}, errors.New("not a refresh token")
	}
	return claims, nil
}

func (m *Manager) Parse(token string) (Claims, error) {
	parts := strings.Split(token, ".")
	if len(parts) != 2 {
		return Claims{}, errors.New("invalid token")
	}
	mac := hmac.New(sha256.New, m.secret)
	_, _ = mac.Write([]byte(parts[0]))
	sig, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil || !hmac.Equal(sig, mac.Sum(nil)) {
		return Claims{}, errors.New("invalid signature")
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		return Claims{}, err
	}
	var claims Claims
	if err := json.Unmarshal(payload, &claims); err != nil {
		return Claims{}, err
	}
	if claims.ExpiresAt < time.Now().Unix() {
		return Claims{}, errors.New("token expired")
	}
	return claims, nil
}

type contextKey struct{}

func WithClaims(ctx context.Context, claims Claims) context.Context {
	return context.WithValue(ctx, contextKey{}, claims)
}
func FromContext(ctx context.Context) (Claims, bool) {
	c, ok := ctx.Value(contextKey{}).(Claims)
	return c, ok
}

func (m *Manager) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		token := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
		if token == "" {
			token = r.URL.Query().Get("token")
		}
		claims, err := m.Parse(token)
		if err != nil || claims.Kind != "access" {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		next.ServeHTTP(w, r.WithContext(WithClaims(r.Context(), claims)))
	})
}

type SteamVerifier struct {
	APIKey, AppID string
	AllowDev      bool
	Client        *http.Client
	Endpoints     []string
}

func (v SteamVerifier) Verify(ctx context.Context, steamID, ticket string) error {
	if v.AllowDev && steamID != "" && ticket == "development" {
		return nil
	}
	if v.APIKey == "" {
		return errors.New("steam authentication is not configured")
	}
	q := url.Values{"key": {v.APIKey}, "appid": {v.AppID}, "ticket": {ticket}}
	client := v.Client
	if client == nil {
		client = &http.Client{Timeout: 10 * time.Second}
	}
	endpoints := v.Endpoints
	if len(endpoints) == 0 {
		endpoints = []string{
			"https://partner.steam-api.com/ISteamUserAuth/AuthenticateUserTicket/v1/",
			"https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/",
		}
	}
	for index, endpoint := range endpoints {
		err := verifySteamTicketEndpoint(ctx, client, endpoint+"?"+q.Encode(), steamID)
		if errors.Is(err, errSteamPublisherForbidden) && index+1 < len(endpoints) {
			continue
		}
		return err
	}
	return errors.New("steam authentication endpoint is not configured")
}

var errSteamPublisherForbidden = errors.New("steam publisher endpoint forbidden")

func verifySteamTicketEndpoint(ctx context.Context, client *http.Client, endpoint, steamID string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return err
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusForbidden {
		return errSteamPublisherForbidden
	}
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("steam returned %s", resp.Status)
	}
	var body struct {
		Response struct {
			Params struct {
				Result  string `json:"result"`
				SteamID string `json:"steamid"`
			} `json:"params"`
			Error *struct {
				ErrorCode int    `json:"errorcode"`
				ErrorDesc string `json:"errordesc"`
			} `json:"error"`
		} `json:"response"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		return err
	}
	if body.Response.Error != nil {
		return fmt.Errorf("steam auth %d: %s", body.Response.Error.ErrorCode, body.Response.Error.ErrorDesc)
	}
	if body.Response.Params.Result != "OK" || body.Response.Params.SteamID != steamID {
		return errors.New("steam ticket identity mismatch")
	}
	if _, err := strconv.ParseUint(steamID, 10, 64); err != nil {
		return errors.New("invalid steam id")
	}
	return nil
}
