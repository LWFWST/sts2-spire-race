package httpapi

import (
	"context"
	"crypto/rand"
	"encoding/json"
	"errors"
	"log/slog"
	"math/big"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/mcc/sts2-spire-race/server/internal/auth"
	"github.com/mcc/sts2-spire-race/server/internal/domain"
	"github.com/mcc/sts2-spire-race/server/internal/integrity"
	"github.com/mcc/sts2-spire-race/server/internal/match"
	"github.com/mcc/sts2-spire-race/server/internal/matchmaking"
	"github.com/mcc/sts2-spire-race/server/internal/realtime"
	"github.com/mcc/sts2-spire-race/server/internal/storage"
)

type Server struct {
	Mux            *http.ServeMux
	Tokens         *auth.Manager
	Steam          auth.SteamVerifier
	Store          *storage.Postgres
	Queue          *matchmaking.Queue
	Matches        *match.Service
	Hub            *realtime.Hub
	Integrity      integrity.Service
	queueMu        sync.Mutex
	queueByPlayer  map[string]domain.QueueRequest
	partyMu        sync.Mutex
	parties        map[string]*partyState
	partyByPlayer  map[string]string
	officialServer bool
	steamAllowlist map[string]bool
	roomLobbyMu    sync.Mutex
	roomLobbies    map[string]map[int]string
}

type partyState struct {
	ID             string
	LeaderPlayerID string
	Kind           string
	TeamSize       int
	Members        []string
	CharacterIDs   map[string]string
}

func New(tokens *auth.Manager, steam auth.SteamVerifier, store *storage.Postgres, queue *matchmaking.Queue, integrityService integrity.Service,
	officialServer bool, steamAllowlist map[string]bool) *Server {
	s := &Server{
		Mux: http.NewServeMux(), Tokens: tokens, Steam: steam, Store: store, Queue: queue, Integrity: integrityService,
		queueByPlayer: map[string]domain.QueueRequest{}, parties: map[string]*partyState{}, partyByPlayer: map[string]string{},
		officialServer: officialServer, steamAllowlist: steamAllowlist,
		roomLobbies: map[string]map[int]string{},
	}
	s.Matches = match.New(store, nil)
	s.Hub = realtime.NewHub(s.onDisconnect)
	s.Matches.SetNotifier(s.Hub)
	s.routes()
	return s
}

func (s *Server) routes() {
	s.Mux.HandleFunc("GET /health", s.health)
	s.Mux.HandleFunc("GET /v1/clock", s.clock)
	s.Mux.HandleFunc("POST /v1/auth/steam", s.steamAuth)
	s.Mux.HandleFunc("POST /v1/auth/refresh", s.refreshAuth)
	s.Mux.Handle("GET /v1/ws", s.Tokens.Middleware(http.HandlerFunc(s.websocket)))
	s.Mux.Handle("POST /v1/queue/join", s.Tokens.Middleware(http.HandlerFunc(s.joinQueue)))
	s.Mux.Handle("POST /v1/queue/cancel", s.Tokens.Middleware(http.HandlerFunc(s.cancelQueue)))
	s.Mux.Handle("POST /v1/match/confirm", s.Tokens.Middleware(http.HandlerFunc(s.confirmMatch)))
	s.Mux.Handle("POST /v1/match/ready", s.Tokens.Middleware(http.HandlerFunc(s.readyMatch)))
	s.Mux.Handle("GET /v1/match/current", s.Tokens.Middleware(http.HandlerFunc(s.currentMatch)))
	s.Mux.Handle("GET /v1/profile", s.Tokens.Middleware(http.HandlerFunc(s.profile)))
	s.Mux.Handle("PUT /v1/profile", s.Tokens.Middleware(http.HandlerFunc(s.updateProfile)))
	s.Mux.Handle("GET /v1/profile/{player}", s.Tokens.Middleware(http.HandlerFunc(s.playerProfile)))
	s.Mux.Handle("GET /v1/history", s.Tokens.Middleware(http.HandlerFunc(s.history)))
	s.Mux.Handle("GET /v1/leaderboard", s.Tokens.Middleware(http.HandlerFunc(s.leaderboard)))
	s.Mux.Handle("GET /v1/friends", s.Tokens.Middleware(http.HandlerFunc(s.friends)))
	s.Mux.Handle("GET /v1/players/search", s.Tokens.Middleware(http.HandlerFunc(s.searchPlayers)))
	s.Mux.Handle("POST /v1/friends/request", s.Tokens.Middleware(http.HandlerFunc(s.requestFriend)))
	s.Mux.Handle("POST /v1/friends/accept", s.Tokens.Middleware(http.HandlerFunc(s.acceptFriend)))
	s.Mux.Handle("POST /v1/friends/decline", s.Tokens.Middleware(http.HandlerFunc(s.declineFriend)))
	s.Mux.Handle("POST /v1/friends/remove", s.Tokens.Middleware(http.HandlerFunc(s.removeFriend)))
	s.Mux.Handle("POST /v1/rooms", s.Tokens.Middleware(http.HandlerFunc(s.createRoom)))
	s.Mux.Handle("POST /v1/rooms/join", s.Tokens.Middleware(http.HandlerFunc(s.joinRoom)))
	s.Mux.Handle("PUT /v1/rooms/{code}/rules", s.Tokens.Middleware(http.HandlerFunc(s.updateRoomRules)))
	s.Mux.Handle("POST /v1/rooms/{code}/team", s.Tokens.Middleware(http.HandlerFunc(s.switchRoomTeam)))
	s.Mux.Handle("PUT /v1/rooms/{code}/member", s.Tokens.Middleware(http.HandlerFunc(s.updateRoomMember)))
	s.Mux.Handle("POST /v1/rooms/{code}/start", s.Tokens.Middleware(http.HandlerFunc(s.startRoom)))
	s.Mux.Handle("POST /v1/rooms/{code}/leave", s.Tokens.Middleware(http.HandlerFunc(s.leaveRoom)))
	s.Mux.HandleFunc("GET /v1/integrity/{version}", s.manifest)
	s.Mux.Handle("POST /v1/integrity/verify", s.Tokens.Middleware(http.HandlerFunc(s.verifyIntegrity)))
}

