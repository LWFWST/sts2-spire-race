package matchmaking

import (
	"fmt"
	"testing"

	"github.com/mcc/sts2-spire-race/server/internal/domain"
)

func TestAssembleSoloPlayersIntoTeamMatches(t *testing.T) {
	for teamSize := 2; teamSize <= 4; teamSize++ {
		t.Run(fmt.Sprintf("%dv%d", teamSize, teamSize), func(t *testing.T) {
			items := make([]queuedItem, 0, teamSize*2)
			for i := 0; i < teamSize*2; i++ {
				items = append(items, queued("p"+fmt.Sprint(i)))
			}
			first, second, ok := assemble(items, teamSize, len(items)-1)
			if !ok {
				t.Fatal("expected a complete match")
			}
			if playerCount(first) != teamSize || playerCount(second) != teamSize {
				t.Fatalf("wrong team sizes: %d/%d", playerCount(first), playerCount(second))
			}
		})
	}
}

func TestAssemblePreservesPremadeParty(t *testing.T) {
	items := []queuedItem{queued("solo-a"), queuedParty("duo", "duo-b"), queued("solo-c"), queuedParty("duo-d", "duo-e")}
	first, second, ok := assemble(items, 3, len(items)-1)
	if !ok {
		t.Fatal("expected two complete teams")
	}
	if playerCount(first) != 3 || playerCount(second) != 3 {
		t.Fatal("premade parties were not assembled to exact teams")
	}
	for _, side := range [][]queuedItem{first, second} {
		for _, item := range side {
			if item.Request.PlayerID == "duo" && len(item.Request.TeamPlayerIDs) != 2 {
				t.Fatal("premade party was split")
			}
		}
	}
}

func TestAssembleWaitsUntilBothTeamsAreComplete(t *testing.T) {
	items := []queuedItem{queued("a"), queued("b"), queued("c")}
	if _, _, ok := assemble(items, 2, len(items)-1); ok {
		t.Fatal("three players must not start a 2v2")
	}
}

func queued(playerID string) queuedItem { return queuedParty(playerID) }
func queuedParty(players ...string) queuedItem {
	return queuedItem{Request: domain.QueueRequest{PlayerID: players[0], TeamSize: 4, TeamPlayerIDs: players, HiddenRating: 1500}}
}
func playerCount(items []queuedItem) int {
	total := 0
	for _, item := range items {
		total += len(item.Request.TeamPlayerIDs)
	}
	return total
}
