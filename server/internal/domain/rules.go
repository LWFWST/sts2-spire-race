package domain

import (
	"crypto/rand"
	"errors"
	"math/big"
	"strings"
)

var Characters = []string{"Ironclad", "Silent", "Defect", "Necrobinder", "Regent"}

func Ascension(kind QueueKind, tiers []string) (int, error) {
	switch kind {
	case QueueCasual:
		n, err := rand.Int(rand.Reader, big.NewInt(5))
		if err != nil {
			return 0, err
		}
		return 3 + int(n.Int64()), nil
	case QueueRanked:
		for _, tier := range tiers {
			if strings.EqualFold(tier, "diamond") || strings.EqualFold(tier, "legend") {
				return 9, nil
			}
		}
		return 7, nil
	default:
		return 0, errors.New("unsupported queue kind")
	}
}

func DefaultRules(req QueueRequest, seed string) (Rules, error) {
	ascension, err := Ascension(req.Kind, req.VisibleTiers)
	if err != nil {
		return Rules{}, err
	}
	sl := 3
	if req.Kind == QueueRanked {
		sl = 1
	}
	character := ""
	if req.TeamSize == 1 {
		if IsPlayableCharacter(req.CharacterID) {
			character = req.CharacterID
		} else {
			i, err := rand.Int(rand.Reader, big.NewInt(int64(len(Characters))))
			if err != nil {
				return Rules{}, err
			}
			character = Characters[i.Int64()]
		}
	}
	return Rules{TeamSize: req.TeamSize, Seed: seed, Ascension: ascension, TimeLimitMS: MaxMatchMilliseconds,
		EventSLLimit: sl, CombatSLLimit: sl, CharacterID: character, Modifiers: []string{}}, nil
}

// NormalizeEntertainmentRules fixes the small set of rules that are part of
// the entertainment product contract rather than user-editable options.
// Character duplication is always allowed, 1v1 uses the host's shared pick,
// and the certified race adjudicator is always used.
func NormalizeEntertainmentRules(rules Rules) Rules {
	if rules.Modifiers == nil {
		rules.Modifiers = []string{}
	}
	if len(rules.Modifiers) > 0 {
		seen := map[string]bool{}
		filtered := make([]string, 0, len(rules.Modifiers))
		for _, modifier := range rules.Modifiers {
			modifier = strings.TrimSpace(modifier)
			if modifier != "" && !seen[strings.ToLower(modifier)] {
				seen[strings.ToLower(modifier)] = true
				filtered = append(filtered, modifier)
			}
		}
		rules.Modifiers = filtered
	}
	rules.AllowDuplicateCharacters = true
	rules.CharacterPolicy = "host_for_1v1"
	rules.VictoryRule = "certified_race"
	rules.AllowSpectators = false
	if rules.SLTimerMode != "pause_on_save" {
		rules.SLTimerMode = "continuous"
	}
	if rules.SpectatorSlots < 0 {
		rules.SpectatorSlots = 0
	}
	if rules.SpectatorSlots > 8 {
		rules.SpectatorSlots = 8
	}
	if rules.CoordinationMode == "p2p" {
		rules.SpectatorSlots = 0
	}
	rules.AllowSpectators = rules.SpectatorSlots > 0
	if len(rules.SeriesSeeds) > 3 {
		rules.SeriesSeeds = append([]string(nil), rules.SeriesSeeds[:3]...)
	}
	for i := range rules.SeriesSeeds {
		rules.SeriesSeeds[i] = strings.TrimSpace(rules.SeriesSeeds[i])
	}
	return rules
}

func SeriesSeed(rules Rules, gameNumber int, fallback string) string {
	if gameNumber >= 1 && gameNumber <= len(rules.SeriesSeeds) {
		if seed := strings.TrimSpace(rules.SeriesSeeds[gameNumber-1]); seed != "" {
			return seed
		}
	}
	if gameNumber == 1 && strings.TrimSpace(rules.Seed) != "" {
		return strings.TrimSpace(rules.Seed)
	}
	return fallback
}

func IsPlayableCharacter(value string) bool {
	for _, character := range Characters {
		if character == value {
			return true
		}
	}
	return false
}

func IsLegend(tiers []string) bool {
	for _, tier := range tiers {
		if strings.EqualFold(tier, "legend") {
			return true
		}
	}
	return false
}
