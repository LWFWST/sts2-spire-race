package domain

import "testing"

func ms(v int64) *int64 { return &v }

func TestBossCompletionAlwaysBeatsFailure(t *testing.T) {
	first := Progress{TeamID: "first", Floor: 30, FloorEnteredAtMS: 1000, FinalBossDefeated: true, CompletedAtMS: ms(5000), Outcome: OutcomeFinished}
	second := Progress{TeamID: "second", Floor: 50, FloorEnteredAtMS: 900, Outcome: OutcomeScoreLocked}
	winner, reason, _, err := Decide(first, second)
	if err != nil {
		t.Fatal(err)
	}
	if winner != "first" || reason != ReasonBossCompletion {
		t.Fatalf("got %s %s", winner, reason)
	}
}

func TestFallbackUsesFloorThenFirstEntry(t *testing.T) {
	tests := []struct {
		name          string
		first, second Progress
		winner        string
		reason        FinishReason
	}{
		{"floor", Progress{TeamID: "a", Floor: 40}, Progress{TeamID: "b", Floor: 39}, "a", ReasonHighestFloor},
		{"entry", Progress{TeamID: "a", Floor: 40, FloorEnteredAtMS: 2000}, Progress{TeamID: "b", Floor: 40, FloorEnteredAtMS: 3000}, "a", ReasonEarlierFloor},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			winner, reason, _, err := Decide(tt.first, tt.second)
			if err != nil {
				t.Fatal(err)
			}
			if winner != tt.winner || reason != tt.reason {
				t.Fatalf("got %s %s", winner, reason)
			}
		})
	}
}

func TestRestartPreservesCertifiedHighScore(t *testing.T) {
	p := Progress{TeamID: "a", Sequence: 7, Floor: 42, FloorEnteredAtMS: 123456, FinalBossDefeated: false, Outcome: OutcomeScoreLocked, RestartCount: 1}
	r := Restart(p)
	if r.Floor != 42 || r.FloorEnteredAtMS != 123456 || r.RestartCount != 2 || r.Sequence != 8 || r.Outcome != OutcomeActive {
		t.Fatalf("unexpected restart: %+v", r)
	}
	r = RecordFloor(r, 12, 200000)
	if r.Floor != 42 {
		t.Fatal("lower attempt erased certified high score")
	}
}

func TestSLBudgets(t *testing.T) {
	r := Rules{EventSLLimit: 1, CombatSLLimit: 3}
	p := Progress{EventSLUsed: 1, CombatSLUsed: 2}
	if HasSL(p, r, false) {
		t.Fatal("event SL should be exhausted")
	}
	if !HasSL(p, r, true) {
		t.Fatal("combat SL should remain")
	}
}

func TestAscensionBoundaries(t *testing.T) {
	for i := 0; i < 100; i++ {
		a, err := Ascension(QueueCasual, nil)
		if err != nil || a < 3 || a > 7 {
			t.Fatalf("casual ascension %d: %v", a, err)
		}
	}
	for _, tt := range []struct {
		tiers []string
		want  int
	}{{[]string{"Gold"}, 7}, {[]string{"Platinum", "Diamond"}, 9}, {[]string{"Legend"}, 9}, {[]string{"Unranked"}, 7}} {
		got, err := Ascension(QueueRanked, tt.tiers)
		if err != nil || got != tt.want {
			t.Fatalf("%v: got %d", tt.tiers, got)
		}
	}
}

func TestForcedLoss(t *testing.T) {
	a := Progress{TeamID: "a", Outcome: OutcomeForfeited}
	b := Progress{TeamID: "b", Outcome: OutcomeActive}
	winner, reason, _, _ := Decide(a, b)
	if winner != "b" || reason != ReasonDisconnect {
		t.Fatalf("got %s %s", winner, reason)
	}
}
