package storage

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/mcc/sts2-spire-race/server/internal/domain"
)

type Postgres struct{ Pool *pgxpool.Pool }

type ReplayRow struct {
	MatchID     string    `json:"match_id"`
	GameID      string    `json:"game_id"`
	PlayerID    string    `json:"player_id"`
	DisplayName string    `json:"display_name"`
	TeamID      string    `json:"team_id"`
	RunID       string    `json:"run_id"`
	CharacterID string    `json:"character_id"`
	EventCount  int       `json:"event_count"`
	Completed   bool      `json:"completed"`
	IsLive      bool      `json:"is_live"`
	IsPublic    bool      `json:"is_public"`
	UpdatedAt   time.Time `json:"updated_at"`
}

type SpectatableReplayRow struct {
	MatchID        string    `json:"match_id"`
	GameID         string    `json:"game_id"`
	PlayerID       string    `json:"player_id"`
	DisplayName    string    `json:"display_name"`
	CharacterID    string    `json:"character_id"`
	Mode           string    `json:"mode"`
	IsFriend       bool      `json:"is_friend"`
	IsLegendPublic bool      `json:"is_legend_public"`
	UpdatedAt      time.Time `json:"updated_at"`
}

func (p *Postgres) UpsertReplay(ctx context.Context, row ReplayRow, bundle []byte) (ReplayRow, error) {
	var allowed, public bool
	err := p.Pool.QueryRow(ctx, `SELECT
		EXISTS(SELECT 1 FROM match_participants WHERE match_id=$1 AND player_id=$2),
		COALESCE((SELECT kind='ranked' AND COALESCE((payload->>'legend_series')::boolean,false) FROM matches WHERE id=$1),false)`,
		row.MatchID, row.PlayerID).Scan(&allowed, &public)
	if err != nil {
		return ReplayRow{}, err
	}
	if !allowed {
		return ReplayRow{}, errors.New("player is not a participant in this match")
	}
	row.IsPublic = public
	err = p.Pool.QueryRow(ctx, `INSERT INTO race_replays(match_id,game_id,player_id,team_id,run_id,character_id,event_count,completed,is_public,bundle,completed_at)
		VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,CASE WHEN $8 THEN now() ELSE NULL END)
		ON CONFLICT(match_id,game_id,player_id) DO UPDATE SET
		team_id=excluded.team_id,run_id=excluded.run_id,character_id=excluded.character_id,
		event_count=GREATEST(race_replays.event_count,excluded.event_count),completed=race_replays.completed OR excluded.completed,
		is_public=excluded.is_public,bundle=excluded.bundle,updated_at=now(),
		completed_at=CASE WHEN race_replays.completed OR excluded.completed THEN COALESCE(race_replays.completed_at,now()) ELSE NULL END
		RETURNING updated_at,completed,is_public`, row.MatchID, row.GameID, row.PlayerID, row.TeamID, row.RunID,
		row.CharacterID, row.EventCount, row.Completed, row.IsPublic, bundle).Scan(&row.UpdatedAt, &row.Completed, &row.IsPublic)
	row.IsLive = !row.Completed
	return row, err
}

func (p *Postgres) MatchReplays(ctx context.Context, requesterID, matchID string) ([]ReplayRow, error) {
	var authorized bool
	if err := p.Pool.QueryRow(ctx, `SELECT
		EXISTS(SELECT 1 FROM match_participants WHERE match_id=$1 AND player_id=$2) OR
		EXISTS(SELECT 1 FROM entertainment_room_spectators WHERE ('fun-'||trim(code))=$1 AND player_id=$2)`,
		matchID, requesterID).Scan(&authorized); err != nil {
		return nil, err
	}
	rows, err := p.Pool.Query(ctx, `SELECT r.match_id,r.game_id,r.player_id,p.display_name,r.team_id,r.run_id,r.character_id,r.event_count,
		r.completed,NOT r.completed,r.is_public,r.updated_at
		FROM race_replays r JOIN players p ON p.id=r.player_id
		WHERE r.match_id=$1 AND ($2 OR r.is_public) ORDER BY r.game_id,r.team_id,r.player_id`, matchID, authorized)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []ReplayRow{}
	for rows.Next() {
		var value ReplayRow
		if err := rows.Scan(&value.MatchID, &value.GameID, &value.PlayerID, &value.DisplayName, &value.TeamID, &value.RunID,
			&value.CharacterID, &value.EventCount, &value.Completed, &value.IsLive, &value.IsPublic, &value.UpdatedAt); err != nil {
			return nil, err
		}
		result = append(result, value)
	}
	return result, rows.Err()
}

func (p *Postgres) ReplayBundle(ctx context.Context, requesterID, matchID, gameID, playerID string) ([]byte, error) {
	var bundle []byte
	err := p.Pool.QueryRow(ctx, `SELECT r.bundle FROM race_replays r WHERE r.match_id=$1 AND r.game_id=$2 AND r.player_id=$3 AND (
		r.player_id=$4 OR r.is_public OR EXISTS(SELECT 1 FROM match_participants mp WHERE mp.match_id=r.match_id AND mp.player_id=$4) OR
		EXISTS(SELECT 1 FROM friendships f WHERE f.state='accepted' AND ((f.requester_id=$4 AND f.addressee_id=r.player_id) OR (f.addressee_id=$4 AND f.requester_id=r.player_id))) OR
		EXISTS(SELECT 1 FROM entertainment_room_spectators s WHERE ('fun-'||trim(s.code))=r.match_id AND s.player_id=$4))`,
		matchID, gameID, playerID, requesterID).Scan(&bundle)
	return bundle, err
}

