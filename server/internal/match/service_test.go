package match

import (
	"context"
	"sync"
	"testing"
	"time"

	"github.com/mcc/sts2-spire-race/server/internal/domain"
)

type fakeRepository struct {
	mu          sync.Mutex
	starts      []domain.Assignment
	settlements []domain.Settlement
	ratingCalls int
}

func (f *fakeRepository) SaveMatch(_ context.Context, a domain.Assignment) error { return nil }
func (f *fakeRepository) StartMatch(_ context.Context, _ string, _ time.Time) error {
	return nil
}
func (f *fakeRepository) SaveSettlement(_ context.Context, s domain.Settlement) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.settlements = append(f.settlements, s)
	return nil
}
func (f *fakeRepository) ApplyRatings(_ context.Context, _ domain.Assignment, _ string) (map[string]int, error) {
	f.mu.Lock()
	f.ratingCalls++
	f.mu.Unlock()
	return map[string]int{}, nil
}

type fakeNotifier struct {
	mu     sync.Mutex
	events []string
}

func (f *fakeNotifier) Broadcast(_ []string, event string, _ any) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.events = append(f.events, event)
}

func request(player string, kind domain.QueueKind, size int, tier string, members ...string) domain.QueueRequest {
	return domain.QueueRequest{PlayerID: player, GameVersion: "v0.111.0", Kind: kind, TeamSize: size,
		Pool: map[bool]string{true: "solo", false: "team"}[size == 1], VisibleTiers: []string{tier}, TeamPlayerIDs: members}
}

func createStarted(t *testing.T, service *Service, first, second domain.QueueRequest) domain.Assignment {
	t.Helper()
	a, err := service.Create(context.Background(), first, second)
	if err != nil {
		t.Fatal(err)
	}
	for _, player := range []string{first.PlayerID, second.PlayerID} {
		if err := service.Confirm(context.Background(), player, true); err != nil {
			t.Fatal(err)
		}
		if err := service.Ready(context.Background(), player); err != nil {
			t.Fatal(err)
		}
	}
	a, _ = service.AssignmentFor(first.PlayerID)
	return a
}

func TestServiceStartsSharedCharacterAndBossSettlement(t *testing.T) {
	repo, notify := &fakeRepository{}, &fakeNotifier{}
	service := New(repo, notify)
	a := createStarted(t, service,
		request("a", domain.QueueCasual, 1, "Gold", "a"),
		request("b", domain.QueueCasual, 1, "Gold", "b"))
	if a.StartedAtMS == 0 || a.Rules.CharacterID == "" || a.Rules.EventSLLimit != 3 || a.Rules.CombatSLLimit != 3 {
		t.Fatalf("invalid started assignment: %+v", a)
	}
	loser := domain.Progress{MatchID: a.MatchID, GameID: a.GameID, TeamID: a.SecondTeamID, Sequence: 1, Floor: 55, FloorEnteredAtMS: 900,
		Outcome: domain.OutcomeScoreLocked}
	if err := service.Progress(context.Background(), "b", "b-1", loser); err != nil {
		t.Fatal(err)
	}
	completed := int64(42_123)
	winner := domain.Progress{MatchID: a.MatchID, GameID: a.GameID, TeamID: a.FirstTeamID, Sequence: 1, Floor: 50, FloorEnteredAtMS: 1000,
		FinalBossDefeated: true, CompletedAtMS: &completed, Outcome: domain.OutcomeFinished}
	if err := service.Progress(context.Background(), "a", "a-1", winner); err != nil {
		t.Fatal(err)
	}
	if len(repo.settlements) != 1 || repo.settlements[0].WinnerTeamID != a.FirstTeamID || repo.settlements[0].Reason != domain.ReasonBossCompletion {
		t.Fatalf("unexpected settlement: %+v", repo.settlements)
	}
}

