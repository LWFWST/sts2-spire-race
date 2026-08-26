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
		len(history[0].OpponentNames) != 1 || history[0].OpponentNames[0] != "Integration Loser" || history[0].Character != "Ironclad" {
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