func (s *Server) health(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
	defer cancel()
	if err := s.Store.Pool.Ping(ctx); err != nil {
		writeError(w, http.StatusServiceUnavailable, "postgres unavailable")
		return
	}
	if err := s.Queue.Client.Ping(ctx).Err(); err != nil {
		writeError(w, http.StatusServiceUnavailable, "redis unavailable")
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"status": "ok", "time_ms": time.Now().UnixMilli()})
}
func (s *Server) clock(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{"server_unix_ms": time.Now().UnixMilli()})
}

func (s *Server) steamAuth(w http.ResponseWriter, r *http.Request) {
	var body struct {
		SteamID     string `json:"steam_id"`
		DisplayName string `json:"display_name"`
		Ticket      string `json:"ticket"`
	}
	if !decode(w, r, &body) {
		return
	}
	if err := s.Steam.Verify(r.Context(), body.SteamID, body.Ticket); err != nil {
		writeError(w, http.StatusUnauthorized, err.Error())
		return
	}
	if s.officialServer && !s.steamAllowlist[body.SteamID] {
		writeErrorCode(w, http.StatusForbidden, "beta_access_required", "Steam account is not in the closed beta allowlist")
		return
	}
	if err := s.Store.UpsertPlayer(r.Context(), body.SteamID, body.DisplayName); err != nil {
		writeError(w, 500, err.Error())
		return
	}
	displayName, _ := s.Store.Player(r.Context(), body.SteamID)
	token, err := s.Tokens.Issue(body.SteamID, displayName)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	refresh, err := s.Tokens.IssueRefresh(body.SteamID, displayName)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	_ = s.Queue.TouchPresence(r.Context(), body.SteamID)
	writeJSON(w, http.StatusOK, map[string]any{"access_token": token, "refresh_token": refresh, "expires_in": 7200})
}

func (s *Server) refreshAuth(w http.ResponseWriter, r *http.Request) {
	var body struct {
		RefreshToken string `json:"refresh_token"`
	}
	if !decode(w, r, &body) {
		return
	}
	claims, err := s.Tokens.ParseRefresh(body.RefreshToken)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "refresh session expired")
		return
	}
	if s.officialServer && !s.steamAllowlist[claims.PlayerID] {
		writeErrorCode(w, http.StatusForbidden, "beta_access_required", "Steam account is not in the closed beta allowlist")
		return
	}
	displayName, err := s.Store.Player(r.Context(), claims.PlayerID)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "player not found")
		return
	}
	access, err := s.Tokens.Issue(claims.PlayerID, displayName)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	refresh, err := s.Tokens.IssueRefresh(claims.PlayerID, displayName)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	_ = s.Queue.TouchPresence(r.Context(), claims.PlayerID)
	writeJSON(w, http.StatusOK, map[string]any{"access_token": access, "refresh_token": refresh, "expires_in": 7200})
}

func (s *Server) joinQueue(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	var req domain.QueueRequest
	if !decode(w, r, &req) {
		return
	}
	req.PlayerID, req.DisplayName = claims.PlayerID, claims.DisplayName
	if req.GameVersion != "v0.107.1" && req.GameVersion != "v0.111.0" {
		writeError(w, 400, "unsupported game version")
		return
	}
	if req.Kind != domain.QueueCasual && req.Kind != domain.QueueRanked {
		writeError(w, 400, "entertainment rooms do not use matchmaking")
		return
	}
	if req.Pool == "" {
		if req.TeamSize == 1 {
			req.Pool = "solo"
		} else {
			req.Pool = "team"
		}
	}
	if len(req.TeamPlayerIDs) == 0 {
		req.TeamPlayerIDs = []string{claims.PlayerID}
	}
	if party := s.partyForPlayer(claims.PlayerID); party != nil {
		if party.LeaderPlayerID != claims.PlayerID {
			writeError(w, http.StatusForbidden, "only the party leader may queue")
			return
		}
		if party.TeamSize != req.TeamSize || party.Kind != string(req.Kind) {
			writeError(w, http.StatusConflict, "party settings do not match queue request")
			return
		}
		req.TeamPlayerIDs = append([]string{}, party.Members...)
		req.CharacterIDs = map[string]string{}
		for playerID, characterID := range party.CharacterIDs {
			req.CharacterIDs[playerID] = characterID
		}
	} else {
		req.TeamPlayerIDs = []string{claims.PlayerID}
		req.CharacterIDs = map[string]string{claims.PlayerID: req.CharacterID}
	}
	if req.TeamSize == 1 && !domain.IsPlayableCharacter(req.CharacterID) {
		writeError(w, 400, "a playable character must be selected before matchmaking")
		return
	}
	totalRating := 0
	req.VisibleTiers = nil
	for _, playerID := range req.TeamPlayerIDs {
		hidden, _, _, tier, err := s.Store.Rating(r.Context(), playerID, req.Pool)
		if err != nil {
			writeError(w, 500, err.Error())
			return
		}
		totalRating += hidden
		req.VisibleTiers = append(req.VisibleTiers, tier)
		if !domain.IsPlayableCharacter(req.CharacterIDs[playerID]) {
			req.CharacterIDs[playerID] = "Ironclad"
		}
	}
	req.HiddenRating = totalRating / len(req.TeamPlayerIDs)
	s.queueMu.Lock()
	s.queueByPlayer[claims.PlayerID] = req
	s.queueMu.Unlock()
	group, err := s.Queue.Join(r.Context(), req)
	if err != nil {
		writeError(w, 409, err.Error())
		return
	}
	if group == nil {
		writeJSON(w, http.StatusAccepted, map[string]any{"state": "searching"})
		return
	}
	assignment, err := s.Matches.Create(r.Context(), group.First, group.Second)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	s.removeQueuedPlayer(group.First.PlayerID)
	s.removeQueuedPlayer(group.Second.PlayerID)
	writeJSON(w, http.StatusOK, map[string]any{"state": "match_found", "assignment": assignment})
}
func (s *Server) cancelQueue(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	req, ok := s.removeQueuedPlayer(claims.PlayerID)
	if ok {
		_ = s.Queue.Cancel(r.Context(), req)
	}
	writeJSON(w, http.StatusOK, map[string]string{"state": "idle"})
}