func TestSingleBossFinishWaitsForOpponent(t *testing.T) {
	repo, notify := &fakeRepository{}, &fakeNotifier{}
	service := New(repo, notify)
	a := createStarted(t, service,
		request("a", domain.QueueRanked, 1, "Gold", "a"),
		request("b", domain.QueueRanked, 1, "Gold", "b"))
	completed := int64(42_123)
	if err := service.Progress(context.Background(), "a", "a-finished", domain.Progress{
		MatchID: a.MatchID, GameID: a.GameID, TeamID: a.FirstTeamID, Sequence: 1, Floor: 50, FloorEnteredAtMS: 40_000,
		FinalBossDefeated: true, CompletedAtMS: &completed, Outcome: domain.OutcomeFinished,
	}); err != nil {
		t.Fatal(err)
	}
	if len(repo.settlements) != 0 {
		t.Fatalf("single finisher was settled before the opponent ended: %+v", repo.settlements)
	}
	notify.mu.Lock()
	defer notify.mu.Unlock()
	foundPending := false
	for _, event := range notify.events {
		if event == "finish_pending" {
			foundPending = true
		}
	}
	if !foundPending {
		t.Fatal("finishing team did not receive finish_pending")
	}
}

func TestEntertainmentSettlementSkipsRatings(t *testing.T) {
	repo, notify := &fakeRepository{}, &fakeNotifier{}
	service := New(repo, notify)
	assignment := domain.Assignment{
		MatchID: "fun-ABC123", GameID: "fun-ABC123", GameVersion: "v0.111.0", Kind: domain.QueueEntertainment,
		TeamSize: 1, FirstTeamID: "room-ABC123-1", SecondTeamID: "room-ABC123-2",
		FirstPlayerIDs: []string{"a"}, SecondPlayerIDs: []string{"b"}, StartedAtMS: time.Now().UnixMilli(),
		Rules: domain.Rules{TeamSize: 1, Seed: "shared", Ascension: 3, TimeLimitMS: domain.MaxMatchMilliseconds,
			EventSLLimit: 3, CombatSLLimit: 3, CharacterID: "Ironclad"},
	}
	if err := service.CreateEntertainment(context.Background(), assignment); err != nil {
		t.Fatal(err)
	}
	loser := domain.Progress{MatchID: assignment.MatchID, GameID: assignment.GameID, TeamID: assignment.SecondTeamID,
		Sequence: 1, Floor: 43, FloorEnteredAtMS: 50_000, Outcome: domain.OutcomeScoreLocked}
	if err := service.Progress(context.Background(), "b", "fun-b", loser); err != nil {
		t.Fatal(err)
	}
	completed := int64(60_123)
	winner := domain.Progress{MatchID: assignment.MatchID, GameID: assignment.GameID, TeamID: assignment.FirstTeamID,
		Sequence: 1, Floor: 51, FloorEnteredAtMS: 58_000, FinalBossDefeated: true, CompletedAtMS: &completed, Outcome: domain.OutcomeFinished}
	if err := service.Progress(context.Background(), "a", "fun-a", winner); err != nil {
		t.Fatal(err)
	}
	if len(repo.settlements) != 1 || repo.settlements[0].WinnerTeamID != assignment.FirstTeamID {
		t.Fatalf("entertainment settlement mismatch: %+v", repo.settlements)
	}
	if repo.ratingCalls != 0 {
		t.Fatalf("entertainment settlement updated ratings %d times", repo.ratingCalls)
	}
}

func TestServiceSLIsChargedOnResumeAndIdempotent(t *testing.T) {
	service := New(&fakeRepository{}, nil)
	a, err := service.Create(context.Background(),
		request("a", domain.QueueRanked, 1, "Gold", "a"),
		request("b", domain.QueueRanked, 1, "Gold", "b"))
	if err != nil {
		t.Fatal(err)
	}
	if a.Rules.EventSLLimit != 1 || a.Rules.CombatSLLimit != 1 {
		t.Fatal("ranked SL budget must be one per category")
	}
	if _, err := service.SaveAndQuit("a", false, false); err != nil {
		t.Fatal(err)
	}
	first, err := service.Resume("a", "resume-key")
	if err != nil {
		t.Fatal(err)
	}
	second, err := service.Resume("a", "resume-key")
	if err != nil {
		t.Fatal(err)
	}
	if first.EventSLUsed != 1 || second.EventSLUsed != 1 || second.Sequence != first.Sequence {
		t.Fatalf("resume was not idempotent: first=%+v second=%+v", first, second)
	}
}