func (p *Postgres) SpectatableReplays(ctx context.Context, requesterID string) ([]SpectatableReplayRow, error) {
	rows, err := p.Pool.Query(ctx, `SELECT r.match_id,r.game_id,r.player_id,p.display_name,r.character_id,m.kind,
		EXISTS(SELECT 1 FROM friendships f WHERE f.state='accepted' AND ((f.requester_id=$1 AND f.addressee_id=r.player_id) OR (f.addressee_id=$1 AND f.requester_id=r.player_id))),
		r.is_public,r.updated_at
		FROM race_replays r JOIN players p ON p.id=r.player_id JOIN matches m ON m.id=r.match_id
		WHERE r.completed=false AND r.player_id<>$1 AND (r.is_public OR EXISTS(SELECT 1 FROM friendships f WHERE f.state='accepted' AND
		((f.requester_id=$1 AND f.addressee_id=r.player_id) OR (f.addressee_id=$1 AND f.requester_id=r.player_id))) OR
		EXISTS(SELECT 1 FROM entertainment_room_spectators s WHERE ('fun-'||trim(s.code))=r.match_id AND s.player_id=$1))
		ORDER BY r.updated_at DESC LIMIT 100`, requesterID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []SpectatableReplayRow{}
	for rows.Next() {
		var value SpectatableReplayRow
		if err := rows.Scan(&value.MatchID, &value.GameID, &value.PlayerID, &value.DisplayName, &value.CharacterID, &value.Mode,
			&value.IsFriend, &value.IsLegendPublic, &value.UpdatedAt); err != nil {
			return nil, err
		}
		result = append(result, value)
	}
	return result, rows.Err()
}

func (p *Postgres) Player(ctx context.Context, playerID string) (string, error) {
	var displayName string
	err := p.Pool.QueryRow(ctx, `SELECT display_name FROM players WHERE id=$1`, playerID).Scan(&displayName)
	return displayName, err
}

func (p *Postgres) PlayerProfile(ctx context.Context, playerID string) (displayName, favoriteCharacter string, err error) {
	err = p.Pool.QueryRow(ctx, `SELECT display_name,favorite_character FROM players WHERE id=$1`, playerID).
		Scan(&displayName, &favoriteCharacter)
	return
}

func (p *Postgres) UpdatePlayerProfile(ctx context.Context, playerID, displayName, favoriteCharacter string) error {
	tag, err := p.Pool.Exec(ctx, `UPDATE players SET display_name=$2,favorite_character=$3,updated_at=now() WHERE id=$1`,
		playerID, displayName, favoriteCharacter)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return errors.New("player not found")
	}
	return nil
}

func Open(ctx context.Context, databaseURL string) (*Postgres, error) {
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		return nil, err
	}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		return nil, err
	}
	return &Postgres{Pool: pool}, nil
}

func (p *Postgres) Close() { p.Pool.Close() }

func (p *Postgres) UpsertPlayer(ctx context.Context, id, name string) error {
	_, err := p.Pool.Exec(ctx, `INSERT INTO players(id, display_name) VALUES($1,$2)
		ON CONFLICT(id) DO UPDATE SET updated_at=now()`, id, name)
	return err
}

func (p *Postgres) ActiveRoomForPlayer(ctx context.Context, playerID string) (EntertainmentRoomSnapshot, error) {
	var code string
	err := p.Pool.QueryRow(ctx, `SELECT active.code FROM (
		SELECT m.code,m.joined_at FROM entertainment_room_members m WHERE m.player_id=$1
		UNION ALL
		SELECT s.code,s.joined_at FROM entertainment_room_spectators s WHERE s.player_id=$1
	) active JOIN entertainment_rooms r ON r.code=active.code
		WHERE r.closed_at IS NULL ORDER BY active.joined_at DESC LIMIT 1`, playerID).Scan(&code)
	if err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	return p.RoomSnapshot(ctx, code)
}

func (p *Postgres) Rating(ctx context.Context, playerID, pool string) (hidden, visible, games int, tier string, err error) {
	if _, err = p.Pool.Exec(ctx, `INSERT INTO ratings(player_id,pool) VALUES($1,$2) ON CONFLICT DO NOTHING`, playerID, pool); err != nil {
		return
	}
	err = p.Pool.QueryRow(ctx, `SELECT hidden_rating,visible_points,games_played,tier FROM ratings WHERE player_id=$1 AND pool=$2`, playerID, pool).Scan(&hidden, &visible, &games, &tier)
	return
}

type RatingProfile struct {
	HiddenRating    int
	VisiblePoints   int
	GamesPlayed     int
	Wins            int
	Losses          int
	Tier            string
	Division        int
	LeaderboardRank int
}

func (p *Postgres) RatingProfile(ctx context.Context, playerID, pool string) (RatingProfile, error) {
	var rp RatingProfile
	if _, err := p.Pool.Exec(ctx, `INSERT INTO ratings(player_id,pool) VALUES($1,$2) ON CONFLICT DO NOTHING`, playerID, pool); err != nil {
		return rp, err
	}
	if err := p.Pool.QueryRow(ctx, `SELECT hidden_rating,visible_points,games_played,wins,losses,tier,division FROM ratings WHERE player_id=$1 AND pool=$2`,
		playerID, pool).Scan(&rp.HiddenRating, &rp.VisiblePoints, &rp.GamesPlayed, &rp.Wins, &rp.Losses, &rp.Tier, &rp.Division); err != nil {
		return rp, err
	}
	err := p.Pool.QueryRow(ctx, `SELECT rn FROM (
		SELECT player_id, row_number() OVER(ORDER BY visible_points DESC,hidden_rating DESC,player_id) AS rn FROM ratings WHERE pool=$1
	) ranked WHERE player_id=$2`, pool, playerID).Scan(&rp.LeaderboardRank)
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return rp, err
	}
	return rp, nil
}

type HistoryEntry struct {
	MatchID              string              `json:"match_id"`
	Kind                 string              `json:"kind"`
	TeamSize             int                 `json:"team_size"`
	Victory              bool                `json:"victory"`
	RunTimeMS            int64               `json:"run_time_ms"`
	Completed            bool                `json:"completed"`
	HighestFloor         int                 `json:"highest_floor"`
	Character            string              `json:"character"`
	OpponentRunTimeMS    int64               `json:"opponent_run_time_ms"`
	OpponentCompleted    bool                `json:"opponent_completed"`
	OpponentHighestFloor int                 `json:"opponent_highest_floor"`
	OpponentNames        []string            `json:"opponent_names"`
	OpponentCharacters   []string            `json:"opponent_characters"`
	SeriesGames          []domain.LegendGame `json:"series_games,omitempty"`
	LocalTeamID          string              `json:"local_team_id"`
	PlayedAt             time.Time           `json:"played_at"`
	RatingDelta          int                 `json:"rating_delta"`
}