func (s *Server) removeQueuedPlayer(playerID string) (domain.QueueRequest, bool) {
	s.queueMu.Lock()
	defer s.queueMu.Unlock()
	req, ok := s.queueByPlayer[playerID]
	if ok {
		delete(s.queueByPlayer, playerID)
	}
	return req, ok
}

func (s *Server) onDisconnect(playerID string) {
	s.Matches.Disconnected(playerID)
	if req, ok := s.removeQueuedPlayer(playerID); ok {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		if err := s.Queue.Cancel(ctx, req); err != nil {
			slog.Warn("failed to remove disconnected player from matchmaking queue", "player_id", playerID, "error", err)
		}
	}
}
func (s *Server) confirmMatch(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	var body struct {
		Accepted bool `json:"accepted"`
	}
	if !decode(w, r, &body) {
		return
	}
	if err := s.Matches.Confirm(r.Context(), claims.PlayerID, body.Accepted); err != nil {
		writeError(w, 409, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"accepted": body.Accepted})
}

func (s *Server) readyMatch(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	if err := s.Matches.Ready(r.Context(), claims.PlayerID); err != nil {
		writeError(w, http.StatusConflict, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ready": true})
}
func (s *Server) currentMatch(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	a, ok := s.Matches.AssignmentFor(claims.PlayerID)
	if !ok {
		writeError(w, 404, "no active match")
		return
	}
	writeJSON(w, http.StatusOK, a)
}

func (s *Server) profile(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	s.writeProfile(w, r, claims.PlayerID)
}
func (s *Server) playerProfile(w http.ResponseWriter, r *http.Request) {
	playerID := r.PathValue("player")
	if _, _, err := s.Store.PlayerProfile(r.Context(), playerID); err != nil {
		writeError(w, 404, "player not found")
		return
	}
	s.writeProfile(w, r, playerID)
}
func (s *Server) updateProfile(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	var body struct {
		DisplayName       string `json:"display_name"`
		FavoriteCharacter string `json:"favorite_character"`
	}
	if !decode(w, r, &body) {
		return
	}
	body.DisplayName = strings.TrimSpace(body.DisplayName)
	if len([]rune(body.DisplayName)) < 2 || len([]rune(body.DisplayName)) > 24 {
		writeError(w, http.StatusBadRequest, "display name must contain 2 to 24 characters")
		return
	}
	characters := map[string]string{"ironclad": "Ironclad", "silent": "Silent", "regent": "Regent", "necrobinder": "Necrobinder", "defect": "Defect"}
	favorite, ok := characters[strings.ToLower(strings.TrimSpace(body.FavoriteCharacter))]
	if !ok {
		writeError(w, http.StatusBadRequest, "unsupported favorite character")
		return
	}
	if err := s.Store.UpdatePlayerProfile(r.Context(), claims.PlayerID, body.DisplayName, favorite); err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	s.writeProfile(w, r, claims.PlayerID)
}
func (s *Server) writeProfile(w http.ResponseWriter, r *http.Request, playerID string) {
	displayName, favoriteCharacter, err := s.Store.PlayerProfile(r.Context(), playerID)
	if err != nil {
		writeError(w, 404, "player not found")
		return
	}
	solo, err := s.Store.RatingProfile(r.Context(), playerID, "solo")
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	team, err := s.Store.RatingProfile(r.Context(), playerID, "team")
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	bestTime, err := s.Store.BestTime(r.Context(), playerID)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	recent, err := s.Store.History(r.Context(), playerID, 20)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	totalGames := solo.Wins + solo.Losses + team.Wins + team.Losses
	winRate := 0.0
	if totalGames > 0 {
		winRate = float64(solo.Wins+team.Wins) / float64(totalGames)
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"id": playerID, "display_name": displayName, "favorite_character": favoriteCharacter,
		"solo":           rankMap(solo),
		"team":           rankMap(team),
		"best_time_ms":   bestTime,
		"win_rate":       winRate,
		"recent_matches": recent,
	})
}

func rankMap(r storage.RatingProfile) map[string]any {
	return map[string]any{"tier": r.Tier, "points": r.VisiblePoints, "games": r.GamesPlayed,
		"hidden_rating": r.HiddenRating, "wins": r.Wins, "losses": r.Losses,
		"division": r.Division, "leaderboard_rank": r.LeaderboardRank}
}

func (s *Server) history(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	limit := 20
	if value := r.URL.Query().Get("limit"); value != "" {
		if n, err := strconv.Atoi(value); err == nil && n > 0 && n <= 50 {
			limit = n
		}
	}
	rows, err := s.Store.History(r.Context(), claims.PlayerID, limit)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, rows)
}
func (s *Server) leaderboard(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	pool := r.URL.Query().Get("pool")
	if pool != "solo" && pool != "team" {
		pool = "solo"
	}
	if r.URL.Query().Get("historical") == "true" {
		writeJSON(w, http.StatusOK, []storage.LeaderboardRow{})
		return
	}
	rows, err := s.Store.Leaderboard(r.Context(), pool, 100, claims.PlayerID, r.URL.Query().Get("friends_only") == "true")
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, rows)
}

