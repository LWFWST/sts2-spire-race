package match

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/mcc/sts2-spire-race/server/internal/domain"
)

type Repository interface {
	SaveMatch(context.Context, domain.Assignment) error
	StartMatch(context.Context, string, time.Time) error
	SaveSettlement(context.Context, domain.Settlement) error
	ApplyRatings(context.Context, domain.Assignment, string) (map[string]int, error)
}

type Notifier interface{ Broadcast([]string, string, any) }

type Service struct {
	mu               sync.Mutex
	repo             Repository
	notifier         Notifier
	matches          map[string]*state
	playerMatch      map[string]string
	disconnectTimers map[string]*time.Timer
}

type state struct {
	Assignment     domain.Assignment
	Confirmed      map[string]bool
	Ready          map[string]bool
	Progress       map[string]domain.Progress
	Idempotency    map[string]bool
	SurrenderVotes map[string]map[string]bool
	PendingSL      map[string]bool
	ForcedReasons  map[string]domain.FinishReason
	Draft          *domain.LegendDraft
	Bans           map[string][2]string
	SeriesGames    []domain.LegendGame
	Settled        bool
	SteamLobbyIDs  map[string]string
	PausedAtMS     map[string]int64
	PausedTotalMS  map[string]int64
}

func New(repo Repository, notifier Notifier) *Service {
	return &Service{repo: repo, notifier: notifier, matches: map[string]*state{}, playerMatch: map[string]string{}, disconnectTimers: map[string]*time.Timer{}}
}

func (s *Service) SetNotifier(notifier Notifier) { s.mu.Lock(); s.notifier = notifier; s.mu.Unlock() }

func (s *Service) Create(ctx context.Context, first, second domain.QueueRequest) (domain.Assignment, error) {
	seed, err := randomHex(8)
	if err != nil {
		return domain.Assignment{}, err
	}
	nonce, err := randomHex(24)
	if err != nil {
		return domain.Assignment{}, err
	}
	rules, err := domain.DefaultRules(domain.QueueRequest{Kind: first.Kind, TeamSize: first.TeamSize,
		VisibleTiers: append(append([]string{}, first.VisibleTiers...), second.VisibleTiers...), CharacterID: first.CharacterID}, seed)
	if err != nil {
		return domain.Assignment{}, err
	}
	matchID, _ := randomHex(12)
	gameID, _ := randomHex(12)
	a := domain.Assignment{MatchID: matchID, GameID: gameID, GameVersion: first.GameVersion, Kind: first.Kind, TeamSize: first.TeamSize,
		FirstTeamID: "team-" + first.PlayerID, SecondTeamID: "team-" + second.PlayerID, FirstPlayerIDs: first.TeamPlayerIDs, SecondPlayerIDs: second.TeamPlayerIDs,
		Rules: rules, SessionNonce: nonce, LegendSeries: first.Kind == domain.QueueRanked && first.TeamSize == 1 && domain.IsLegend(first.VisibleTiers) && domain.IsLegend(second.VisibleTiers)}
	characters := map[string]string{}
	for playerID, characterID := range first.CharacterIDs {
		characters[playerID] = characterID
	}
	for playerID, characterID := range second.CharacterIDs {
		characters[playerID] = characterID
	}
	a.CharacterIDs = characters
	if len(a.FirstPlayerIDs) > 0 {
		a.FirstSteamHostPlayerID = a.FirstPlayerIDs[0]
	}
	if len(a.SecondPlayerIDs) > 0 {
		a.SecondSteamHostPlayerID = a.SecondPlayerIDs[0]
	}
	st := &state{Assignment: a, Confirmed: map[string]bool{}, Ready: map[string]bool{}, Progress: map[string]domain.Progress{}, Idempotency: map[string]bool{}, SurrenderVotes: map[string]map[string]bool{}, PendingSL: map[string]bool{}, ForcedReasons: map[string]domain.FinishReason{}, Bans: map[string][2]string{}, SteamLobbyIDs: map[string]string{}, PausedAtMS: map[string]int64{}, PausedTotalMS: map[string]int64{}}
	s.mu.Lock()
	s.matches[matchID] = st
	for _, p := range append(append([]string{}, a.FirstPlayerIDs...), a.SecondPlayerIDs...) {
		s.playerMatch[p] = matchID
	}
	notifier := s.notifier
	s.mu.Unlock()
	if err := s.repo.SaveMatch(ctx, a); err != nil {
		return domain.Assignment{}, err
	}
	if notifier != nil {
		notifier.Broadcast(allPlayers(a), "match_found", a)
	}
	time.AfterFunc(domain.ReadyCheckWindow, func() { s.expireUnstarted(matchID) })
	return a, nil
}

