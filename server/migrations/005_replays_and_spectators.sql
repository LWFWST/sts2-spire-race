CREATE TABLE IF NOT EXISTS race_replays (
    match_id TEXT NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
    game_id TEXT NOT NULL,
    player_id TEXT NOT NULL REFERENCES players(id),
    team_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    character_id TEXT NOT NULL DEFAULT '',
    event_count INTEGER NOT NULL DEFAULT 0,
    completed BOOLEAN NOT NULL DEFAULT false,
    is_public BOOLEAN NOT NULL DEFAULT false,
    bundle BYTEA NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ,
    PRIMARY KEY(match_id, game_id, player_id)
);

CREATE INDEX IF NOT EXISTS race_replays_live_idx
    ON race_replays(updated_at DESC) WHERE completed = false;

CREATE TABLE IF NOT EXISTS entertainment_room_spectators (
    code CHAR(6) NOT NULL REFERENCES entertainment_rooms(code) ON DELETE CASCADE,
    player_id TEXT NOT NULL REFERENCES players(id),
    watching_team SMALLINT NOT NULL DEFAULT 1 CHECK (watching_team IN (1,2)),
    joined_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY(code, player_id)
);

CREATE INDEX IF NOT EXISTS entertainment_room_spectators_player_idx
    ON entertainment_room_spectators(player_id);
