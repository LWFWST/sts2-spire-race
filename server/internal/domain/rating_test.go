package domain

import "testing"

func TestRatingConstants(t *testing.T) {
	if got := HiddenDelta(1500, 1500, true, 0, false); got != 24 {
		t.Fatalf("placement K mismatch: %d", got)
	}
	if got := HiddenDelta(1500, 1500, true, 20, false); got != 12 {
		t.Fatalf("standard K mismatch: %d", got)
	}
	if got := HiddenDelta(1500, 1500, true, 20, true); got != 8 {
		t.Fatalf("legend K mismatch: %d", got)
	}
	if got := VisibleDelta(1500, 1500, true); got != 25 {
		t.Fatalf("visible win: %d", got)
	}
	if got := VisibleDelta(1500, 1500, false); got != -20 {
		t.Fatalf("visible loss: %d", got)
	}
}
