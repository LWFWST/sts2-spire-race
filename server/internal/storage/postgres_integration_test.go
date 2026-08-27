package storage

import (
	"context"
	"fmt"
	"os"
	"testing"
	"time"

	"github.com/mcc/sts2-spire-race/server/internal/domain"
)

func TestRankedSettlementPersistsProfileHistoryAndLeaderboard(t *testing.T) {
	databaseURL := os.Getenv("RACE_TEST_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("RACE_TEST_DATABASE_URL is not set")
	}
	ctx := context.Background()
	store, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()

	suffix := time.Now().UTC().Format("20060102150405.000000000")
	winnerID := "integration-winner-" + suffix
	loserID := "integration-loser-" + suffix
	matchID := "integration-match-" + suffix
	defer func() {
		_, _ = store.Pool.Exec(ctx, `DELETE FROM match_participants WHERE match_id=$1`, matchID)
		_, _ = store.Pool.Exec(ctx, `DELETE FROM matches WHERE id=$1`, matchID)
		_, _ = store.Pool.Exec(ctx, `DELETE FROM ratings WHERE player_id IN ($1,$2)`, winnerID, loserID)
		_, _ = store.Pool.Exec(ctx, `DELETE FROM players WHERE id IN ($1,$2)`, winnerID, loserID)
	}()

	if err := store.UpsertPlayer(ctx, winnerID, "Integration Winner"); err != nil {
		t.Fatal(err)
	}
	if err := store.UpsertPlayer(ctx, loserID, "Integration Loser"); err != nil {
		t.Fatal(err)
	}
	a := domain.Assignment{
		MatchID: matchID, GameID: "integration-game", GameVersion: "v0.111.0", Kind: domain.QueueRanked, TeamSize: 1,
		FirstTeamID: "winner-team", SecondTeamID: "loser-team", FirstPlayerIDs: []string{winnerID}, SecondPlayerIDs: []string{loserID},
		Rules: domain.Rules{CharacterID: "Ironclad", Ascension: 7, Seed: "INTEGRATION"},
	}
	if err := store.SaveMatch(ctx, a); err != nil {
		t.Fatal(err)
	}
	if err := store.StartMatch(ctx, matchID, time.Now().UTC()); err != nil {
		t.Fatal(err)
	}
	winnerTime := int64(2_523_456)
	settlement := domain.Settlement{
		MatchID: matchID, GameID: a.GameID, WinnerTeamID: a.FirstTeamID, Reason: domain.ReasonBossCompletion,
		First:       domain.SettlementSide{TeamID: a.FirstTeamID, Outcome: domain.OutcomeFinished, HighestFloor: 51, HighestFloorEnteredMS: winnerTime, CompletionMS: &winnerTime},
		Second:      domain.SettlementSide{TeamID: a.SecondTeamID, Outcome: domain.OutcomeScoreLocked, HighestFloor: 38, HighestFloorEnteredMS: 1_900_000},
		CompletedAt: time.Now().UTC(),
		SeriesGames: []domain.LegendGame{
			{GameNumber: 1, GameID: "g1", CharacterID: "Ironclad", WinnerTeamID: a.FirstTeamID, Reason: domain.ReasonBossCompletion, ElapsedMS: winnerTime},
			{GameNumber: 2, GameID: "g2", CharacterID: "Silent", WinnerTeamID: a.FirstTeamID, Reason: domain.ReasonHighestFloor, ElapsedMS: 2_600_000},
		},
	}
	deltas, err := store.ApplyRatings(ctx, a, a.FirstTeamID)
	if err != nil {
		t.Fatal(err)
	}
	settlement.VisibleRatingDeltas = deltas
	if err := store.SaveSettlement(ctx, settlement); err != nil {
		t.Fatal(err)
	}
	if deltas[winnerID] != 25 || deltas[loserID] != -20 {
		t.Fatalf("unexpected visible rating deltas: %+v", deltas)
	}

	winnerRank, err := store.RatingProfile(ctx, winnerID, "solo")
	if err != nil {
		t.Fatal(err)
	}
	if winnerRank.GamesPlayed != 1 || winnerRank.Wins != 1 || winnerRank.Losses != 0 {
		t.Fatalf("winner profile was not updated: %+v", winnerRank)
	}
	loserRank, err := store.RatingProfile(ctx, loserID, "solo")
	if err != nil {
		t.Fatal(err)
	}
	if loserRank.GamesPlayed != 1 || loserRank.Wins != 0 || loserRank.Losses != 1 {
		t.Fatalf("loser profile was not updated: %+v", loserRank)
	}

	history, err := store.History(ctx, winnerID, 5)
	if err != nil {
		t.Fatal(err)
	}
	if len(history) != 1 || !history[0].Victory || history[0].RatingDelta != 25 || history[0].RunTimeMS != winnerTime ||
		!history[0].Completed || history[0].HighestFloor != 51 || history[0].OpponentCompleted || history[0].OpponentHighestFloor != 38 ||
		len(history[0].OpponentNames) != 1 || history[0].OpponentNames[0] != "Integration Loser" || history[0].Character != "Ironclad" ||
		history[0].LocalTeamID != a.FirstTeamID || len(history[0].SeriesGames) != 2 || history[0].SeriesGames[1].CharacterID != "Silent" {
		t.Fatalf("winner history mismatch: %+v", history)
	}
	loserHistory, err := store.History(ctx, loserID, 5)
	if err != nil {
		t.Fatal(err)
	}
	if len(loserHistory) != 1 || loserHistory[0].Victory || loserHistory[0].RatingDelta != -20 || loserHistory[0].RunTimeMS != 1_900_000 ||
		loserHistory[0].Completed || loserHistory[0].HighestFloor != 38 || !loserHistory[0].OpponentCompleted ||
		loserHistory[0].OpponentRunTimeMS != winnerTime || loserHistory[0].OpponentHighestFloor != 51 ||
		len(loserHistory[0].OpponentNames) != 1 || loserHistory[0].OpponentNames[0] != "Integration Winner" {
		t.Fatalf("loser history mismatch: %+v", loserHistory)
	}
	best, err := store.BestTime(ctx, winnerID)
	if err != nil || best != winnerTime {
		t.Fatalf("best time mismatch: %d, %v", best, err)
	}
	leaders, err := store.Leaderboard(ctx, "solo", 1000, winnerID, false)
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, row := range leaders {
		if row.PlayerID == winnerID {
			found = true
			if row.Wins != 1 || row.BestTimeMS != winnerTime {
				t.Fatalf("leaderboard row mismatch: %+v", row)
			}
		}
	}
	if !found {
		t.Fatal(fmt.Sprintf("winner %s was missing from leaderboard", winnerID))
	}
}