func (p *Postgres) History(ctx context.Context, playerID string, limit int) ([]HistoryEntry, error) {
	rows, err := p.Pool.Query(ctx, `SELECT m.id,m.kind,m.team_size,m.winner_team_id,m.completed_at,
		COALESCE(m.settlement->'first'->>'team_id','') AS first_team,
		COALESCE(m.settlement->'first'->>'outcome','') AS first_outcome,
		COALESCE((m.settlement->'first'->>'highest_floor')::int,0) AS first_floor,
		NULLIF(m.settlement->'first'->>'highest_floor_entered_ms','null') AS first_floor_ms,
		NULLIF(m.settlement->'first'->>'completion_ms','null') AS first_completion_ms,
		COALESCE(m.settlement->'second'->>'team_id','') AS second_team,
		COALESCE(m.settlement->'second'->>'outcome','') AS second_outcome,
		COALESCE((m.settlement->'second'->>'highest_floor')::int,0) AS second_floor,
		NULLIF(m.settlement->'second'->>'highest_floor_entered_ms','null') AS second_floor_ms,
		NULLIF(m.settlement->'second'->>'completion_ms','null') AS second_completion_ms,
		COALESCE(m.payload->'character_ids'->>$1,m.payload->'rules'->>'character_id','') AS character_id,
		COALESCE(m.settlement->'series_games','[]'::jsonb) AS series_games,
		ARRAY(SELECT COALESCE(NULLIF(p2.display_name,''),mp2.player_id)
			FROM match_participants mp2 LEFT JOIN players p2 ON p2.id=mp2.player_id
			WHERE mp2.match_id=m.id AND mp2.team_id<>mp.team_id ORDER BY mp2.player_id) AS opponent_names,
		ARRAY(SELECT COALESCE(m.payload->'character_ids'->>mp2.player_id,m.payload->'rules'->>'character_id','')
			FROM match_participants mp2 WHERE mp2.match_id=m.id AND mp2.team_id<>mp.team_id ORDER BY mp2.player_id) AS opponent_characters,
		COALESCE(mp.rating_delta,0),
		mp.team_id
		FROM matches m JOIN match_participants mp ON mp.match_id=m.id
		WHERE mp.player_id=$1 AND m.state='completed' AND m.winner_team_id IS NOT NULL
		ORDER BY m.completed_at DESC NULLS LAST LIMIT $2`, playerID, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []HistoryEntry{}
	for rows.Next() {
		var e HistoryEntry
		var winnerTeamID, firstTeam, secondTeam, firstOutcome, secondOutcome string
		var firstFloor, secondFloor int
		var firstFloorMs, firstCompletionMs, secondFloorMs, secondCompletionMs *string
		var completedAt *time.Time
		var playerTeam string
		var seriesPayload []byte
		if err := rows.Scan(&e.MatchID, &e.Kind, &e.TeamSize, &winnerTeamID, &completedAt,
			&firstTeam, &firstOutcome, &firstFloor, &firstFloorMs, &firstCompletionMs,
			&secondTeam, &secondOutcome, &secondFloor, &secondFloorMs, &secondCompletionMs,
			&e.Character, &seriesPayload, &e.OpponentNames, &e.OpponentCharacters, &e.RatingDelta, &playerTeam); err != nil {
			return nil, err
		}
		if err := json.Unmarshal(seriesPayload, &e.SeriesGames); err != nil {
			return nil, err
		}
		if len(e.SeriesGames) > 0 {
			e.Character = e.SeriesGames[0].CharacterID
			e.OpponentCharacters = []string{}
			seenCharacters := map[string]bool{}
			for _, game := range e.SeriesGames {
				if game.CharacterID != "" && !seenCharacters[game.CharacterID] {
					seenCharacters[game.CharacterID] = true
					e.OpponentCharacters = append(e.OpponentCharacters, game.CharacterID)
				}
			}
		}
		e.Victory = playerTeam == winnerTeamID && winnerTeamID != ""
		e.LocalTeamID = playerTeam
		if playerTeam == firstTeam {
			e.Completed, e.HighestFloor = firstOutcome == string(domain.OutcomeFinished), firstFloor
			e.OpponentCompleted, e.OpponentHighestFloor = secondOutcome == string(domain.OutcomeFinished), secondFloor
			e.RunTimeMS = firstNonNullMilliseconds(firstCompletionMs, firstFloorMs)
			e.OpponentRunTimeMS = firstNonNullMilliseconds(secondCompletionMs, secondFloorMs)
		} else if playerTeam == secondTeam {
			e.Completed, e.HighestFloor = secondOutcome == string(domain.OutcomeFinished), secondFloor
			e.OpponentCompleted, e.OpponentHighestFloor = firstOutcome == string(domain.OutcomeFinished), firstFloor
			e.RunTimeMS = firstNonNullMilliseconds(secondCompletionMs, secondFloorMs)
			e.OpponentRunTimeMS = firstNonNullMilliseconds(firstCompletionMs, firstFloorMs)
		}
		if completedAt != nil {
			e.PlayedAt = *completedAt
		}
		result = append(result, e)
	}
	return result, rows.Err()
}

func firstNonNullMilliseconds(values ...*string) int64 {
	for _, value := range values {
		if value != nil {
			if parsed, err := parseInt64(*value); err == nil {
				return parsed
			}
		}
	}
	return 0
}

func (p *Postgres) BestTime(ctx context.Context, playerID string) (int64, error) {
	var ms int64
	err := p.Pool.QueryRow(ctx, `SELECT COALESCE(MIN(ms),0) FROM (
		SELECT CASE
			WHEN COALESCE(m.settlement->'first'->>'team_id','')=mp.team_id THEN NULLIF(m.settlement->'first'->>'completion_ms','null')
			WHEN COALESCE(m.settlement->'second'->>'team_id','')=mp.team_id THEN NULLIF(m.settlement->'second'->>'completion_ms','null')
			ELSE NULL
		END::bigint AS ms
		FROM matches m JOIN match_participants mp ON mp.match_id=m.id
		WHERE m.state='completed' AND mp.player_id=$1 AND mp.team_id=m.winner_team_id
	) t WHERE ms IS NOT NULL AND ms > 0`, playerID).Scan(&ms)
	if err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return 0, err
	}
	return ms, nil
}