func (s *Service) CreateEntertainment(ctx context.Context, a domain.Assignment) error {
	if a.Kind != domain.QueueEntertainment || len(a.FirstPlayerIDs) == 0 || len(a.SecondPlayerIDs) == 0 {
		return errors.New("invalid entertainment assignment")
	}
	a.Rules = domain.NormalizeEntertainmentRules(a.Rules)
	if a.Rules.BestOf == 0 {
		a.Rules.BestOf = 1
	}
	a.LegendSeries = a.TeamSize == 1 && a.Rules.BestOf == 3
	if a.LegendSeries {
		// BO3 1v1 starts in the Ban phase; the race clock begins only after
		// both bans are locked and the first shared character is selected.
		a.StartedAtMS = 0
	} else if a.StartedAtMS == 0 {
		a.StartedAtMS = time.Now().UnixMilli()
	}
	if err := s.repo.SaveMatch(ctx, a); err != nil {
		return err
	}
	if !a.LegendSeries {
		if err := s.repo.StartMatch(ctx, a.MatchID, time.UnixMilli(a.StartedAtMS)); err != nil {
			return err
		}
	}
	st := &state{Assignment: a, Confirmed: map[string]bool{}, Ready: map[string]bool{}, Progress: map[string]domain.Progress{},
		Idempotency: map[string]bool{}, SurrenderVotes: map[string]map[string]bool{}, PendingSL: map[string]bool{},
		ForcedReasons: map[string]domain.FinishReason{}, Bans: map[string][2]string{}, SteamLobbyIDs: map[string]string{},
		PausedAtMS: map[string]int64{}, PausedTotalMS: map[string]int64{}}
	s.mu.Lock()
	s.matches[a.MatchID] = st
	for _, playerID := range allPlayers(a) {
		s.playerMatch[playerID] = a.MatchID
	}
	notifier := s.notifier
	s.mu.Unlock()
	if a.LegendSeries {
		if notifier != nil {
			notifier.Broadcast(allPlayers(a), "legend_ban_required", map[string]any{"available_characters": domain.Characters, "draft": domain.LegendDraft{GameNumber: 1}, "assignment": a})
		}
		time.AfterFunc(domain.LegendPickWindow, func() { s.autoLegendBans(a.MatchID) })
	} else {
		time.AfterFunc(time.Duration(a.Rules.TimeLimitMS)*time.Millisecond, func() { s.timeout(a.MatchID, a.GameID) })
	}
	return nil
}

func (s *Service) Confirm(ctx context.Context, playerID string, accepted bool) error {
	s.mu.Lock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	if !accepted {
		st.Assignment.LegendSeries = false
		st.Progress[teamID] = domain.Progress{MatchID: st.Assignment.MatchID, GameID: st.Assignment.GameID, TeamID: teamID, Outcome: domain.OutcomeForfeited}
	}
	st.Confirmed[teamID] = accepted
	a := st.Assignment
	notifier := s.notifier
	s.mu.Unlock()
	if !accepted {
		return s.trySettle(ctx, a.MatchID)
	}
	if notifier != nil {
		notifier.Broadcast(allPlayers(a), "match_confirmed", map[string]any{"team_id": teamID})
	}
	return nil
}

func (s *Service) Ready(ctx context.Context, playerID string) error {
	s.mu.Lock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	if !st.Confirmed[teamID] {
		s.mu.Unlock()
		return errors.New("team must confirm before readying")
	}
	st.Ready[teamID] = true
	start := st.Confirmed[st.Assignment.FirstTeamID] && st.Confirmed[st.Assignment.SecondTeamID] && st.Ready[st.Assignment.FirstTeamID] && st.Ready[st.Assignment.SecondTeamID]
	if start && !st.Assignment.LegendSeries {
		st.Assignment.StartedAtMS = time.Now().UnixMilli()
	}
	a := st.Assignment
	notifier := s.notifier
	s.mu.Unlock()
	if notifier != nil {
		notifier.Broadcast(allPlayers(a), "team_ready", map[string]any{"team_id": teamID})
	}
	if !start {
		return nil
	}
	if a.LegendSeries {
		if notifier != nil {
			notifier.Broadcast(allPlayers(a), "legend_ban_required", map[string]any{"available_characters": domain.Characters, "draft": domain.LegendDraft{GameNumber: 1}, "assignment": a})
		}
		time.AfterFunc(domain.LegendPickWindow, func() { s.autoLegendBans(a.MatchID) })
		return nil
	}
	if a.TeamSize > 1 {
		if notifier != nil {
			notifier.Broadcast([]string{a.FirstSteamHostPlayerID, a.SecondSteamHostPlayerID}, "steam_lobby_required", a)
		}
		return nil
	}
	return s.startGame(ctx, a)
}