func (s *Server) friends(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	_ = s.Queue.TouchPresence(r.Context(), claims.PlayerID)
	rows, err := s.Store.Friends(r.Context(), claims.PlayerID)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	result := make([]map[string]any, 0, len(rows))
	for _, row := range rows {
		result = append(result, map[string]any{"player_id": row.PlayerID, "display_name": row.DisplayName,
			"relationship": row.Relationship, "tier": row.Tier, "online": s.Hub.IsConnected(row.PlayerID) || s.Queue.IsOnline(r.Context(), row.PlayerID), "in_race": s.Matches.IsInRace(row.PlayerID)})
	}
	writeJSON(w, http.StatusOK, result)
}

func (s *Server) searchPlayers(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	query := strings.TrimSpace(r.URL.Query().Get("q"))
	if len([]rune(query)) < 2 {
		writeJSON(w, http.StatusOK, []storage.SocialRow{})
		return
	}
	rows, err := s.Store.SearchPlayers(r.Context(), claims.PlayerID, query, 20)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, rows)
}

func (s *Server) friendTarget(w http.ResponseWriter, r *http.Request) (auth.Claims, string, bool) {
	claims, _ := auth.FromContext(r.Context())
	var body struct {
		PlayerID string `json:"player_id"`
	}
	if !decode(w, r, &body) {
		return claims, "", false
	}
	body.PlayerID = strings.TrimSpace(body.PlayerID)
	if body.PlayerID == "" {
		writeError(w, 400, "player_id is required")
		return claims, "", false
	}
	return claims, body.PlayerID, true
}
func (s *Server) requestFriend(w http.ResponseWriter, r *http.Request) {
	claims, target, ok := s.friendTarget(w, r)
	if !ok {
		return
	}
	if err := s.Store.RequestFriend(r.Context(), claims.PlayerID, target); err != nil {
		writeError(w, 409, err.Error())
		return
	}
	writeJSON(w, http.StatusCreated, map[string]string{"state": "pending"})
}
func (s *Server) acceptFriend(w http.ResponseWriter, r *http.Request) {
	claims, target, ok := s.friendTarget(w, r)
	if !ok {
		return
	}
	if err := s.Store.AcceptFriend(r.Context(), claims.PlayerID, target); err != nil {
		writeError(w, 409, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"state": "accepted"})
}
func (s *Server) declineFriend(w http.ResponseWriter, r *http.Request) {
	claims, target, ok := s.friendTarget(w, r)
	if !ok {
		return
	}
	if err := s.Store.DeclineFriend(r.Context(), claims.PlayerID, target); err != nil {
		writeError(w, 409, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"state": "declined"})
}
func (s *Server) removeFriend(w http.ResponseWriter, r *http.Request) {
	claims, target, ok := s.friendTarget(w, r)
	if !ok {
		return
	}
	if err := s.Store.RemoveFriend(r.Context(), claims.PlayerID, target); err != nil {
		writeError(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"state": "removed"})
}

func (s *Server) createRoom(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	var rules domain.Rules
	if !decode(w, r, &rules) {
		return
	}
	rules = domain.NormalizeEntertainmentRules(rules)
	if rules.TeamSize < 1 || rules.TeamSize > 4 || rules.Ascension < 0 || rules.Ascension > 10 ||
		rules.TimeLimitMS <= 0 || rules.TimeLimitMS > domain.MaxMatchMilliseconds ||
		rules.EventSLLimit < 0 || rules.EventSLLimit > 9 || rules.CombatSLLimit < 0 || rules.CombatSLLimit > 9 ||
		(rules.BestOf != 0 && rules.BestOf != 1 && rules.BestOf != 3) || len(rules.SeriesSeeds) > 3 {
		writeError(w, 400, "invalid entertainment rules")
		return
	}
	code, err := roomCode()
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	if err := s.Store.CreateRoom(r.Context(), code, claims.PlayerID, rules); err != nil {
		writeError(w, 500, err.Error())
		return
	}
	room, err := s.Store.RoomSnapshot(r.Context(), code)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusCreated, room)
}
func (s *Server) joinRoom(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	var body struct {
		Code string `json:"code"`
	}
	if !decode(w, r, &body) {
		return
	}
	body.Code = strings.ToUpper(strings.TrimSpace(body.Code))
	room, err := s.Store.JoinRoom(r.Context(), body.Code, claims.PlayerID)
	if err != nil {
		writeError(w, 404, "room not found")
		return
	}
	s.broadcastRoom(room)
	writeJSON(w, http.StatusOK, room)
}