func TestFullEntertainmentRoomSwapsTeamsAndRejectsShrink(t *testing.T) {
	databaseURL := os.Getenv("RACE_TEST_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("RACE_TEST_DATABASE_URL is not set")
	}
	ctx := context.Background()
	store, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()

	suffix := fmt.Sprintf("%d", time.Now().UnixNano())
	code := "S" + suffix[len(suffix)-5:]
	players := []string{"room-host-" + suffix, "room-b-" + suffix, "room-c-" + suffix, "room-d-" + suffix}
	defer func() {
		_, _ = store.Pool.Exec(ctx, `DELETE FROM entertainment_rooms WHERE code=$1`, code)
		_, _ = store.Pool.Exec(ctx, `DELETE FROM players WHERE id=ANY($1)`, players)
	}()
	for _, player := range players {
		if err := store.UpsertPlayer(ctx, player, player); err != nil {
			t.Fatal(err)
		}
	}
	rules := domain.Rules{TeamSize: 2, Ascension: 3, TimeLimitMS: domain.MaxMatchMilliseconds, BestOf: 3}
	if err := store.CreateRoom(ctx, code, players[0], rules); err != nil {
		t.Fatal(err)
	}
	for _, player := range players[1:] {
		if _, err := store.JoinRoom(ctx, code, player); err != nil {
			t.Fatal(err)
		}
	}
	before, err := store.RoomSnapshot(ctx, code)
	if err != nil || len(before.Members) != 4 {
		t.Fatalf("room did not fill: %+v %v", before, err)
	}
	after, err := store.SwitchRoomTeam(ctx, code, players[0])
	if err != nil {
		t.Fatal(err)
	}
	teamOf := func(id string) int {
		for _, member := range after.Members {
			if member.PlayerID == id {
				return member.Team
			}
		}
		return 0
	}
	if teamOf(players[0]) != 2 || len(after.Members) != 4 ||
		len(filterRoomTeam(after.Members, 1)) != 2 || len(filterRoomTeam(after.Members, 2)) != 2 {
		t.Fatalf("full-room team exchange failed: %+v", after.Members)
	}
	if _, err := store.UpdateRoomRules(ctx, code, players[0], rulesWithTeamSize(rules, 1)); err == nil {
		t.Fatal("room size was reduced below the current team population")
	}
}