func (s *Service) RegisterSteamLobby(ctx context.Context, playerID, lobbyID string) error {
	if lobbyID == "" {
		return errors.New("steam lobby id is required")
	}
	s.mu.Lock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	host := st.Assignment.FirstSteamHostPlayerID
	if teamID == st.Assignment.SecondTeamID {
		host = st.Assignment.SecondSteamHostPlayerID
	}
	if playerID != host {
		s.mu.Unlock()
		return errors.New("only the assigned Steam host may report a lobby")
	}
	st.SteamLobbyIDs[teamID] = lobbyID
	if teamID == st.Assignment.FirstTeamID {
		st.Assignment.FirstSteamLobbyID = lobbyID
	} else {
		st.Assignment.SecondSteamLobbyID = lobbyID
	}
	ready := st.Assignment.FirstSteamLobbyID != "" && st.Assignment.SecondSteamLobbyID != ""
	if ready {
		st.Assignment.StartedAtMS = time.Now().UnixMilli()
	}
	a := st.Assignment
	notifier := s.notifier
	s.mu.Unlock()
	if notifier != nil {
		notifier.Broadcast(allPlayers(a), "steam_lobby_reported", map[string]string{"team_id": teamID})
	}
	if ready {
		return s.startGame(ctx, a)
	}
	return nil
}

func (s *Service) startGame(ctx context.Context, a domain.Assignment) error {
	if err := s.repo.StartMatch(ctx, a.MatchID, time.UnixMilli(a.StartedAtMS)); err != nil {
		return err
	}
	s.mu.Lock()
	notifier := s.notifier
	s.mu.Unlock()
	if notifier != nil {
		notifier.Broadcast(allPlayers(a), "match_started", a)
	}
	gameID := a.GameID
	time.AfterFunc(time.Duration(a.Rules.TimeLimitMS)*time.Millisecond, func() { s.timeout(a.MatchID, gameID) })
	return nil
}

func (s *Service) SubmitLegendBans(ctx context.Context, playerID, banOne, banTwo string) error {
	if !validCharacter(banOne) || !validCharacter(banTwo) {
		return errors.New("two playable characters are required")
	}
	s.mu.Lock()
	st, _, err := s.stateForPlayer(playerID)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	if !st.Assignment.LegendSeries {
		s.mu.Unlock()
		return errors.New("match is not a Legend series")
	}
	if st.Draft != nil {
		s.mu.Unlock()
		return errors.New("Legend bans are already locked")
	}
	st.Bans[playerID] = [2]string{banOne, banTwo}
	p1, p2 := st.Assignment.FirstPlayerIDs[0], st.Assignment.SecondPlayerIDs[0]
	if len(st.Bans) < 2 {
		s.mu.Unlock()
		return nil
	}
	d := domain.LegendDraft{PlayerOneBanOne: st.Bans[p1][0], PlayerOneBanTwo: st.Bans[p1][1], PlayerTwoBanOne: st.Bans[p2][0], PlayerTwoBanTwo: st.Bans[p2][1], GameNumber: 1}
	character, err := domain.SelectLegendCharacter(d, "")
	if err != nil {
		s.mu.Unlock()
		return err
	}
	d.Selected = character
	st.Draft = &d
	st.Assignment.Rules.CharacterID = character
	st.Assignment.StartedAtMS = time.Now().UnixMilli()
	a := st.Assignment
	notifier := s.notifier
	s.mu.Unlock()
	if notifier != nil {
		notifier.Broadcast(allPlayers(a), "legend_draft_locked", d)
	}
	return s.startGame(ctx, a)
}

func (s *Service) autoLegendBans(matchID string) {
	s.mu.Lock()
	st := s.matches[matchID]
	if st == nil || st.Settled || !st.Assignment.LegendSeries || st.Draft != nil {
		s.mu.Unlock()
		return
	}
	players := []string{st.Assignment.FirstPlayerIDs[0], st.Assignment.SecondPlayerIDs[0]}
	missing := make([]string, 0, 2)
	for _, playerID := range players {
		if _, ok := st.Bans[playerID]; !ok {
			missing = append(missing, playerID)
		}
	}
	s.mu.Unlock()
	for _, playerID := range missing {
		first, second := randomLegendBanPair()
		_ = s.SubmitLegendBans(context.Background(), playerID, first, second)
	}
}