func (s *Server) updateRoomRules(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	code := strings.ToUpper(strings.TrimSpace(r.PathValue("code")))
	var rules domain.Rules
	if !decode(w, r, &rules) {
		return
	}
	rules = domain.NormalizeEntertainmentRules(rules)
	if rules.TeamSize < 1 || rules.TeamSize > 4 || rules.Ascension < 0 || rules.Ascension > 10 ||
		rules.EventSLLimit < 0 || rules.EventSLLimit > 9 || rules.CombatSLLimit < 0 || rules.CombatSLLimit > 9 ||
		(rules.BestOf != 0 && rules.BestOf != 1 && rules.BestOf != 3) || len(rules.SeriesSeeds) > 3 {
		writeError(w, 400, "invalid entertainment rules")
		return
	}
	room, err := s.Store.UpdateRoomRules(r.Context(), code, claims.PlayerID, rules)
	if err != nil {
		writeError(w, http.StatusForbidden, err.Error())
		return
	}
	s.broadcastRoom(room)
	writeJSON(w, http.StatusOK, room)
}

func (s *Server) switchRoomTeam(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	room, err := s.Store.SwitchRoomTeam(r.Context(), strings.ToUpper(r.PathValue("code")), claims.PlayerID)
	if err != nil {
		writeError(w, http.StatusConflict, err.Error())
		return
	}
	s.broadcastRoom(room)
	writeJSON(w, http.StatusOK, room)
}

func (s *Server) updateRoomMember(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	var body struct {
		CharacterID string `json:"character_id"`
		Ready       bool   `json:"ready"`
	}
	if !decode(w, r, &body) {
		return
	}
	room, err := s.Store.SetRoomMember(r.Context(), strings.ToUpper(r.PathValue("code")), claims.PlayerID, body.CharacterID, body.Ready)
	if err != nil {
		writeError(w, http.StatusConflict, err.Error())
		return
	}
	s.broadcastRoom(room)
	writeJSON(w, http.StatusOK, room)
}

func (s *Server) startRoom(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	seedA, err := roomCode()
	if err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	seedB, err := roomCode()
	if err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	room, err := s.Store.StartRoom(r.Context(), strings.ToUpper(r.PathValue("code")), claims.PlayerID, seedA+seedB)
	if err != nil {
		writeError(w, http.StatusConflict, err.Error())
		return
	}
	s.broadcastRoom(room)
	hosts := map[int]string{}
	for _, member := range room.Members {
		if hosts[member.Team] == "" {
			hosts[member.Team] = member.PlayerID
		}
	}
	firstPlayers, secondPlayers := []string{}, []string{}
	characters := map[string]string{}
	for _, member := range room.Members {
		characters[member.PlayerID] = member.CharacterID
		if member.Team == 1 {
			firstPlayers = append(firstPlayers, member.PlayerID)
		} else {
			secondPlayers = append(secondPlayers, member.PlayerID)
		}
	}
	matchID := "fun-" + room.Code
	assignment := domain.Assignment{
		MatchID: matchID, GameID: matchID, GameVersion: r.Header.Get("X-Spire-Race-Game-Version"), Kind: domain.QueueEntertainment,
		TeamSize: room.Rules.TeamSize, FirstTeamID: "room-" + room.Code + "-1", SecondTeamID: "room-" + room.Code + "-2",
		FirstPlayerIDs: firstPlayers, SecondPlayerIDs: secondPlayers, Rules: domain.NormalizeEntertainmentRules(room.Rules), SessionNonce: room.Code,
		StartedAtMS: time.Now().UnixMilli(), CharacterIDs: characters,
		FirstSteamHostPlayerID: hosts[1], SecondSteamHostPlayerID: hosts[2],
	}
	assignment.LegendSeries = assignment.TeamSize == 1 && assignment.Rules.BestOf == 3
	if assignment.TeamSize == 1 && len(firstPlayers) > 0 {
		assignment.Rules.CharacterID = characters[firstPlayers[0]]
	}
	if assignment.GameVersion == "" {
		assignment.GameVersion = "unknown"
	}
	if err := s.Matches.CreateEntertainment(r.Context(), assignment); err != nil {
		writeError(w, http.StatusConflict, err.Error())
		return
	}
	if room.Rules.TeamSize == 1 {
		// A 1v1 BO3 remains in the Ban/Pick phase until the match service
		// broadcasts match_started. It must not enter the multiplayer Steam
		// lobby branch used by 2v2-4v4 rooms.
		if room.Rules.BestOf != 3 {
			ids := make([]string, 0, len(room.Members))
			for _, member := range room.Members {
				ids = append(ids, member.PlayerID)
			}
			s.Hub.Broadcast(ids, "entertainment_match_started", map[string]any{
				"room": room, "first_steam_host_player_id": hosts[1], "second_steam_host_player_id": hosts[2],
				"first_steam_lobby_id": "", "second_steam_lobby_id": "", "started_at_ms": assignment.StartedAtMS,
			})
		}
		writeJSON(w, http.StatusOK, room)
		return
	}
	s.roomLobbyMu.Lock()
	s.roomLobbies[room.Code] = map[int]string{}
	s.roomLobbyMu.Unlock()
	for team, hostID := range hosts {
		_ = s.Hub.Send(hostID, "entertainment_steam_lobby_required", map[string]any{"room": room, "team": team, "host_player_id": hostID})
	}
	writeJSON(w, http.StatusOK, room)
}