func TestTeamSurrenderRequiresStrictMajority(t *testing.T) {
	repo := &fakeRepository{}
	service := New(repo, nil)
	a, err := service.Create(context.Background(),
		request("a1", domain.QueueCasual, 3, "Gold", "a1", "a2", "a3"),
		request("b1", domain.QueueCasual, 3, "Gold", "b1", "b2", "b3"))
	if err != nil {
		t.Fatal(err)
	}
	if err := service.Surrender(context.Background(), "a1", true); err != nil {
		t.Fatal(err)
	}
	if len(repo.settlements) != 0 {
		t.Fatal("one of three votes incorrectly surrendered the team")
	}
	if err := service.Surrender(context.Background(), "a2", true); err != nil {
		t.Fatal(err)
	}
	if len(repo.settlements) != 1 || repo.settlements[0].WinnerTeamID != a.SecondTeamID || repo.settlements[0].Reason != domain.ReasonSurrender {
		t.Fatalf("strict-majority settlement mismatch: %+v", repo.settlements)
	}
}

func TestLegendBO3PersistsBansAndOnlySettlesAfterTwoWins(t *testing.T) {
	repo, notify := &fakeRepository{}, &fakeNotifier{}
	service := New(repo, notify)
	a := createStarted(t, service,
		request("a", domain.QueueRanked, 1, "Legend", "a"),
		request("b", domain.QueueRanked, 1, "Legend", "b"))
	if !a.LegendSeries || a.StartedAtMS == 0 {
		t.Fatal("Legend ready check did not enter the draft")
	}
	if err := service.SubmitLegendBans(context.Background(), "a", "Ironclad", "Silent"); err != nil {
		t.Fatal(err)
	}
	if err := service.SubmitLegendBans(context.Background(), "b", "Defect", "Regent"); err != nil {
		t.Fatal(err)
	}
	a, _ = service.AssignmentFor("a")
	finishLegendGame(t, service, a, "a", "b")
	if len(repo.settlements) != 0 {
		t.Fatal("BO3 settled after only one game")
	}
	second, _ := service.AssignmentFor("a")
	if second.GameID == a.GameID || second.Rules.Seed == a.Rules.Seed {
		t.Fatal("next BO3 game did not receive a fresh game id and seed")
	}
	if err := service.SelectLegendCharacter(context.Background(), "b", "Silent"); err != nil {
		t.Fatal(err)
	}
	second, _ = service.AssignmentFor("a")
	finishLegendGame(t, service, second, "a", "b")
	if len(repo.settlements) != 1 || repo.settlements[0].Reason != domain.ReasonSeriesVictory || len(repo.settlements[0].SeriesGames) != 2 {
		t.Fatalf("unexpected BO3 settlement: %+v", repo.settlements)
	}
}

func finishLegendGame(t *testing.T, service *Service, a domain.Assignment, winner, loser string) {
	t.Helper()
	loserTeam := a.SecondTeamID
	winnerTeam := a.FirstTeamID
	if winner != a.FirstPlayerIDs[0] {
		winnerTeam, loserTeam = loserTeam, winnerTeam
	}
	if err := service.Progress(context.Background(), loser, a.GameID+"-loser", domain.Progress{
		MatchID: a.MatchID, GameID: a.GameID, TeamID: loserTeam, Sequence: 1, Floor: 30, FloorEnteredAtMS: 3000, Outcome: domain.OutcomeScoreLocked,
	}); err != nil {
		t.Fatal(err)
	}
	completed := int64(10_000)
	if err := service.Progress(context.Background(), winner, a.GameID+"-winner", domain.Progress{
		MatchID: a.MatchID, GameID: a.GameID, TeamID: winnerTeam, Sequence: 1, Floor: 50, FloorEnteredAtMS: 9000,
		FinalBossDefeated: true, CompletedAtMS: &completed, Outcome: domain.OutcomeFinished,
	}); err != nil {
		t.Fatal(err)
	}
}