func randomLegendBanPair() (string, string) {
	value := make([]byte, 2)
	if _, err := rand.Read(value); err != nil {
		return domain.Characters[0], domain.Characters[1]
	}
	firstIndex := int(value[0]) % len(domain.Characters)
	secondIndex := int(value[1]) % (len(domain.Characters) - 1)
	if secondIndex >= firstIndex {
		secondIndex++
	}
	return domain.Characters[firstIndex], domain.Characters[secondIndex]
}

func (s *Service) SelectLegendCharacter(ctx context.Context, playerID, character string) error {
	s.mu.Lock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	if st.Draft == nil || st.Draft.SelectingTeam != teamID {
		s.mu.Unlock()
		return errors.New("this team is not selecting")
	}
	selected, err := domain.SelectLegendCharacter(*st.Draft, character)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	st.Draft.Selected = selected
	st.Assignment.Rules.CharacterID = selected
	st.Assignment.StartedAtMS = time.Now().UnixMilli()
	a := st.Assignment
	draft := *st.Draft
	notifier := s.notifier
	s.mu.Unlock()
	if notifier != nil {
		notifier.Broadcast(allPlayers(a), "legend_draft_locked", draft)
	}
	return s.startGame(ctx, a)
}

func (s *Service) autoLegendPick(matchID, gameID string) {
	s.mu.Lock()
	st := s.matches[matchID]
	if st == nil || st.Settled || st.Assignment.GameID != gameID || st.Draft == nil ||
		st.Draft.SelectingTeam == "" || st.Draft.Selected != "" || st.Assignment.StartedAtMS != 0 {
		s.mu.Unlock()
		return
	}
	selector := st.Assignment.FirstPlayerIDs[0]
	if st.Draft.SelectingTeam == st.Assignment.SecondTeamID {
		selector = st.Assignment.SecondPlayerIDs[0]
	}
	s.mu.Unlock()
	_ = s.SelectLegendCharacter(context.Background(), selector, "")
}

func (s *Service) Progress(ctx context.Context, playerID, idempotency string, p domain.Progress) error {
	s.mu.Lock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	if st.Settled {
		s.mu.Unlock()
		return errors.New("match is already settled")
	}
	if st.Idempotency[idempotency] {
		s.mu.Unlock()
		return nil
	}
	if p.MatchID != st.Assignment.MatchID || p.GameID != st.Assignment.GameID || p.TeamID != teamID {
		s.mu.Unlock()
		return errors.New("progress identity mismatch")
	}
	old := st.Progress[teamID]
	if p.Sequence <= old.Sequence {
		s.mu.Unlock()
		return errors.New("progress sequence is not monotonic")
	}
	if p.Floor < old.Floor {
		p.Floor = old.Floor
		p.FloorEnteredAtMS = old.FloorEnteredAtMS
	}
	if p.FloorEnteredAtMS < 0 || p.FloorEnteredAtMS > domain.MaxMatchMilliseconds+5000 {
		s.mu.Unlock()
		return errors.New("invalid progress time")
	}
	st.Idempotency[idempotency] = true
	st.Progress[teamID] = p
	notifier := s.notifier
	players := allPlayers(st.Assignment)
	opponentTeamID := st.Assignment.FirstTeamID
	if teamID == opponentTeamID {
		opponentTeamID = st.Assignment.SecondTeamID
	}
	opponent, opponentOK := st.Progress[opponentTeamID]
	finishPending := p.FinalBossDefeated && p.Outcome == domain.OutcomeFinished &&
		(!opponentOK || opponent.Outcome == "" || opponent.Outcome == domain.OutcomeActive)
	scorePending := p.Outcome == domain.OutcomeScoreLocked &&
		(!opponentOK || opponent.Outcome == "" || opponent.Outcome == domain.OutcomeActive)
	team := append([]string{}, teamPlayers(st.Assignment, teamID)...)
	s.mu.Unlock()
	if notifier != nil {
		notifier.Broadcast(players, "progress", p)
		if finishPending || scorePending {
			notifier.Broadcast(team, "finish_pending", p)
		}
	}
	return s.trySettle(ctx, p.MatchID)
}

func (s *Service) SaveAndQuit(playerID string, combat, confirmForfeit bool) (domain.Progress, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		return domain.Progress{}, err
	}
	if len(st.Assignment.FirstPlayerIDs) > 1 && first(st.Assignment.FirstPlayerIDs, playerID) == false && first(st.Assignment.SecondPlayerIDs, playerID) == false {
		return domain.Progress{}, errors.New("only the room host can save and quit")
	}
	p := st.Progress[teamID]
	allowed := domain.HasSL(p, st.Assignment.Rules, combat)
	if !allowed {
		if confirmForfeit {
			p.Outcome = domain.OutcomeSurrendered
			st.Progress[teamID] = p
			st.ForcedReasons[teamID] = domain.ReasonSurrender
			go s.trySettle(context.Background(), st.Assignment.MatchID)
			return p, nil
		}
		return p, errors.New("SL allowance exhausted")
	}
	st.PendingSL[teamID] = combat
	if st.Assignment.Rules.SLTimerMode == "pause_on_save" && st.PausedAtMS[teamID] == 0 {
		st.PausedAtMS[teamID] = time.Now().UnixMilli()
	}
	return p, nil
}