func parseInt64(value string) (int64, error) {
	var result int64
	_, err := fmt.Sscan(value, &result)
	return result, err
}

type LeaderboardRow struct {
	Position    int    `json:"position"`
	PlayerID    string `json:"player_id"`
	DisplayName string `json:"display_name"`
	Tier        string `json:"tier"`
	Rating      int    `json:"rating"`
	Wins        int    `json:"wins"`
	Losses      int    `json:"losses"`
	BestTimeMS  int64  `json:"best_time_ms"`
}

func (p *Postgres) Leaderboard(ctx context.Context, pool string, limit int, viewerID string, friendsOnly bool) ([]LeaderboardRow, error) {
	rows, err := p.Pool.Query(ctx, `SELECT row_number() OVER(ORDER BY r.visible_points DESC,r.hidden_rating DESC,p.id),p.id,p.display_name,r.tier,r.visible_points,r.wins,r.losses,
		COALESCE((SELECT MIN((CASE
			WHEN COALESCE(m.settlement->'first'->>'team_id','')=mp.team_id THEN NULLIF(m.settlement->'first'->>'completion_ms','null')
			WHEN COALESCE(m.settlement->'second'->>'team_id','')=mp.team_id THEN NULLIF(m.settlement->'second'->>'completion_ms','null')
			ELSE NULL END)::bigint)
			FROM matches m JOIN match_participants mp ON mp.match_id=m.id
			WHERE m.state='completed' AND mp.player_id=p.id AND mp.team_id=m.winner_team_id),0)
		FROM ratings r JOIN players p ON p.id=r.player_id WHERE r.pool=$1 AND (NOT $4 OR EXISTS(
			SELECT 1 FROM friendships f WHERE f.state='accepted' AND ((f.requester_id=$3 AND f.addressee_id=p.id) OR (f.addressee_id=$3 AND f.requester_id=p.id))))
		ORDER BY r.visible_points DESC,r.hidden_rating DESC,p.id LIMIT $2`, pool, limit, viewerID, friendsOnly)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []LeaderboardRow{}
	for rows.Next() {
		var r LeaderboardRow
		if err := rows.Scan(&r.Position, &r.PlayerID, &r.DisplayName, &r.Tier, &r.Rating, &r.Wins, &r.Losses, &r.BestTimeMS); err != nil {
			return nil, err
		}
		result = append(result, r)
	}
	return result, rows.Err()
}

type SocialRow struct {
	PlayerID     string `json:"player_id"`
	DisplayName  string `json:"display_name"`
	Relationship string `json:"relationship"`
	Tier         string `json:"tier"`
}

