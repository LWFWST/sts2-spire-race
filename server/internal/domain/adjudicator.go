package domain

import (
	"crypto/rand"
	"fmt"
	"time"
)

func Decide(first, second Progress) (string, FinishReason, string, error) {
	firstForced := first.Outcome == OutcomeSurrendered || first.Outcome == OutcomeForfeited
	secondForced := second.Outcome == OutcomeSurrendered || second.Outcome == OutcomeForfeited
	if firstForced != secondForced {
		if firstForced {
			return second.TeamID, forcedReason(first.Outcome), "forced-loss", nil
		}
		return first.TeamID, forcedReason(second.Outcome), "forced-loss", nil
	}
	if first.FinalBossDefeated || second.FinalBossDefeated {
		if first.FinalBossDefeated != second.FinalBossDefeated {
			if first.FinalBossDefeated {
				return first.TeamID, ReasonBossCompletion, "only-finisher", nil
			}
			return second.TeamID, ReasonBossCompletion, "only-finisher", nil
		}
		if first.CompletedAtMS != nil && second.CompletedAtMS != nil && *first.CompletedAtMS != *second.CompletedAtMS {
			if *first.CompletedAtMS < *second.CompletedAtMS {
				return first.TeamID, ReasonBossCompletion, "faster-completion", nil
			}
			return second.TeamID, ReasonBossCompletion, "faster-completion", nil
		}
	}
	if first.Floor != second.Floor {
		if first.Floor > second.Floor {
			return first.TeamID, ReasonHighestFloor, "highest-floor", nil
		}
		return second.TeamID, ReasonHighestFloor, "highest-floor", nil
	}
	if first.FloorEnteredAtMS != second.FloorEnteredAtMS {
		if first.FloorEnteredAtMS < second.FloorEnteredAtMS {
			return first.TeamID, ReasonEarlierFloor, "earlier-floor-entry", nil
		}
		return second.TeamID, ReasonEarlierFloor, "earlier-floor-entry", nil
	}
	coin := []byte{0}
	if _, err := rand.Read(coin); err != nil {
		return "", "", "", err
	}
	if coin[0]&1 == 0 {
		return first.TeamID, ReasonRandomTiebreak, fmt.Sprintf("crypto-coin:%02X", coin[0]), nil
	}
	return second.TeamID, ReasonRandomTiebreak, fmt.Sprintf("crypto-coin:%02X", coin[0]), nil
}

func BuildSettlement(matchID, gameID string, first, second Progress) (Settlement, error) {
	winner, reason, audit, err := Decide(first, second)
	if err != nil {
		return Settlement{}, err
	}
	return Settlement{MatchID: matchID, GameID: gameID, WinnerTeamID: winner, Reason: reason,
		First: side(first), Second: side(second), AuditDetail: audit, CompletedAt: time.Now().UTC()}, nil
}

func RecordFloor(current Progress, floor int, enteredAtMS int64) Progress {
	if floor > current.Floor {
		current.Floor, current.FloorEnteredAtMS = floor, enteredAtMS
	}
	return current
}

func Restart(current Progress) Progress {
	current.Sequence++
	current.RestartCount++
	current.Outcome = OutcomeActive
	current.FinalBossDefeated = false
	current.CompletedAtMS = nil
	return current
}

func HasSL(current Progress, rules Rules, combat bool) bool {
	if combat {
		return current.CombatSLUsed < rules.CombatSLLimit
	}
	return current.EventSLUsed < rules.EventSLLimit
}

func side(progress Progress) SettlementSide {
	return SettlementSide{TeamID: progress.TeamID, Outcome: progress.Outcome, HighestFloor: progress.Floor,
		HighestFloorEnteredMS: progress.FloorEnteredAtMS, CompletionMS: progress.CompletedAtMS,
		RestartCount: progress.RestartCount, EventSLUsed: progress.EventSLUsed, CombatSLUsed: progress.CombatSLUsed}
}

func forcedReason(outcome Outcome) FinishReason {
	if outcome == OutcomeSurrendered {
		return ReasonSurrender
	}
	return ReasonDisconnect
}