func (s *Server) registerEntertainmentLobby(ctx context.Context, playerID, code, lobbyID string) error {
	room, err := s.Store.RoomSnapshot(ctx, strings.ToUpper(code))
	if err != nil {
		return errors.New("entertainment room not found")
	}
	team := 0
	hosts := map[int]string{}
	for _, member := range room.Members {
		if hosts[member.Team] == "" {
			hosts[member.Team] = member.PlayerID
		}
		if member.PlayerID == playerID {
			team = member.Team
		}
	}
	if team == 0 || hosts[team] != playerID {
		return errors.New("only the assigned team host may report a Steam lobby")
	}
	s.roomLobbyMu.Lock()
	lobbies := s.roomLobbies[room.Code]
	if lobbies == nil {
		lobbies = map[int]string{}
		s.roomLobbies[room.Code] = lobbies
	}
	lobbies[team] = lobbyID
	first, second := lobbies[1], lobbies[2]
	s.roomLobbyMu.Unlock()
	if first == "" || second == "" {
		return nil
	}
	ids := make([]string, 0, len(room.Members))
	for _, member := range room.Members {
		ids = append(ids, member.PlayerID)
	}
	s.Hub.Broadcast(ids, "entertainment_match_started", map[string]any{
		"room": room, "first_steam_host_player_id": hosts[1], "second_steam_host_player_id": hosts[2],
		"first_steam_lobby_id": first, "second_steam_lobby_id": second,
		"started_at_ms": room.StartedAt.UnixMilli(),
	})
	return nil
}

func (s *Server) leaveRoom(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	code := strings.ToUpper(r.PathValue("code"))
	before, err := s.Store.RoomSnapshot(r.Context(), code)
	if err != nil {
		// Leaving is idempotent. The host may have closed the room before another
		// member receives the realtime closure notification.
		writeJSON(w, http.StatusOK, map[string]string{"state": "left"})
		return
	}
	if err = s.Store.LeaveRoom(r.Context(), code, claims.PlayerID); err != nil {
		writeError(w, 500, err.Error())
		return
	}
	if before.HostPlayerID == claims.PlayerID {
		ids := make([]string, 0, len(before.Members))
		for _, member := range before.Members {
			ids = append(ids, member.PlayerID)
		}
		s.Hub.Broadcast(ids, "entertainment_room_closed", map[string]string{"code": code})
	} else if room, snapshotErr := s.Store.RoomSnapshot(r.Context(), code); snapshotErr == nil {
		s.broadcastRoom(room)
	}
	writeJSON(w, http.StatusOK, map[string]string{"state": "left"})
}

func (s *Server) broadcastRoom(room storage.EntertainmentRoomSnapshot) {
	ids := make([]string, 0, len(room.Members))
	for _, member := range room.Members {
		ids = append(ids, member.PlayerID)
	}
	s.Hub.Broadcast(ids, "entertainment_room_updated", room)
}

func (s *Server) partyForPlayer(playerID string) *partyState {
	s.partyMu.Lock()
	defer s.partyMu.Unlock()
	partyID, ok := s.partyByPlayer[playerID]
	if !ok {
		return nil
	}
	party, ok := s.parties[partyID]
	if !ok {
		return nil
	}
	copy := *party
	copy.Members = append([]string(nil), party.Members...)
	copy.CharacterIDs = map[string]string{}
	for playerID, characterID := range party.CharacterIDs {
		copy.CharacterIDs[playerID] = characterID
	}
	return &copy
}

func (s *Server) openParty(playerID, kind string, teamSize int) {
	s.leaveParty(playerID)
	code, err := roomCode()
	if err != nil {
		return
	}
	party := &partyState{ID: "P-" + code, LeaderPlayerID: playerID, Kind: kind, TeamSize: teamSize, Members: []string{playerID}, CharacterIDs: map[string]string{playerID: "Ironclad"}}
	s.partyMu.Lock()
	s.parties[party.ID] = party
	s.partyByPlayer[playerID] = party.ID
	s.partyMu.Unlock()
	s.broadcastParty(party)
}

func (s *Server) joinParty(inviterID, playerID string) {
	if inviterID == playerID {
		return
	}
	s.leaveParty(playerID)
	s.partyMu.Lock()
	partyID, ok := s.partyByPlayer[inviterID]
	party := s.parties[partyID]
	if !ok || party == nil || len(party.Members) >= party.TeamSize {
		s.partyMu.Unlock()
		return
	}
	for _, memberID := range party.Members {
		if memberID == playerID {
			s.partyMu.Unlock()
			return
		}
	}
	party.Members = append(party.Members, playerID)
	party.CharacterIDs[playerID] = "Ironclad"
	s.partyByPlayer[playerID] = party.ID
	copy := *party
	copy.Members = append([]string(nil), party.Members...)
	s.partyMu.Unlock()
	s.broadcastParty(&copy)
}

func (s *Server) leaveParty(playerID string) {
	s.partyMu.Lock()
	partyID, ok := s.partyByPlayer[playerID]
	if !ok {
		s.partyMu.Unlock()
		return
	}
	party := s.parties[partyID]
	delete(s.partyByPlayer, playerID)
	if party == nil {
		s.partyMu.Unlock()
		return
	}
	previousMembers := append([]string(nil), party.Members...)
	if party.LeaderPlayerID == playerID {
		delete(s.parties, partyID)
		for _, memberID := range previousMembers {
			delete(s.partyByPlayer, memberID)
		}
		s.partyMu.Unlock()
		s.Hub.Broadcast(previousMembers, "party_closed", map[string]string{"id": partyID})
		return
	}
	members := party.Members[:0]
	for _, memberID := range party.Members {
		if memberID != playerID {
			members = append(members, memberID)
		}
	}
	party.Members = members
	delete(party.CharacterIDs, playerID)
	copy := *party
	copy.Members = append([]string(nil), party.Members...)
	s.partyMu.Unlock()
	_ = s.Hub.Send(playerID, "party_closed", map[string]string{"id": partyID})
	s.broadcastParty(&copy)
}