func (p *Postgres) Friends(ctx context.Context, playerID string) ([]SocialRow, error) {
	rows, err := p.Pool.Query(ctx, `WITH links AS (
		SELECT CASE WHEN requester_id=$1 THEN addressee_id ELSE requester_id END AS friend_id,
		CASE WHEN state='accepted' THEN 'accepted' WHEN addressee_id=$1 THEN 'incoming' ELSE 'outgoing' END AS relationship
		FROM friendships WHERE requester_id=$1 OR addressee_id=$1
	) SELECT p.id,p.display_name,l.relationship,COALESCE(r.tier,'Unranked')
	FROM links l JOIN players p ON p.id=l.friend_id
	LEFT JOIN ratings r ON r.player_id=p.id AND r.pool='solo'
	ORDER BY l.relationship,p.display_name`, playerID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []SocialRow{}
	for rows.Next() {
		var row SocialRow
		if err := rows.Scan(&row.PlayerID, &row.DisplayName, &row.Relationship, &row.Tier); err != nil {
			return nil, err
		}
		result = append(result, row)
	}
	return result, rows.Err()
}

func (p *Postgres) SearchPlayers(ctx context.Context, playerID, query string, limit int) ([]SocialRow, error) {
	rows, err := p.Pool.Query(ctx, `SELECT p.id,p.display_name,'search',COALESCE(r.tier,'Unranked')
	FROM players p LEFT JOIN ratings r ON r.player_id=p.id AND r.pool='solo'
	WHERE p.id<>$1 AND (p.display_name ILIKE '%' || $2 || '%' OR p.id ILIKE '%' || $2 || '%')
	AND NOT EXISTS (SELECT 1 FROM friendships f WHERE (f.requester_id=$1 AND f.addressee_id=p.id) OR (f.requester_id=p.id AND f.addressee_id=$1))
	ORDER BY p.display_name LIMIT $3`, playerID, query, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []SocialRow{}
	for rows.Next() {
		var row SocialRow
		if err := rows.Scan(&row.PlayerID, &row.DisplayName, &row.Relationship, &row.Tier); err != nil {
			return nil, err
		}
		result = append(result, row)
	}
	return result, rows.Err()
}

func (p *Postgres) RequestFriend(ctx context.Context, requester, addressee string) error {
	if requester == addressee {
		return errors.New("cannot add yourself")
	}
	var exists bool
	if err := p.Pool.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM players WHERE id=$1)`, addressee).Scan(&exists); err != nil {
		return err
	}
	if !exists {
		return errors.New("player not found")
	}
	tag, err := p.Pool.Exec(ctx, `INSERT INTO friendships(requester_id,addressee_id,state)
		SELECT $1,$2,'pending' WHERE NOT EXISTS(
			SELECT 1 FROM friendships WHERE (requester_id=$1 AND addressee_id=$2) OR (requester_id=$2 AND addressee_id=$1))`, requester, addressee)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return errors.New("friend relationship already exists")
	}
	return nil
}

func (p *Postgres) AcceptFriend(ctx context.Context, playerID, requester string) error {
	tag, err := p.Pool.Exec(ctx, `UPDATE friendships SET state='accepted',updated_at=now()
		WHERE requester_id=$2 AND addressee_id=$1 AND state='pending'`, playerID, requester)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return errors.New("friend request not found")
	}
	return nil
}

func (p *Postgres) DeclineFriend(ctx context.Context, playerID, requester string) error {
	tag, err := p.Pool.Exec(ctx, `DELETE FROM friendships WHERE requester_id=$2 AND addressee_id=$1 AND state='pending'`, playerID, requester)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return errors.New("friend request not found")
	}
	return nil
}

func (p *Postgres) RemoveFriend(ctx context.Context, playerID, other string) error {
	_, err := p.Pool.Exec(ctx, `DELETE FROM friendships WHERE (requester_id=$1 AND addressee_id=$2) OR (requester_id=$2 AND addressee_id=$1)`, playerID, other)
	return err
}

func (p *Postgres) SaveMatch(ctx context.Context, a domain.Assignment) error {
	payload, _ := json.Marshal(a)
	tx, err := p.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	if _, err = tx.Exec(ctx, `INSERT INTO matches(id,game_version,kind,team_size,state,payload,started_at)
		VALUES($1,$2,$3,$4,'ready_check',$5,NULL) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload`,
		a.MatchID, a.GameVersion, a.Kind, a.TeamSize, payload); err != nil {
		return err
	}
	for _, side := range []struct {
		teamID  string
		players []string
	}{{a.FirstTeamID, a.FirstPlayerIDs}, {a.SecondTeamID, a.SecondPlayerIDs}} {
		for _, playerID := range side.players {
			if _, err = tx.Exec(ctx, `INSERT INTO match_participants(match_id,player_id,team_id,rating_before,rating_delta)
				VALUES($1,$2,$3,1500,0) ON CONFLICT(match_id,player_id) DO NOTHING`, a.MatchID, playerID, side.teamID); err != nil {
				return err
			}
		}
	}
	return tx.Commit(ctx)
}

func (p *Postgres) StartMatch(ctx context.Context, id string, startedAt time.Time) error {
	_, err := p.Pool.Exec(ctx, `UPDATE matches SET state='running',started_at=$2 WHERE id=$1`, id, startedAt)
	return err
}

func (p *Postgres) SaveSettlement(ctx context.Context, s domain.Settlement) error {
	payload, _ := json.Marshal(s)
	_, err := p.Pool.Exec(ctx, `UPDATE matches SET state='completed',completed_at=$2,winner_team_id=$3,finish_reason=$4,settlement=$5 WHERE id=$1`,
		s.MatchID, s.CompletedAt, s.WinnerTeamID, s.Reason, payload)
	return err
}

func (p *Postgres) ApplyRatings(ctx context.Context, a domain.Assignment, winnerTeamID string) (map[string]int, error) {
	pool := "team"
	if a.TeamSize == 1 {
		pool = "solo"
	}
	if a.Kind == domain.QueueCasual {
		pool = "casual_" + pool
	}
	all := append(append([]string{}, a.FirstPlayerIDs...), a.SecondPlayerIDs...)
	for _, id := range all {
		if _, err := p.Pool.Exec(ctx, `INSERT INTO players(id,display_name) VALUES($1,$1) ON CONFLICT DO NOTHING`, id); err != nil {
			return nil, err
		}
		if _, err := p.Pool.Exec(ctx, `INSERT INTO ratings(player_id,pool) VALUES($1,$2) ON CONFLICT DO NOTHING`, id, pool); err != nil {
			return nil, err
		}
	}
	firstRatings, err := p.ratingRows(ctx, a.FirstPlayerIDs, pool)
	if err != nil {
		return nil, err
	}
	secondRatings, err := p.ratingRows(ctx, a.SecondPlayerIDs, pool)
	if err != nil {
		return nil, err
	}
	firstAverage, secondAverage := averageRating(firstRatings), averageRating(secondRatings)
	deltas := map[string]int{}
	tx, err := p.Pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)
	for _, side := range []struct {
		rows     []ratingRow
		team     string
		opponent float64
	}{{firstRatings, a.FirstTeamID, secondAverage}, {secondRatings, a.SecondTeamID, firstAverage}} {
		won := side.team == winnerTeamID
		for _, row := range side.rows {
			hiddenDelta := domain.HiddenDelta(float64(row.Hidden), side.opponent, won, row.Games, a.LegendSeries)
			visibleDelta := 0
			progress := domain.RankProgress{Tier: row.Tier, Division: row.Division, Points: row.Visible}
			if a.Kind == domain.QueueRanked {
				visibleDelta = domain.VisibleDelta(float64(row.Hidden), side.opponent, won)
				progress = domain.ApplyRankProgress(progress, visibleDelta, row.Games+1, row.Hidden+hiddenDelta)
				deltas[row.PlayerID] = visibleDelta
			}
			wins, losses := 0, 1
			if won {
				wins, losses = 1, 0
			}
			if _, err := tx.Exec(ctx, `UPDATE ratings SET hidden_rating=$3,visible_points=$4,games_played=games_played+1,wins=wins+$5,losses=losses+$6,tier=$7,division=$8 WHERE player_id=$1 AND pool=$2`, row.PlayerID, pool, row.Hidden+hiddenDelta, progress.Points, wins, losses, progress.Tier, progress.Division); err != nil {
				return nil, err
			}
			if _, err := tx.Exec(ctx, `INSERT INTO match_participants(match_id,player_id,team_id,rating_before,rating_delta)
				VALUES($1,$2,$3,$4,$5) ON CONFLICT(match_id,player_id) DO UPDATE
				SET team_id=excluded.team_id,rating_before=excluded.rating_before,rating_delta=excluded.rating_delta`, a.MatchID, row.PlayerID, side.team, row.Hidden, deltas[row.PlayerID]); err != nil {
				return nil, err
			}
		}
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return deltas, nil
}

type ratingRow struct {
	PlayerID                         string
	Hidden, Visible, Games, Division int
	Tier                             string
}

func (p *Postgres) ratingRows(ctx context.Context, ids []string, pool string) ([]ratingRow, error) {
	result := make([]ratingRow, 0, len(ids))
	for _, id := range ids {
		var r ratingRow
		r.PlayerID = id
		if err := p.Pool.QueryRow(ctx, `SELECT hidden_rating,visible_points,games_played,division,tier FROM ratings WHERE player_id=$1 AND pool=$2`, id, pool).Scan(&r.Hidden, &r.Visible, &r.Games, &r.Division, &r.Tier); err != nil {
			return nil, err
		}
		result = append(result, r)
	}
	return result, nil
}
func averageRating(rows []ratingRow) float64 {
	if len(rows) == 0 {
		return 1500
	}
	total := 0
	for _, r := range rows {
		total += r.Hidden
	}
	return float64(total) / float64(len(rows))
}

func (p *Postgres) CreateRoom(ctx context.Context, code, host string, rules domain.Rules) error {
	payload, _ := json.Marshal(rules)
	mode := rules.CoordinationMode
	if mode != "p2p" {
		mode = "server"
	}
	tx, err := p.Pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	if _, err = tx.Exec(ctx, `INSERT INTO entertainment_rooms(code,host_player_id,rules,coordination_mode) VALUES($1,$2,$3,$4)`, code, host, payload, mode); err != nil {
		return err
	}
	if _, err = tx.Exec(ctx, `INSERT INTO entertainment_room_members(code,player_id,team) VALUES($1,$2,1)`, code, host); err != nil {
		return err
	}
	return tx.Commit(ctx)
}

func (p *Postgres) Room(ctx context.Context, code string) (string, domain.Rules, error) {
	var host string
	var payload []byte
	err := p.Pool.QueryRow(ctx, `SELECT host_player_id,rules FROM entertainment_rooms WHERE code=$1 AND closed_at IS NULL`, code).Scan(&host, &payload)
	var rules domain.Rules
	if err == nil {
		err = json.Unmarshal(payload, &rules)
	}
	return host, rules, err
}

type EntertainmentRoomMember struct {
	PlayerID    string `json:"player_id"`
	DisplayName string `json:"display_name"`
	Team        int    `json:"team"`
	IsHost      bool   `json:"is_host"`
	IsReady     bool   `json:"is_ready"`
	CharacterID string `json:"character_id"`
}

type EntertainmentRoomSpectator struct {
	PlayerID     string `json:"player_id"`
	DisplayName  string `json:"display_name"`
	WatchingTeam int    `json:"watching_team"`
}

type EntertainmentRoomSnapshot struct {
	Code             string                       `json:"code"`
	HostPlayerID     string                       `json:"host_player_id"`
	Rules            domain.Rules                 `json:"rules"`
	Members          []EntertainmentRoomMember    `json:"members"`
	Spectators       []EntertainmentRoomSpectator `json:"spectators"`
	CreatedAt        time.Time                    `json:"created_at"`
	CoordinationMode string                       `json:"coordination_mode"`
	State            string                       `json:"state"`
	StartedAt        *time.Time                   `json:"started_at,omitempty"`
}

func (p *Postgres) RoomSnapshot(ctx context.Context, code string) (EntertainmentRoomSnapshot, error) {
	var room EntertainmentRoomSnapshot
	var payload []byte
	err := p.Pool.QueryRow(ctx, `SELECT code,host_player_id,rules,created_at,coordination_mode,state,started_at FROM entertainment_rooms WHERE code=$1 AND closed_at IS NULL`, code).
		Scan(&room.Code, &room.HostPlayerID, &payload, &room.CreatedAt, &room.CoordinationMode, &room.State, &room.StartedAt)
	if err != nil {
		return room, err
	}
	if err = json.Unmarshal(payload, &room.Rules); err != nil {
		return room, err
	}
	rows, err := p.Pool.Query(ctx, `SELECT m.player_id,p.display_name,m.team,(m.player_id=$2),m.is_ready,m.character_id
		FROM entertainment_room_members m JOIN players p ON p.id=m.player_id
		WHERE m.code=$1 ORDER BY m.team,m.joined_at`, code, room.HostPlayerID)
	if err != nil {
		return room, err
	}
	defer rows.Close()
	room.Members = []EntertainmentRoomMember{}
	for rows.Next() {
		var member EntertainmentRoomMember
		if err = rows.Scan(&member.PlayerID, &member.DisplayName, &member.Team, &member.IsHost, &member.IsReady, &member.CharacterID); err != nil {
			return room, err
		}
		room.Members = append(room.Members, member)
	}
	if err = rows.Err(); err != nil {
		return room, err
	}
	spectatorRows, err := p.Pool.Query(ctx, `SELECT s.player_id,p.display_name,s.watching_team
		FROM entertainment_room_spectators s JOIN players p ON p.id=s.player_id
		WHERE s.code=$1 ORDER BY s.joined_at`, code)
	if err != nil {
		return room, err
	}
	defer spectatorRows.Close()
	room.Spectators = []EntertainmentRoomSpectator{}
	for spectatorRows.Next() {
		var spectator EntertainmentRoomSpectator
		if err = spectatorRows.Scan(&spectator.PlayerID, &spectator.DisplayName, &spectator.WatchingTeam); err != nil {
			return room, err
		}
		room.Spectators = append(room.Spectators, spectator)
	}
	return room, spectatorRows.Err()
}

func (p *Postgres) JoinRoomSpectator(ctx context.Context, code, playerID string) (EntertainmentRoomSnapshot, error) {
	tx, err := p.Pool.Begin(ctx)
	if err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	defer tx.Rollback(ctx)
	var payload []byte
	var mode string
	if err = tx.QueryRow(ctx, `SELECT rules,coordination_mode FROM entertainment_rooms
		WHERE code=$1 AND closed_at IS NULL FOR UPDATE`, code).Scan(&payload, &mode); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	var rules domain.Rules
	if err = json.Unmarshal(payload, &rules); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if mode == "p2p" {
		return EntertainmentRoomSnapshot{}, errors.New("Steam P2P rooms do not provide server spectator seats")
	}
	if rules.SpectatorSlots <= 0 {
		return EntertainmentRoomSnapshot{}, errors.New("this room has no spectator seats")
	}
	var member bool
	if err = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM entertainment_room_members WHERE code=$1 AND player_id=$2)`, code, playerID).Scan(&member); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if member {
		return EntertainmentRoomSnapshot{}, errors.New("players cannot also occupy a spectator seat")
	}
	var alreadySpectating bool
	if err = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM entertainment_room_spectators WHERE code=$1 AND player_id=$2)`, code, playerID).Scan(&alreadySpectating); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if alreadySpectating {
		if err = tx.Commit(ctx); err != nil {
			return EntertainmentRoomSnapshot{}, err
		}
		return p.RoomSnapshot(ctx, code)
	}
	var count int
	if err = tx.QueryRow(ctx, `SELECT count(*) FROM entertainment_room_spectators WHERE code=$1`, code).Scan(&count); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if count >= rules.SpectatorSlots {
		return EntertainmentRoomSnapshot{}, errors.New("spectator seats are full")
	}
	if _, err = tx.Exec(ctx, `INSERT INTO entertainment_room_spectators(code,player_id,watching_team)
		VALUES($1,$2,1) ON CONFLICT(code,player_id) DO NOTHING`, code, playerID); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if err = tx.Commit(ctx); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	return p.RoomSnapshot(ctx, code)
}

func (p *Postgres) SetRoomSpectatorTarget(ctx context.Context, code, playerID string, team int) (EntertainmentRoomSnapshot, error) {
	if team != 1 && team != 2 {
		return EntertainmentRoomSnapshot{}, errors.New("spectator target must be team 1 or team 2")
	}
	tag, err := p.Pool.Exec(ctx, `UPDATE entertainment_room_spectators SET watching_team=$3
		WHERE code=$1 AND player_id=$2`, code, playerID, team)
	if err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if tag.RowsAffected() == 0 {
		return EntertainmentRoomSnapshot{}, errors.New("player is not spectating this room")
	}
	return p.RoomSnapshot(ctx, code)
}

func (p *Postgres) LeaveRoomSpectator(ctx context.Context, code, playerID string) error {
	_, err := p.Pool.Exec(ctx, `DELETE FROM entertainment_room_spectators WHERE code=$1 AND player_id=$2`, code, playerID)
	return err
}

func (p *Postgres) JoinRoom(ctx context.Context, code, playerID string) (EntertainmentRoomSnapshot, error) {
	room, err := p.RoomSnapshot(ctx, code)
	if err != nil {
		return room, err
	}
	for _, member := range room.Members {
		if member.PlayerID == playerID {
			return room, nil
		}
	}
	first, second := 0, 0
	for _, member := range room.Members {
		if member.Team == 1 {
			first++
		} else {
			second++
		}
	}
	team := 1
	if first > second {
		team = 2
	}
	if (team == 1 && first >= room.Rules.TeamSize) || (team == 2 && second >= room.Rules.TeamSize) {
		if team == 1 && second < room.Rules.TeamSize {
			team = 2
		} else if team == 2 && first < room.Rules.TeamSize {
			team = 1
		} else {
			return room, errors.New("room is full")
		}
	}
	if _, err = p.Pool.Exec(ctx, `DELETE FROM entertainment_room_spectators WHERE code=$1 AND player_id=$2`, code, playerID); err != nil {
		return room, err
	}
	if _, err = p.Pool.Exec(ctx, `INSERT INTO entertainment_room_members(code,player_id,team) VALUES($1,$2,$3)`, code, playerID, team); err != nil {
		return room, err
	}
	return p.RoomSnapshot(ctx, code)
}

func (p *Postgres) UpdateRoomRules(ctx context.Context, code, playerID string, rules domain.Rules) (EntertainmentRoomSnapshot, error) {
	payload, _ := json.Marshal(rules)
	mode := rules.CoordinationMode
	if mode != "p2p" {
		mode = "server"
	}
	tx, err := p.Pool.Begin(ctx)
	if err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	defer tx.Rollback(ctx)
	var overfull bool
	if err = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM entertainment_room_members WHERE code=$1 GROUP BY team HAVING count(*)>$2)`, code, rules.TeamSize).Scan(&overfull); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if overfull {
		return EntertainmentRoomSnapshot{}, errors.New("remove players before reducing team size")
	}
	tag, err := tx.Exec(ctx, `UPDATE entertainment_rooms SET rules=$3,coordination_mode=$4 WHERE code=$1 AND host_player_id=$2 AND closed_at IS NULL AND state='waiting'`, code, playerID, payload, mode)
	if err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if tag.RowsAffected() == 0 {
		return EntertainmentRoomSnapshot{}, errors.New("only the room host may change rules")
	}
	if _, err = tx.Exec(ctx, `UPDATE entertainment_room_members SET is_ready=false WHERE code=$1`, code); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if err = tx.Commit(ctx); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	return p.RoomSnapshot(ctx, code)
}

