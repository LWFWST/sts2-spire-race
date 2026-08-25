package domain

import "testing"

func TestLegendSecondBanReturnsAfterGameOne(t *testing.T) {
	d := LegendDraft{PlayerOneBanOne: "Ironclad", PlayerOneBanTwo: "Silent", PlayerTwoBanOne: "Defect", PlayerTwoBanTwo: "Regent", GameNumber: 1}
	first := AvailableCharacters(d, 1)
	if len(first) != 1 || first[0] != "Necrobinder" {
		t.Fatalf("unexpected game one pool: %v", first)
	}
	d.UsedCharacters = []string{"Necrobinder"}
	d.GameNumber = 2
	second := AvailableCharacters(d, 2)
	if len(second) != 2 || second[0] != "Silent" || second[1] != "Regent" {
		t.Fatalf("unexpected game two pool: %v", second)
	}
}

func TestLegendCharactersNeverRepeat(t *testing.T) {
	d := LegendDraft{PlayerOneBanOne: "Ironclad", PlayerTwoBanOne: "Defect", UsedCharacters: []string{"Silent"}, GameNumber: 2}
	if _, err := SelectLegendCharacter(d, "Silent"); err == nil {
		t.Fatal("used character was accepted")
	}
	if c, err := SelectLegendCharacter(d, "Regent"); err != nil || c != "Regent" {
		t.Fatalf("pick failed: %s %v", c, err)
	}
}