func (s *Service) Resume(playerID, idempotency string) (domain.Progress, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		return domain.Progress{}, err
	}
	if st.Idempotency[idempotency] {
		return st.Progress[teamID], nil
	}
	combat, pending := st.PendingSL[teamID]
	if !pending {
		return domain.Progress{}, errors.New("there is no saved race session to resume")
	}
	p := st.Progress[teamID]
	if pausedAt := st.PausedAtMS[teamID]; pausedAt > 0 {
		st.PausedTotalMS[teamID] += max64(0, time.Now().UnixMilli()-pausedAt)
		delete(st.PausedAtMS, teamID)
	}
	if combat {
		p.CombatSLUsed++
	} else {
		p.EventSLUsed++
	}
	p.Sequence++
	st.Progress[teamID] = p
	st.Idempotency[idempotency] = true
	delete(st.PendingSL, teamID)
	return p, nil
}

func (s *Service) DeathChoice(playerID string, restart bool) (domain.Progress, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		return domain.Progress{}, err
	}
	p := st.Progress[teamID]
	if restart {
		p.Outcome = domain.OutcomeActive
		p.RestartCount++
	} else {
		p.Outcome = domain.OutcomeScoreLocked
	}
	st.Progress[teamID] = p
	return p, nil
}

func (s *Service) Surrender(ctx context.Context, playerID string, accept bool) error {
	s.mu.Lock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	players := teamPlayers(st.Assignment, teamID)
	if st.SurrenderVotes[teamID] == nil {
		st.SurrenderVotes[teamID] = map[string]bool{}
	}
	st.SurrenderVotes[teamID][playerID] = accept
	votes := 0
	for _, v := range st.SurrenderVotes[teamID] {
		if v {
			votes++
		}
	}
	passed := len(players) == 1 && accept || votes >= len(players)/2+1
	if passed {
		p := st.Progress[teamID]
		p.MatchID = st.Assignment.MatchID
		p.GameID = st.Assignment.GameID
		p.TeamID = teamID
		p.Sequence++
		p.Outcome = domain.OutcomeSurrendered
		st.Progress[teamID] = p
		st.ForcedReasons[teamID] = domain.ReasonSurrender
	}
	s.mu.Unlock()
	if passed {
		return s.trySettle(ctx, st.Assignment.MatchID)
	}
	return nil
}

func (s *Service) Disconnected(playerID string) {
	s.mu.Lock()
	if old := s.disconnectTimers[playerID]; old != nil {
		old.Stop()
	}
	s.disconnectTimers[playerID] = time.AfterFunc(domain.DisconnectGrace, func() { s.forfeitDisconnected(playerID) })
	s.mu.Unlock()
}
func (s *Service) Reconnected(playerID string) {
	s.mu.Lock()
	if timer := s.disconnectTimers[playerID]; timer != nil {
		timer.Stop()
		delete(s.disconnectTimers, playerID)
	}
	s.mu.Unlock()
}

func (s *Service) forfeitDisconnected(playerID string) {
	s.mu.Lock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		s.mu.Unlock()
		return
	}
	if st.Settled {
		s.mu.Unlock()
		return
	}
	if st.Assignment.StartedAtMS == 0 {
		st.Settled = true
		players := allPlayers(st.Assignment)
		matchID := st.Assignment.MatchID
		for _, player := range players {
			delete(s.playerMatch, player)
		}
		notifier := s.notifier
		s.mu.Unlock()
		if notifier != nil {
			notifier.Broadcast(players, "match_cancelled", map[string]any{"match_id": matchID, "reason": "opponent_disconnected"})
		}
		return
	}
	p := st.Progress[teamID]
	p.MatchID = st.Assignment.MatchID
	p.GameID = st.Assignment.GameID
	p.TeamID = teamID
	p.Sequence++
	p.Outcome = domain.OutcomeForfeited
	st.Progress[teamID] = p
	st.ForcedReasons[teamID] = domain.ReasonDisconnect
	matchID := st.Assignment.MatchID
	s.mu.Unlock()
	_ = s.trySettle(context.Background(), matchID)
}