func (p *Postgres) SwitchRoomTeam(ctx context.Context, code, playerID string) (EntertainmentRoomSnapshot, error) {
	room, err := p.RoomSnapshot(ctx, code)
	if err != nil {
		return room, err
	}
	current := 0
	for _, member := range room.Members {
		if member.PlayerID == playerID {
			current = member.Team
		}
	}
	if current == 0 {
		return room, errors.New("player is not in this room")
	}
	target, count := 3-current, 0
	for _, member := range room.Members {
		if member.Team == target {
			count++
		}
	}
	if count >= room.Rules.TeamSize {
		// A full room can still exchange sides. Swap with the most recently
		// joined member on the target team so both teams remain full.
		var swapPlayer string
		for i := len(room.Members) - 1; i >= 0; i-- {
			if room.Members[i].Team == target && room.Members[i].PlayerID != playerID {
				swapPlayer = room.Members[i].PlayerID
				break
			}
		}
		if swapPlayer == "" {
			return room, errors.New("target team is full")
		}
		tx, txErr := p.Pool.Begin(ctx)
		if txErr != nil {
			return room, txErr
		}
		defer tx.Rollback(ctx)
		if _, err = tx.Exec(ctx, `UPDATE entertainment_room_members SET team=$3,is_ready=false WHERE code=$1 AND player_id=$2`, code, playerID, target); err != nil {
			return room, err
		}
		if _, err = tx.Exec(ctx, `UPDATE entertainment_room_members SET team=$3,is_ready=false WHERE code=$1 AND player_id=$2`, code, swapPlayer, 3-target); err != nil {
			return room, err
		}
		if err = tx.Commit(ctx); err != nil {
			return room, err
		}
	} else if _, err = p.Pool.Exec(ctx, `UPDATE entertainment_room_members SET team=$3,is_ready=false WHERE code=$1 AND player_id=$2`, code, playerID, target); err != nil {
		return room, err
	}
	return p.RoomSnapshot(ctx, code)
}

