package domain

import "math"

var rankedTiers = []string{"Bronze", "Silver", "Gold", "Platinum", "Diamond"}

type RankProgress struct {
	Tier     string `json:"tier"`
	Division int    `json:"division"`
	Points   int    `json:"points"`
}

func Expected(rating, opponent float64) float64 { return 1 / (1 + math.Pow(10, (opponent-rating)/400)) }

func HiddenDelta(rating, opponent float64, won bool, games int, legend bool) int {
	k := 24.0
	if games < 10 {
		k = 48
	} else if legend {
		k = 16
	}
	score := 0.0
	if won {
		score = 1
	}
	return int(math.Round(k * (score - Expected(rating, opponent))))
}

func VisibleDelta(rating, opponent float64, won bool) int {
	adjustment := int(math.Round((0.5 - Expected(rating, opponent)) * 10))
	if adjustment < -5 {
		adjustment = -5
	}
	if adjustment > 5 {
		adjustment = 5
	}
	if won {
		return 25 + adjustment
	}
	return -20 + adjustment
}

func ApplyRankProgress(current RankProgress, delta, gamesAfter, hiddenRating int) RankProgress {
	if current.Tier == "Unranked" {
		if gamesAfter < 10 {
			return current
		}
		current = placement(hiddenRating)
	}
	if current.Tier == "Legend" {
		current.Points += delta
		if current.Points < 0 {
			current.Points = 0
		}
		return current
	}
	index := tierIndex(current.Tier)
	if index < 0 {
		current = RankProgress{Tier: "Bronze", Division: 4}
		index = 0
	}
	current.Points += delta
	if current.Points >= 100 {
		current.Points -= 100
		if current.Division > 1 {
			current.Division--
		} else if index == len(rankedTiers)-1 {
			current.Tier = "Legend"
			current.Division = 0
		} else {
			current.Tier = rankedTiers[index+1]
			current.Division = 4
		}
	} else if current.Points < 0 {
		if index == 0 && current.Division == 4 {
			current.Points = 0
		} else if current.Division < 4 {
			current.Division++
			current.Points = 75
		} else {
			current.Tier = rankedTiers[index-1]
			current.Division = 1
			current.Points = 75
		}
	}
	return current
}

func placement(hidden int) RankProgress {
	switch {
	case hidden < 1300:
		return RankProgress{"Bronze", 4, 0}
	case hidden < 1450:
		return RankProgress{"Silver", 4, 0}
	case hidden < 1600:
		return RankProgress{"Gold", 4, 0}
	case hidden < 1750:
		return RankProgress{"Platinum", 4, 0}
	case hidden < 1900:
		return RankProgress{"Diamond", 4, 0}
	default:
		return RankProgress{"Legend", 0, hidden - 1500}
	}
}
func tierIndex(tier string) int {
	for i, v := range rankedTiers {
		if v == tier {
			return i
		}
	}
	return -1
}