func (s *Service) expireUnstarted(matchID string) {
	s.mu.Lock()
	st := s.matches[matchID]
	legendDraftActive := st != nil && st.Assignment.LegendSeries && st.Ready[st.Assignment.FirstTeamID] && st.Ready[st.Assignment.SecondTeamID]
	if st == nil || st.Settled || st.Assignment.StartedAtMS != 0 || legendDraftActive {
		s.mu.Unlock()
		return
	}
	st.Settled = true
	players := allPlayers(st.Assignment)
	for _, player := range players {
		delete(s.playerMatch, player)
	}
	notifier := s.notifier
	s.mu.Unlock()
	if notifier != nil {
		notifier.Broadcast(players, "match_cancelled", map[string]any{"match_id": matchID, "reason": "connection_timeout"})
	}
}

func (s *Service) trySettle(ctx context.Context, matchID string) error {
	s.mu.Lock()
	st := s.matches[matchID]
	if st == nil || st.Settled {
		s.mu.Unlock()
		return nil
	}
	firstP, firstOK := st.Progress[st.Assignment.FirstTeamID]
	secondP, secondOK := st.Progress[st.Assignment.SecondTeamID]
	forced := firstOK && (firstP.Outcome == domain.OutcomeForfeited || firstP.Outcome == domain.OutcomeSurrendered || firstP.Outcome == domain.OutcomeTimedOut) || secondOK && (secondP.Outcome == domain.OutcomeForfeited || secondP.Outcome == domain.OutcomeSurrendered || secondP.Outcome == domain.OutcomeTimedOut)
	finished := firstOK && secondOK && firstP.Outcome != domain.OutcomeActive && secondP.Outcome != domain.OutcomeActive
	if !forced && !finished {
		s.mu.Unlock()
		return nil
	}
	if !firstOK {
		firstP = domain.Progress{MatchID: matchID, GameID: st.Assignment.GameID, TeamID: st.Assignment.FirstTeamID, Outcome: domain.OutcomeActive}
	}
	if !secondOK {
		secondP = domain.Progress{MatchID: matchID, GameID: st.Assignment.GameID, TeamID: st.Assignment.SecondTeamID, Outcome: domain.OutcomeActive}
	}
	settlement, err := domain.BuildSettlement(matchID, st.Assignment.GameID, firstP, secondP)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	losingTeam := st.Assignment.FirstTeamID
	if settlement.WinnerTeamID == losingTeam {
		losingTeam = st.Assignment.SecondTeamID
	}
	if reason, ok := st.ForcedReasons[losingTeam]; ok {
		settlement.Reason = reason
	}
	if st.Assignment.LegendSeries {
		elapsed := int64(0)
		if settlement.WinnerTeamID == firstP.TeamID && firstP.CompletedAtMS != nil {
			elapsed = *firstP.CompletedAtMS
		}
		if settlement.WinnerTeamID == secondP.TeamID && secondP.CompletedAtMS != nil {
			elapsed = *secondP.CompletedAtMS
		}
		st.SeriesGames = append(st.SeriesGames, domain.LegendGame{GameNumber: st.Draft.GameNumber, GameID: st.Assignment.GameID, CharacterID: st.Draft.Selected, WinnerTeamID: settlement.WinnerTeamID, Reason: settlement.Reason, ElapsedMS: elapsed})
		if settlement.WinnerTeamID == st.Assignment.FirstTeamID {
			st.Draft.PlayerOneWins++
		} else {
			st.Draft.PlayerTwoWins++
		}
		seriesForced := settlement.Reason == domain.ReasonDisconnect || settlement.Reason == domain.ReasonIntegrity
		if !seriesForced && st.Draft.PlayerOneWins < 2 && st.Draft.PlayerTwoWins < 2 {
			game := st.SeriesGames[len(st.SeriesGames)-1]
			st.Draft.UsedCharacters = append(st.Draft.UsedCharacters, st.Draft.Selected)
			st.Draft.GameNumber++
			st.Draft.Selected = ""
			st.Draft.SelectingTeam = losingTeam
			st.Assignment.GameID, _ = randomHex(12)
			fallback, _ := randomHex(8)
			st.Assignment.Rules.Seed = domain.SeriesSeed(st.Assignment.Rules, st.Draft.GameNumber, fallback)
			st.Assignment.Rules.CharacterID = ""
			st.Assignment.StartedAtMS = 0
			st.Progress = map[string]domain.Progress{}
			st.Idempotency = map[string]bool{}
			st.PendingSL = map[string]bool{}
			delete(st.ForcedReasons, losingTeam)
			a := st.Assignment
			draft := *st.Draft
			players := allPlayers(a)
			notifier := s.notifier
			s.mu.Unlock()
			if notifier != nil {
				notifier.Broadcast(players, "legend_game_settled", game)
				notifier.Broadcast(players, "legend_pick_required", map[string]any{"available_characters": domain.AvailableCharacters(draft, draft.GameNumber), "draft": draft, "assignment": a})
			}
			time.AfterFunc(domain.LegendPickWindow, func() { s.autoLegendPick(a.MatchID, a.GameID) })
			return nil
		}
		if !seriesForced {
			settlement.Reason = domain.ReasonSeriesVictory
		}
		settlement.SeriesGames = append([]domain.LegendGame{}, st.SeriesGames...)
	} else if st.Assignment.Kind == domain.QueueEntertainment && st.Assignment.Rules.BestOf == 3 {
		elapsed := int64(0)
		if settlement.WinnerTeamID == firstP.TeamID && firstP.CompletedAtMS != nil {
			elapsed = *firstP.CompletedAtMS
		}
		if settlement.WinnerTeamID == secondP.TeamID && secondP.CompletedAtMS != nil {
			elapsed = *secondP.CompletedAtMS
		}
		character := st.Assignment.Rules.CharacterID
		if character == "" && st.Assignment.TeamSize == 1 && len(st.Assignment.FirstPlayerIDs) > 0 {
			character = st.Assignment.CharacterIDs[st.Assignment.FirstPlayerIDs[0]]
		}
		game := domain.LegendGame{GameNumber: len(st.SeriesGames) + 1, GameID: st.Assignment.GameID,
			CharacterID: character, WinnerTeamID: settlement.WinnerTeamID, Reason: settlement.Reason, ElapsedMS: elapsed}
		st.SeriesGames = append(st.SeriesGames, game)
		firstWins, secondWins := 0, 0
		for _, played := range st.SeriesGames {
			if played.WinnerTeamID == st.Assignment.FirstTeamID {
				firstWins++
			} else if played.WinnerTeamID == st.Assignment.SecondTeamID {
				secondWins++
			}
		}
		seriesForced := settlement.Reason == domain.ReasonDisconnect || settlement.Reason == domain.ReasonIntegrity
		if !seriesForced && firstWins < 2 && secondWins < 2 {
			st.Assignment.GameID, _ = randomHex(12)
			fallback, _ := randomHex(8)
			st.Assignment.Rules.Seed = domain.SeriesSeed(st.Assignment.Rules, len(st.SeriesGames)+1, fallback)
			st.Assignment.StartedAtMS = 0
			st.Assignment.FirstSteamLobbyID = ""
			st.Assignment.SecondSteamLobbyID = ""
			st.Progress = map[string]domain.Progress{}
			st.Idempotency = map[string]bool{}
			st.PendingSL = map[string]bool{}
			st.SurrenderVotes = map[string]map[string]bool{}
			st.ForcedReasons = map[string]domain.FinishReason{}
			st.SteamLobbyIDs = map[string]string{}
			a := st.Assignment
			if a.TeamSize == 1 {
				a.StartedAtMS = time.Now().UnixMilli()
				st.Assignment.StartedAtMS = a.StartedAtMS
			}
			players := allPlayers(a)
			notifier := s.notifier
			s.mu.Unlock()
			if notifier != nil {
				notifier.Broadcast(players, "series_game_settled", game)
			}
			if a.TeamSize > 1 {
				if notifier != nil {
					notifier.Broadcast([]string{a.FirstSteamHostPlayerID, a.SecondSteamHostPlayerID}, "steam_lobby_required", a)
				}
				return nil
			}
			return s.startGame(ctx, a)
		}
		if !seriesForced {
			settlement.Reason = domain.ReasonSeriesVictory
		}
		settlement.SeriesGames = append([]domain.LegendGame{}, st.SeriesGames...)
	}
	st.Settled = true
	players := allPlayers(st.Assignment)
	assignment := st.Assignment
	notifier := s.notifier
	s.mu.Unlock()
	deltas := map[string]int{}
	if assignment.Kind != domain.QueueEntertainment {
		var err error
		deltas, err = s.repo.ApplyRatings(ctx, assignment, settlement.WinnerTeamID)
		if err != nil {
			return err
		}
	}
	settlement.VisibleRatingDeltas = deltas
	if err := s.repo.SaveSettlement(ctx, settlement); err != nil {
		return err
	}
	if notifier != nil {
		notifier.Broadcast(players, "settlement", settlement)
	}
	return nil
}

