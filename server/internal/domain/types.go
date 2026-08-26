package domain

import "time"

const (
	MaxMatchMilliseconds int64 = 180 * 60 * 1000
	DisconnectGrace            = 10 * time.Second
	ReadyCheckWindow           = 30 * time.Second
	DeathDecisionWindow        = 60 * time.Second
	LegendPickWindow           = 30 * time.Second
)

type QueueKind string

const (
	QueueCasual        QueueKind = "casual"
	QueueRanked        QueueKind = "ranked"
	QueueEntertainment QueueKind = "entertainment"
)

type Outcome string

const (
	OutcomeActive      Outcome = "active"
	OutcomeScoreLocked Outcome = "score_locked"
	OutcomeFinished    Outcome = "finished"
	OutcomeSurrendered Outcome = "surrendered"
	OutcomeForfeited   Outcome = "forfeited"
	OutcomeTimedOut    Outcome = "timed_out"
)

type FinishReason string

const (
	ReasonBossCompletion FinishReason = "boss_completion"
	ReasonHighestFloor   FinishReason = "highest_floor"
	ReasonEarlierFloor   FinishReason = "earlier_floor_entry"
	ReasonRandomTiebreak FinishReason = "random_tiebreak"
	ReasonSurrender      FinishReason = "surrender"
	ReasonDisconnect     FinishReason = "disconnect"
	ReasonIntegrity      FinishReason = "integrity_failure"
	ReasonTimeout        FinishReason = "timeout"
	ReasonSeriesVictory  FinishReason = "series_victory"
)

type Rules struct {
	TeamSize                 int      `json:"team_size"`
	Seed                     string   `json:"seed"`
	Ascension                int      `json:"ascension"`
	TimeLimitMS              int64    `json:"time_limit_ms"`
	EventSLLimit             int      `json:"event_sl_limit"`
	CombatSLLimit            int      `json:"combat_sl_limit"`
	CharacterID              string   `json:"character_id,omitempty"`
	Modifiers                []string `json:"modifiers"`
	RandomSeed               bool     `json:"random_seed,omitempty"`
	AllowDuplicateCharacters bool     `json:"allow_duplicate_characters,omitempty"`
	CharacterPolicy          string   `json:"character_policy,omitempty"`
	TimerKind                string   `json:"timer_kind,omitempty"`
	VictoryRule              string   `json:"victory_rule,omitempty"`
	AllowSpectators          bool     `json:"allow_spectators,omitempty"`
	Visibility               string   `json:"visibility,omitempty"`
	CoordinationMode         string   `json:"coordination_mode,omitempty"`
	BestOf                   int      `json:"best_of,omitempty"`
}

type Progress struct {
	MatchID           string  `json:"match_id"`
	GameID            string  `json:"game_id"`
	TeamID            string  `json:"team_id"`
	Sequence          int64   `json:"sequence"`
	Floor             int     `json:"floor"`
	FloorEnteredAtMS  int64   `json:"floor_entered_at_ms"`
	FinalBossDefeated bool    `json:"final_boss_defeated"`
	CompletedAtMS     *int64  `json:"completed_at_ms,omitempty"`
	Outcome           Outcome `json:"outcome"`
	RestartCount      int     `json:"restart_count"`
	EventSLUsed       int     `json:"event_sl_used"`
	CombatSLUsed      int     `json:"combat_sl_used"`
}

type SettlementSide struct {
	TeamID                string  `json:"team_id"`
	Outcome               Outcome `json:"outcome"`
	HighestFloor          int     `json:"highest_floor"`
	HighestFloorEnteredMS int64   `json:"highest_floor_entered_ms"`
	CompletionMS          *int64  `json:"completion_ms,omitempty"`
	RestartCount          int     `json:"restart_count"`
	EventSLUsed           int     `json:"event_sl_used"`
	CombatSLUsed          int     `json:"combat_sl_used"`
}

type Settlement struct {
	MatchID             string         `json:"match_id"`
	GameID              string         `json:"game_id"`
	WinnerTeamID        string         `json:"winner_team_id"`
	Reason              FinishReason   `json:"reason"`
	First               SettlementSide `json:"first"`
	Second              SettlementSide `json:"second"`
	AuditDetail         string         `json:"audit_detail"`
	SeriesGames         []LegendGame   `json:"series_games,omitempty"`
	VisibleRatingDeltas map[string]int `json:"visible_rating_deltas,omitempty"`
	CompletedAt         time.Time      `json:"completed_at"`
}

type LegendGame struct {
	GameNumber   int          `json:"game_number"`
	GameID       string       `json:"game_id"`
	CharacterID  string       `json:"character_id"`
	WinnerTeamID string       `json:"winner_team_id"`
	Reason       FinishReason `json:"reason"`
	ElapsedMS    int64        `json:"elapsed_ms"`
}

type QueueRequest struct {
	PlayerID      string            `json:"-"`
	DisplayName   string            `json:"-"`
	GameVersion   string            `json:"game_version"`
	Kind          QueueKind         `json:"kind"`
	TeamSize      int               `json:"team_size"`
	Pool          string            `json:"pool"`
	HiddenRating  int               `json:"hidden_rating"`
	VisibleTiers  []string          `json:"visible_tiers"`
	TeamPlayerIDs []string          `json:"team_player_ids"`
	CharacterID   string            `json:"character_id,omitempty"`
	CharacterIDs  map[string]string `json:"character_ids,omitempty"`
}

type Assignment struct {
	MatchID                 string            `json:"match_id"`
	GameID                  string            `json:"game_id"`
	GameVersion             string            `json:"game_version"`
	Kind                    QueueKind         `json:"kind"`
	TeamSize                int               `json:"team_size"`
	FirstTeamID             string            `json:"first_team_id"`
	SecondTeamID            string            `json:"second_team_id"`
	FirstPlayerIDs          []string          `json:"first_player_ids"`
	SecondPlayerIDs         []string          `json:"second_player_ids"`
	Rules                   Rules             `json:"rules"`
	SessionNonce            string            `json:"session_nonce"`
	StartedAtMS             int64             `json:"started_at_ms"`
	LegendSeries            bool              `json:"legend_series"`
	CharacterIDs            map[string]string `json:"character_ids,omitempty"`
	FirstSteamHostPlayerID  string            `json:"first_steam_host_player_id,omitempty"`
	SecondSteamHostPlayerID string            `json:"second_steam_host_player_id,omitempty"`
	FirstSteamLobbyID       string            `json:"first_steam_lobby_id,omitempty"`
	SecondSteamLobbyID      string            `json:"second_steam_lobby_id,omitempty"`
}

type LegendDraft struct {
	PlayerOneBanOne string   `json:"player_one_ban_one"`
	PlayerOneBanTwo string   `json:"player_one_ban_two"`
	PlayerTwoBanOne string   `json:"player_two_ban_one"`
	PlayerTwoBanTwo string   `json:"player_two_ban_two"`
	UsedCharacters  []string `json:"used_characters"`
	Selected        string   `json:"selected_character,omitempty"`
	SelectingTeam   string   `json:"selecting_team,omitempty"`
	GameNumber      int      `json:"game_number"`
	PlayerOneWins   int      `json:"player_one_wins"`
	PlayerTwoWins   int      `json:"player_two_wins"`
}