func (s *Server) broadcastParty(party *partyState) {
	members := make([]map[string]string, 0, len(party.Members))
	for _, playerID := range party.Members {
		displayName, err := s.Store.Player(context.Background(), playerID)
		if err != nil || displayName == "" {
			displayName = playerID
		}
		members = append(members, map[string]string{"player_id": playerID, "display_name": displayName, "character_id": party.CharacterIDs[playerID]})
	}
	s.Hub.Broadcast(party.Members, "party_updated", map[string]any{
		"id": party.ID, "leader_player_id": party.LeaderPlayerID, "kind": party.Kind, "team_size": party.TeamSize, "members": members,
	})
}

func (s *Server) manifest(w http.ResponseWriter, r *http.Request) {
	m, err := s.Integrity.Manifest(r.PathValue("version"))
	if err != nil {
		writeError(w, 404, "unsupported game version")
		return
	}
	writeJSON(w, http.StatusOK, m)
}
func (s *Server) verifyIntegrity(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	var a integrity.Attestation
	if !decode(w, r, &a) {
		return
	}
	v, err := s.Integrity.Verify(r.Context(), a)
	if err != nil {
		writeError(w, 500, err.Error())
		return
	}
	if !v.Accepted {
		_ = s.Matches.IntegrityFailure(r.Context(), claims.PlayerID)
		writeJSON(w, http.StatusForbidden, v)
		return
	}
	writeJSON(w, http.StatusOK, v)
}