func (s *Service) stateForPlayer(playerID string) (*state, string, error) {
	id := s.playerMatch[playerID]
	st := s.matches[id]
	if st == nil {
		return nil, "", errors.New("player has no active match")
	}
	for _, p := range st.Assignment.FirstPlayerIDs {
		if p == playerID {
			return st, st.Assignment.FirstTeamID, nil
		}
	}
	for _, p := range st.Assignment.SecondPlayerIDs {
		if p == playerID {
			return st, st.Assignment.SecondTeamID, nil
		}
	}
	return nil, "", errors.New("player is not in match")
}
func randomHex(n int) (string, error) {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return hex.EncodeToString(b), nil
}
func allPlayers(a domain.Assignment) []string {
	return append(append([]string{}, a.FirstPlayerIDs...), a.SecondPlayerIDs...)
}
func teamPlayers(a domain.Assignment, teamID string) []string {
	if teamID == a.FirstTeamID {
		return a.FirstPlayerIDs
	}
	return a.SecondPlayerIDs
}
func first(ids []string, id string) bool { return len(ids) > 0 && ids[0] == id }
func validCharacter(value string) bool {
	for _, character := range domain.Characters {
		if character == value {
			return true
		}
	}
	return false
}
func (s *Service) AssignmentFor(playerID string) (domain.Assignment, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	st, _, err := s.stateForPlayer(playerID)
	if err != nil {
		return domain.Assignment{}, false
	}
	return st.Assignment, true
}

