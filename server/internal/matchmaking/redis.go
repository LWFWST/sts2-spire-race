package matchmaking

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/mcc/sts2-spire-race/server/internal/domain"
	"github.com/redis/go-redis/v9"
)

type Queue struct{ Client *redis.Client }

type MatchGroup struct {
	First  domain.QueueRequest
	Second domain.QueueRequest
}

func Open(ctx context.Context, redisURL string) (*Queue, error) {
	options, err := redis.ParseURL(redisURL)
	if err != nil {
		return nil, err
	}
	client := redis.NewClient(options)
	if err := client.Ping(ctx).Err(); err != nil {
		_ = client.Close()
		return nil, err
	}
	return &Queue{Client: client}, nil
}

func (q *Queue) Close() error { return q.Client.Close() }

func (q *Queue) TouchPresence(ctx context.Context, playerID string) error {
	return q.Client.Set(ctx, "race:presence:"+playerID, time.Now().UnixMilli(), 45*time.Second).Err()
}

func (q *Queue) IsOnline(ctx context.Context, playerID string) bool {
	found, err := q.Client.Exists(ctx, "race:presence:"+playerID).Result()
	return err == nil && found > 0
}

func Key(req domain.QueueRequest) string {
	character := "any"
	if req.TeamSize == 1 && domain.IsPlayableCharacter(req.CharacterID) {
		character = req.CharacterID
	}
	return fmt.Sprintf("race:queue:%s:%s:%d:%s:%s", req.GameVersion, req.Kind, req.TeamSize, req.Pool, character)
}

func (q *Queue) Join(ctx context.Context, req domain.QueueRequest) (*MatchGroup, error) {
	if req.TeamSize < 1 || req.TeamSize > 4 {
		return nil, errors.New("team size must be 1-4")
	}
	if len(req.TeamPlayerIDs) < 1 || len(req.TeamPlayerIDs) > req.TeamSize {
		return nil, errors.New("party size must be between one and the target team size")
	}
	seen := map[string]bool{}
	for _, playerID := range req.TeamPlayerIDs {
		if playerID == "" || seen[playerID] {
			return nil, errors.New("party contains an invalid player")
		}
		seen[playerID] = true
	}
	key := Key(req)
	lockKey := "race:lock:" + key
	locked, err := q.Client.SetNX(ctx, lockKey, req.PlayerID, 3*time.Second).Result()
	if err != nil {
		return nil, err
	}
	if !locked {
		return nil, errors.New("queue is busy; retry")
	}
	defer q.Client.Del(context.Background(), lockKey)

	now := time.Now().UnixMilli()
	candidates, err := q.Client.ZRangeWithScores(ctx, key, 0, -1).Result()
	if err != nil {
		return nil, err
	}
	items := make([]queuedItem, 0, len(candidates)+1)
	for _, candidate := range candidates {
		playerID, ok := candidate.Member.(string)
		if !ok || playerID == req.PlayerID {
			continue
		}
		payload, err := q.Client.Get(ctx, "race:queue:item:"+playerID).Bytes()
		if err != nil {
			continue
		}
		var other queuedItem
		if json.Unmarshal(payload, &other) != nil {
			continue
		}
		waited := now - other.EnqueuedAtMS
		band := 100 + int(waited/15000)*50
		if band > 600 {
			band = 600
		}
		if abs(req.HiddenRating-other.Request.HiddenRating) > band {
			continue
		}
		items = append(items, other)
		if len(items) >= 24 {
			break
		}
	}
	items = append(items, queuedItem{Request: req, EnqueuedAtMS: now})
	if firstItems, secondItems, ok := assemble(items, req.TeamSize, len(items)-1); ok {
		pipe := q.Client.TxPipeline()
		for _, item := range append(append([]queuedItem{}, firstItems...), secondItems...) {
			if item.Request.PlayerID == req.PlayerID {
				continue
			}
			pipe.ZRem(ctx, key, item.Request.PlayerID)
			pipe.Del(ctx, "race:queue:item:"+item.Request.PlayerID)
		}
		if _, err := pipe.Exec(ctx); err != nil {
			return nil, err
		}
		first := combine(firstItems)
		second := combine(secondItems)
		return &MatchGroup{First: first, Second: second}, nil
	}

	payload, err := json.Marshal(queuedItem{Request: req, EnqueuedAtMS: now})
	if err != nil {
		return nil, err
	}
	pipe := q.Client.TxPipeline()
	pipe.Set(ctx, "race:queue:item:"+req.PlayerID, payload, 4*time.Hour)
	pipe.ZAdd(ctx, key, redis.Z{Score: float64(req.HiddenRating), Member: req.PlayerID})
	_, err = pipe.Exec(ctx)
	return nil, err
}