func (s *Server) websocket(w http.ResponseWriter, r *http.Request) {
	claims, _ := auth.FromContext(r.Context())
	upgrader := websocket.Upgrader{CheckOrigin: func(*http.Request) bool { return true }}
	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	s.Matches.Reconnected(claims.PlayerID)
	s.Hub.Attach(claims.PlayerID, conn)
	defer func() { s.Hub.Detach(claims.PlayerID, conn); _ = conn.Close() }()
	for {
		_ = conn.SetReadDeadline(time.Now().Add(15 * time.Second))
		_, raw, err := conn.ReadMessage()
		if err != nil {
			return
		}
		var header struct {
			Type string          `json:"type"`
			Data json.RawMessage `json:"data"`
		}
		if json.Unmarshal(raw, &header) != nil {
			continue
		}
		switch header.Type {
		case "heartbeat":
			_ = s.Queue.TouchPresence(r.Context(), claims.PlayerID)
			_ = s.Hub.Send(claims.PlayerID, "clock", map[string]int64{"server_unix_ms": time.Now().UnixMilli()})
		case "party_open":
			var body struct {
				Kind     string `json:"kind"`
				TeamSize int    `json:"team_size"`
			}
			if json.Unmarshal(header.Data, &body) == nil && (body.Kind == "casual" || body.Kind == "ranked") && body.TeamSize >= 1 && body.TeamSize <= 4 {
				s.openParty(claims.PlayerID, body.Kind, body.TeamSize)
			}
		case "party_leave":
			s.leaveParty(claims.PlayerID)
		case "party_character":
			var body struct {
				CharacterID string `json:"character_id"`
			}
			if json.Unmarshal(header.Data, &body) == nil && domain.IsPlayableCharacter(body.CharacterID) {
				s.partyMu.Lock()
				partyID, ok := s.partyByPlayer[claims.PlayerID]
				party := s.parties[partyID]
				if ok && party != nil {
					party.CharacterIDs[claims.PlayerID] = body.CharacterID
				}
				var copy *partyState
				if party != nil {
					value := *party
					value.Members = append([]string{}, party.Members...)
					value.CharacterIDs = map[string]string{}
					for k, v := range party.CharacterIDs {
						value.CharacterIDs[k] = v
					}
					copy = &value
				}
				s.partyMu.Unlock()
				if copy != nil {
					s.broadcastParty(copy)
				}
			}
		case "steam_lobby_ready":
			var body struct {
				LobbyID string `json:"lobby_id"`
			}
			if json.Unmarshal(header.Data, &body) == nil {
				if err := s.Matches.RegisterSteamLobby(r.Context(), claims.PlayerID, body.LobbyID); err != nil {
					_ = s.Hub.Send(claims.PlayerID, "error", err.Error())
				}
			}
		case "entertainment_steam_lobby_ready":
			var body struct {
				Code    string `json:"code"`
				LobbyID string `json:"lobby_id"`
			}
			if json.Unmarshal(header.Data, &body) == nil {
				if err := s.registerEntertainmentLobby(r.Context(), claims.PlayerID, body.Code, body.LobbyID); err != nil {
					_ = s.Hub.Send(claims.PlayerID, "error", err.Error())
				}
			}
		case "friend_invite":
			var body struct {
				PlayerID string `json:"player_id"`
				RoomCode string `json:"room_code"`
			}
			if json.Unmarshal(header.Data, &body) == nil && s.Store.AreFriends(r.Context(), claims.PlayerID, body.PlayerID) {
				displayName, _ := s.Store.Player(r.Context(), claims.PlayerID)
				roomCode := ""
				if room, roomErr := s.Store.ActiveRoomForPlayer(r.Context(), claims.PlayerID); roomErr == nil {
					roomCode = room.Code
				}
				invite := map[string]any{"player_id": claims.PlayerID, "display_name": displayName, "room_code": roomCode}
				if roomCode == "" {
					if party := s.partyForPlayer(claims.PlayerID); party != nil {
						invite["party_id"] = party.ID
						invite["kind"] = party.Kind
						invite["team_size"] = party.TeamSize
					}
				}
				_ = s.Hub.Send(body.PlayerID, "friend_invite", invite)
			}
		case "friend_invite_response":
			var body struct {
				PlayerID string `json:"player_id"`
				Accepted bool   `json:"accepted"`
			}
			if json.Unmarshal(header.Data, &body) == nil && s.Store.AreFriends(r.Context(), claims.PlayerID, body.PlayerID) {
				joinedCode := ""
				if body.Accepted {
					if inviterRoom, roomErr := s.Store.ActiveRoomForPlayer(r.Context(), body.PlayerID); roomErr == nil {
						if joinedRoom, joinErr := s.Store.JoinRoom(r.Context(), inviterRoom.Code, claims.PlayerID); joinErr == nil {
							joinedCode = joinedRoom.Code
							s.broadcastRoom(joinedRoom)
						}
					} else {
						s.joinParty(body.PlayerID, claims.PlayerID)
					}
				}
				displayName, _ := s.Store.Player(r.Context(), claims.PlayerID)
				_ = s.Hub.Send(body.PlayerID, "friend_invite_response", map[string]any{
					"player_id": claims.PlayerID, "display_name": displayName, "accepted": body.Accepted, "room_code": joinedCode,
				})
			}
		case "progress":
			var body struct {
				IdempotencyKey string          `json:"idempotency_key"`
				Progress       domain.Progress `json:"progress"`
			}
			if json.Unmarshal(header.Data, &body) == nil {
				if err := s.Matches.Progress(r.Context(), claims.PlayerID, body.IdempotencyKey, body.Progress); err != nil {
					_ = s.Hub.Send(claims.PlayerID, "error", err.Error())
				}
			}
		case "surrender_vote":
			var body struct {
				Accept bool `json:"accept"`
			}
			if json.Unmarshal(header.Data, &body) == nil {
				_ = s.Matches.Surrender(r.Context(), claims.PlayerID, body.Accept)
			}
		case "save_quit":
			var body struct {
				Combat         bool `json:"combat"`
				ConfirmForfeit bool `json:"confirm_forfeit"`
			}
			if json.Unmarshal(header.Data, &body) == nil {
				p, err := s.Matches.SaveAndQuit(claims.PlayerID, body.Combat, body.ConfirmForfeit)
				if err != nil {
					_ = s.Hub.Send(claims.PlayerID, "save_quit_rejected", err.Error())
				} else {
					_ = s.Hub.Send(claims.PlayerID, "save_quit_accepted", p)
				}
			}
		case "resume":
			var body struct {
				IdempotencyKey string `json:"idempotency_key"`
			}
			if json.Unmarshal(header.Data, &body) == nil {
				p, err := s.Matches.Resume(claims.PlayerID, body.IdempotencyKey)
				if err != nil {
					_ = s.Hub.Send(claims.PlayerID, "resume_rejected", err.Error())
				} else {
					_ = s.Hub.Send(claims.PlayerID, "resume_accepted", p)
				}
			}
		case "death_choice":
			var body struct {
				Restart bool `json:"restart"`
			}
			if json.Unmarshal(header.Data, &body) == nil {
				p, err := s.Matches.DeathChoice(claims.PlayerID, body.Restart)
				if err != nil {
					_ = s.Hub.Send(claims.PlayerID, "error", err.Error())
				} else {
					_ = s.Hub.Send(claims.PlayerID, "death_choice_accepted", p)
				}
			}
		case "legend_bans":
			var body struct {
				BanOne string `json:"ban_one"`
				BanTwo string `json:"ban_two"`
			}
			if json.Unmarshal(header.Data, &body) == nil {
				if err := s.Matches.SubmitLegendBans(r.Context(), claims.PlayerID, body.BanOne, body.BanTwo); err != nil {
					_ = s.Hub.Send(claims.PlayerID, "error", err.Error())
				}
			}
		case "legend_pick":
			var body struct {
				CharacterID string `json:"character_id"`
			}
			if json.Unmarshal(header.Data, &body) == nil {
				if err := s.Matches.SelectLegendCharacter(r.Context(), claims.PlayerID, body.CharacterID); err != nil {
					_ = s.Hub.Send(claims.PlayerID, "error", err.Error())
				}
			}
		}
	}
}

func decode(w http.ResponseWriter, r *http.Request, target any) bool {
	r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
	defer r.Body.Close()
	d := json.NewDecoder(r.Body)
	d.DisallowUnknownFields()
	if err := d.Decode(target); err != nil {
		writeError(w, 400, err.Error())
		return false
	}
	return true
}
func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	if err := json.NewEncoder(w).Encode(value); err != nil {
		slog.Error("write response", "error", err)
	}
}
func writeError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]string{"error": message})
}
func writeErrorCode(w http.ResponseWriter, status int, code, message string) {
	writeJSON(w, status, map[string]string{"code": code, "error": message})
}
func roomCode() (string, error) {
	const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
	b := make([]byte, 6)
	for i := range b {
		n, err := rand.Int(rand.Reader, big.NewInt(int64(len(alphabet))))
		if err != nil {
			return "", err
		}
		b[i] = alphabet[n.Int64()]
	}
	return string(b), nil
}

var _ = errors.New