func (p *Postgres) SetRoomMember(ctx context.Context, code, playerID, characterID string, ready bool) (EntertainmentRoomSnapshot, error) {
	if !domain.IsPlayableCharacter(characterID) {
		return EntertainmentRoomSnapshot{}, errors.New("unsupported character")
	}
	room, err := p.RoomSnapshot(ctx, code)
	if err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if room.State != "waiting" {
		return EntertainmentRoomSnapshot{}, errors.New("room has already started")
	}
	var tag pgconn.CommandTag
	if room.Rules.TeamSize == 1 {
		if room.HostPlayerID != playerID {
			for _, member := range room.Members {
				if member.PlayerID == room.HostPlayerID {
					characterID = member.CharacterID
					break
				}
			}
		}
		tx, beginErr := p.Pool.Begin(ctx)
		if beginErr != nil {
			return EntertainmentRoomSnapshot{}, beginErr
		}
		defer tx.Rollback(ctx)
		if _, err = tx.Exec(ctx, `UPDATE entertainment_room_members
			SET is_ready=CASE WHEN character_id<>$2 THEN false ELSE is_ready END,character_id=$2 WHERE code=$1`, code, characterID); err != nil {
			return EntertainmentRoomSnapshot{}, err
		}
		tag, err = tx.Exec(ctx, `UPDATE entertainment_room_members SET is_ready=$3 WHERE code=$1 AND player_id=$2`, code, playerID, ready)
		if err == nil {
			err = tx.Commit(ctx)
		}
	} else {
		tag, err = p.Pool.Exec(ctx, `UPDATE entertainment_room_members SET character_id=$3,is_ready=$4
			WHERE code=$1 AND player_id=$2`, code, playerID, characterID, ready)
	}
	if err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if tag.RowsAffected() == 0 {
		return EntertainmentRoomSnapshot{}, errors.New("player is not in this room")
	}
	return p.RoomSnapshot(ctx, code)
}