func TestReplayCloudAndEntertainmentSpectatorPermissions(t *testing.T) {
	databaseURL := os.Getenv("RACE_TEST_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("RACE_TEST_DATABASE_URL is not set")
	}
	ctx := context.Background()
	store, err := Open(ctx, databaseURL)
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()

	suffix := fmt.Sprintf("%d", time.Now().UnixNano())
	code := "W" + suffix[len(suffix)-5:]
	players := []string{"watch-host-" + suffix, "watch-rival-" + suffix, "watch-viewer-" + suffix, "watch-outsider-" + suffix}
	matchID := "fun-" + code
	defer func() {
		_, _ = store.Pool.Exec(ctx, `DELETE FROM matches WHERE id=$1`, matchID)
		_, _ = store.Pool.Exec(ctx, `DELETE FROM entertainment_rooms WHERE code=$1`, code)
		_, _ = store.Pool.Exec(ctx, `DELETE FROM players WHERE id=ANY($1)`, players)
	}()
	for _, player := range players {
		if err := store.UpsertPlayer(ctx, player, player); err != nil {
			t.Fatal(err)
		}
	}
	rules := domain.Rules{TeamSize: 1, Seed: "WATCH", Ascension: 3, TimeLimitMS: domain.MaxMatchMilliseconds,
		EventSLLimit: 3, CombatSLLimit: 3, CoordinationMode: "server", SpectatorSlots: 1}
	if err := store.CreateRoom(ctx, code, players[0], rules); err != nil {
		t.Fatal(err)
	}
	if _, err := store.JoinRoom(ctx, code, players[1]); err != nil {
		t.Fatal(err)
	}
	room, err := store.JoinRoomSpectator(ctx, code, players[2])
	if err != nil || len(room.Spectators) != 1 {
		t.Fatalf("spectator did not join: %+v %v", room.Spectators, err)
	}
	room, err = store.SetRoomSpectatorTarget(ctx, code, players[2], 2)
	if err != nil || room.Spectators[0].WatchingTeam != 2 {
		t.Fatalf("spectator target did not update: %+v %v", room.Spectators, err)
	}
	if _, err := store.JoinRoomSpectator(ctx, code, players[3]); err == nil {
		t.Fatal("spectator capacity was not enforced")
	}

	a := domain.Assignment{MatchID: matchID, GameID: matchID + "-g1", GameVersion: "v0.111.0", Kind: domain.QueueEntertainment,
		TeamSize: 1, FirstTeamID: "watch-team-a", SecondTeamID: "watch-team-b", FirstPlayerIDs: []string{players[0]},
		SecondPlayerIDs: []string{players[1]}, Rules: rules}
	if err := store.SaveMatch(ctx, a); err != nil {
		t.Fatal(err)
	}
	bundle := []byte{'P', 'K', 3, 4, 1, 2, 3}
	replay, err := store.UpsertReplay(ctx, ReplayRow{MatchID: matchID, GameID: a.GameID, PlayerID: players[0],
		TeamID: a.FirstTeamID, RunID: "run-1", CharacterID: "Ironclad", EventCount: 4}, bundle)
	if err != nil || !replay.IsLive {
		t.Fatalf("live replay was not stored: %+v %v", replay, err)
	}
	live, err := store.SpectatableReplays(ctx, players[2])
	if err != nil || len(live) != 1 || live[0].MatchID != matchID {
		t.Fatalf("room spectator could not discover live replay: %+v %v", live, err)
	}
	listed, err := store.MatchReplays(ctx, players[2], matchID)
	if err != nil || len(listed) != 1 {
		t.Fatalf("room spectator could not list both-side replays: %+v %v", listed, err)
	}
	downloaded, err := store.ReplayBundle(ctx, players[2], matchID, a.GameID, players[0])
	if err != nil || string(downloaded) != string(bundle) {
		t.Fatalf("room spectator could not download replay: %v %v", downloaded, err)
	}
	if _, err := store.ReplayBundle(ctx, players[3], matchID, a.GameID, players[0]); err == nil {
		t.Fatal("unrelated player downloaded a private replay")
	}
}

func filterRoomTeam(members []EntertainmentRoomMember, team int) []EntertainmentRoomMember {
	result := make([]EntertainmentRoomMember, 0, len(members))
	for _, member := range members {
		if member.Team == team {
			result = append(result, member)
		}
	}
	return result
}

func rulesWithTeamSize(rules domain.Rules, size int) domain.Rules {
	rules.TeamSize = size
	return rules
}