func (s *Service) ClockForPlayer(playerID string) map[string]any {
	s.mu.Lock()
	defer s.mu.Unlock()
	now := time.Now().UnixMilli()
	result := map[string]any{"server_unix_ms": now}
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		return result
	}
	result["match_id"] = st.Assignment.MatchID
	result["game_id"] = st.Assignment.GameID
	if st.Assignment.StartedAtMS == 0 {
		return result
	}
	result["match_started_ms"] = st.Assignment.StartedAtMS
	result["elapsed_ms"] = teamElapsed(st, teamID, now)
	result["paused"] = st.PausedAtMS[teamID] > 0
	return result
}

func (s *Service) IsInRace(playerID string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	st, _, err := s.stateForPlayer(playerID)
	return err == nil && !st.Settled && st.Assignment.StartedAtMS > 0
}
func (s *Service) IntegrityFailure(ctx context.Context, playerID string) error {
	s.mu.Lock()
	st, teamID, err := s.stateForPlayer(playerID)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	p := st.Progress[teamID]
	p.MatchID = st.Assignment.MatchID
	p.GameID = st.Assignment.GameID
	p.TeamID = teamID
	p.Sequence++
	p.Outcome = domain.OutcomeForfeited
	st.Progress[teamID] = p
	st.ForcedReasons[teamID] = domain.ReasonIntegrity
	id := st.Assignment.MatchID
	s.mu.Unlock()
	return s.trySettle(ctx, id)
}

func (s *Service) timeout(matchID, gameID string) {
	s.mu.Lock()
	st := s.matches[matchID]
	if st == nil || st.Settled || st.Assignment.GameID != gameID {
		s.mu.Unlock()
		return
	}
	now := time.Now().UnixMilli()
	changed := false
	nextDelay := st.Assignment.Rules.TimeLimitMS
	for _, teamID := range []string{st.Assignment.FirstTeamID, st.Assignment.SecondTeamID} {
		p := st.Progress[teamID]
		p.MatchID, p.GameID, p.TeamID = matchID, st.Assignment.GameID, teamID
		if p.Outcome == domain.OutcomeActive || p.Outcome == "" {
			remaining := st.Assignment.Rules.TimeLimitMS - teamElapsed(st, teamID, now)
			if remaining > 0 {
				if remaining < nextDelay {
					nextDelay = remaining
				}
				continue
			}
			p.Outcome = domain.OutcomeTimedOut
			st.ForcedReasons[teamID] = domain.ReasonTimeout
			changed = true
		}
		p.Sequence++
		st.Progress[teamID] = p
	}
	s.mu.Unlock()
	if !changed {
		time.AfterFunc(time.Duration(max64(1000, nextDelay))*time.Millisecond, func() { s.timeout(matchID, gameID) })
		return
	}
	_ = s.trySettle(context.Background(), matchID)
}

func teamElapsed(st *state, teamID string, now int64) int64 {
	effectiveNow := now
	if pausedAt := st.PausedAtMS[teamID]; pausedAt > 0 {
		effectiveNow = pausedAt
	}
	return max64(0, effectiveNow-st.Assignment.StartedAtMS-st.PausedTotalMS[teamID])
}

func max64(first, second int64) int64 {
	if first > second {
		return first
	}
	return second
}

var _ = fmt.Sprintf