func (p *Postgres) StartRoom(ctx context.Context, code, playerID, sharedSeed string) (EntertainmentRoomSnapshot, error) {
	tx, err := p.Pool.Begin(ctx)
	if err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	defer tx.Rollback(ctx)
	var host string
	var payload []byte
	var state string
	if err = tx.QueryRow(ctx, `SELECT host_player_id,rules,state FROM entertainment_rooms
		WHERE code=$1 AND closed_at IS NULL FOR UPDATE`, code).Scan(&host, &payload, &state); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if host != playerID {
		return EntertainmentRoomSnapshot{}, errors.New("only the room host may start")
	}
	if state != "waiting" {
		return EntertainmentRoomSnapshot{}, errors.New("room has already started")
	}
	var rules domain.Rules
	if err = json.Unmarshal(payload, &rules); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	rules = domain.NormalizeEntertainmentRules(rules)
	if rules.RandomSeed {
		rules.Seed = sharedSeed
		if rules.BestOf == 3 {
			rules.SeriesSeeds = []string{sharedSeed, sharedSeed + "-2", sharedSeed + "-3"}
		}
	}
	if rules.BestOf == 3 {
		for len(rules.SeriesSeeds) < 3 {
			rules.SeriesSeeds = append(rules.SeriesSeeds, "")
		}
		if strings.TrimSpace(rules.SeriesSeeds[0]) == "" && strings.TrimSpace(rules.Seed) != "" {
			rules.SeriesSeeds[0] = strings.TrimSpace(rules.Seed)
		}
		for i := range rules.SeriesSeeds {
			if strings.TrimSpace(rules.SeriesSeeds[i]) == "" {
				rules.SeriesSeeds[i] = fmt.Sprintf("%s-%d", sharedSeed, i+1)
			}
		}
		rules.Seed = rules.SeriesSeeds[0]
	}
	payload, _ = json.Marshal(rules)
	rows, err := tx.Query(ctx, `SELECT team,is_ready,character_id FROM entertainment_room_members WHERE code=$1`, code)
	if err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	defer rows.Close()
	counts := [3]int{}
	characters := [3][]string{}
	for rows.Next() {
		var team int
		var ready bool
		var character string
		if err = rows.Scan(&team, &ready, &character); err != nil {
			return EntertainmentRoomSnapshot{}, err
		}
		if !ready {
			return EntertainmentRoomSnapshot{}, errors.New("all players must be ready")
		}
		counts[team]++
		characters[team] = append(characters[team], character)
	}
	if counts[1] != rules.TeamSize || counts[2] != rules.TeamSize {
		return EntertainmentRoomSnapshot{}, errors.New("both teams must be full")
	}
	if rules.TeamSize == 1 && characters[1][0] != characters[2][0] {
		return EntertainmentRoomSnapshot{}, errors.New("1v1 players must select the same character")
	}
	if _, err = tx.Exec(ctx, `UPDATE entertainment_rooms SET rules=$2,state='starting',started_at=now() WHERE code=$1`, code, payload); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	if err = tx.Commit(ctx); err != nil {
		return EntertainmentRoomSnapshot{}, err
	}
	return p.RoomSnapshot(ctx, code)
}

func (p *Postgres) LeaveRoom(ctx context.Context, code, playerID string) error {
	room, err := p.RoomSnapshot(ctx, code)
	if err != nil {
		return err
	}
	if room.HostPlayerID == playerID {
		_, err = p.Pool.Exec(ctx, `UPDATE entertainment_rooms SET closed_at=now() WHERE code=$1`, code)
		return err
	}
	_, err = p.Pool.Exec(ctx, `DELETE FROM entertainment_room_members WHERE code=$1 AND player_id=$2`, code, playerID)
	return err
}

func (p *Postgres) AreFriends(ctx context.Context, first, second string) bool {
	var found bool
	err := p.Pool.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM friendships WHERE state='accepted' AND
		((requester_id=$1 AND addressee_id=$2) OR (requester_id=$2 AND addressee_id=$1)))`, first, second).Scan(&found)
	return err == nil && found
}