func (q *Queue) Cancel(ctx context.Context, req domain.QueueRequest) error {
	pipe := q.Client.TxPipeline()
	pipe.ZRem(ctx, Key(req), req.PlayerID)
	pipe.Del(ctx, "race:queue:item:"+req.PlayerID)
	_, err := pipe.Exec(ctx)
	return err
}

type queuedItem struct {
	Request      domain.QueueRequest `json:"request"`
	EnqueuedAtMS int64               `json:"enqueued_at_ms"`
}

func assemble(items []queuedItem, teamSize, required int) ([]queuedItem, []queuedItem, bool) {
	firstIndices, ok := subset(items, teamSize, required, nil)
	if !ok {
		return nil, nil, false
	}
	excluded := map[int]bool{}
	first := make([]queuedItem, 0, len(firstIndices))
	for _, index := range firstIndices {
		excluded[index] = true
		first = append(first, items[index])
	}
	secondIndices, ok := subset(items, teamSize, -1, excluded)
	if !ok {
		// Explore alternative first-team combinations rather than committing to the first subset.
		return assembleSearch(items, teamSize, required)
	}
	second := make([]queuedItem, 0, len(secondIndices))
	for _, index := range secondIndices {
		second = append(second, items[index])
	}
	return first, second, true
}

func assembleSearch(items []queuedItem, teamSize, required int) ([]queuedItem, []queuedItem, bool) {
	var first, second []queuedItem
	var search func(int, int, []int) bool
	search = func(index, total int, selected []int) bool {
		if total == teamSize {
			contains := false
			excluded := map[int]bool{}
			for _, i := range selected {
				excluded[i] = true
				if i == required {
					contains = true
				}
			}
			if !contains {
				return false
			}
			other, ok := subset(items, teamSize, -1, excluded)
			if !ok {
				return false
			}
			for _, i := range selected {
				first = append(first, items[i])
			}
			for _, i := range other {
				second = append(second, items[i])
			}
			return true
		}
		if index >= len(items) || total > teamSize {
			return false
		}
		if search(index+1, total+len(items[index].Request.TeamPlayerIDs), append(selected, index)) {
			return true
		}
		return search(index+1, total, selected)
	}
	if search(0, 0, nil) {
		return first, second, true
	}
	return nil, nil, false
}

func subset(items []queuedItem, target, required int, excluded map[int]bool) ([]int, bool) {
	var found []int
	var search func(int, int, []int) bool
	search = func(index, total int, selected []int) bool {
		if total == target {
			if required >= 0 {
				for _, i := range selected {
					if i == required {
						found = append([]int{}, selected...)
						return true
					}
				}
				return false
			}
			found = append([]int{}, selected...)
			return true
		}
		if index >= len(items) || total > target {
			return false
		}
		if excluded != nil && excluded[index] {
			return search(index+1, total, selected)
		}
		size := len(items[index].Request.TeamPlayerIDs)
		if search(index+1, total+size, append(selected, index)) {
			return true
		}
		return search(index+1, total, selected)
	}
	ok := search(0, 0, nil)
	return found, ok
}

func combine(items []queuedItem) domain.QueueRequest {
	result := items[0].Request
	result.TeamPlayerIDs = nil
	result.VisibleTiers = nil
	result.CharacterIDs = map[string]string{}
	weightedRating := 0
	for _, item := range items {
		result.TeamPlayerIDs = append(result.TeamPlayerIDs, item.Request.TeamPlayerIDs...)
		result.VisibleTiers = append(result.VisibleTiers, item.Request.VisibleTiers...)
		weightedRating += item.Request.HiddenRating * len(item.Request.TeamPlayerIDs)
		for playerID, characterID := range item.Request.CharacterIDs {
			result.CharacterIDs[playerID] = characterID
		}
		if len(item.Request.CharacterIDs) == 0 && len(item.Request.TeamPlayerIDs) == 1 && item.Request.CharacterID != "" {
			result.CharacterIDs[item.Request.TeamPlayerIDs[0]] = item.Request.CharacterID
		}
	}
	result.HiddenRating = weightedRating / len(result.TeamPlayerIDs)
	return result
}

func abs(v int) int {
	if v < 0 {
		return -v
	}
	return v
}
