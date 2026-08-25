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
