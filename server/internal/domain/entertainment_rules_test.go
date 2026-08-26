package domain

import "testing"

func TestNormalizeEntertainmentRulesLocksCoreDefaultsAndPreservesSeriesSeedSlots(t *testing.T) {
	rules := NormalizeEntertainmentRules(Rules{
		TeamSize: 1, BestOf: 3, AllowDuplicateCharacters: false, CharacterPolicy: "random",
		VictoryRule: "custom", AllowSpectators: true, Modifiers: []string{"Draft", "Draft"},
		SeriesSeeds: []string{" FIRST ", "", " THIRD "},
	})
	if !rules.AllowDuplicateCharacters || rules.CharacterPolicy != "host_for_1v1" ||
		rules.VictoryRule != "certified_race" || rules.AllowSpectators || len(rules.Modifiers) != 1 {
		t.Fatalf("entertainment defaults were not locked: %+v", rules)
	}
	if len(rules.SeriesSeeds) != 3 || rules.SeriesSeeds[0] != "FIRST" || rules.SeriesSeeds[1] != "" || rules.SeriesSeeds[2] != "THIRD" {
		t.Fatalf("series seed slots changed: %#v", rules.SeriesSeeds)
	}
	if got := SeriesSeed(rules, 2, "fallback"); got != "fallback" {
		t.Fatalf("empty seed slot should use fallback, got %q", got)
	}
}
