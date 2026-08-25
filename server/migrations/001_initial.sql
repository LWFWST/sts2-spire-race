CREATE TABLE IF NOT EXISTS players (
    id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS ratings (
    player_id TEXT NOT NULL REFERENCES players(id),
    pool TEXT NOT NULL CHECK (pool IN ('solo','team','casual_solo','casual_team')),
    hidden_rating INTEGER NOT NULL DEFAULT 1500,
    visible_points INTEGER NOT NULL DEFAULT 0,
    games_played INTEGER NOT NULL DEFAULT 0,
    wins INTEGER NOT NULL DEFAULT 0,
    losses INTEGER NOT NULL DEFAULT 0,
    tier TEXT NOT NULL DEFAULT 'Unranked',
    division INTEGER NOT NULL DEFAULT 4,
    PRIMARY KEY(player_id,pool)
);

CREATE TABLE IF NOT EXISTS matches (
    id TEXT PRIMARY KEY,
    game_version TEXT NOT NULL,
    kind TEXT NOT NULL,
    team_size INTEGER NOT NULL,
    state TEXT NOT NULL,
    payload JSONB NOT NULL,
    settlement JSONB,
    winner_team_id TEXT,
    finish_reason TEXT,
    started_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS match_participants (
    match_id TEXT NOT NULL REFERENCES matches(id),
    player_id TEXT NOT NULL REFERENCES players(id),
    team_id TEXT NOT NULL,
    rating_before INTEGER NOT NULL,
    rating_delta INTEGER,
    PRIMARY KEY(match_id,player_id)
);

CREATE TABLE IF NOT EXISTS entertainment_rooms (
    code CHAR(6) PRIMARY KEY,
    host_player_id TEXT NOT NULL REFERENCES players(id),
    rules JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    closed_at TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS integrity_audit (
    id BIGSERIAL PRIMARY KEY,
    player_id TEXT NOT NULL,
    match_id TEXT,
    game_version TEXT NOT NULL,
    verdict TEXT NOT NULL,
    detail JSONB NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS friendships (
    requester_id TEXT NOT NULL REFERENCES players(id),
    addressee_id TEXT NOT NULL REFERENCES players(id),
    state TEXT NOT NULL CHECK (state IN ('pending','accepted')) DEFAULT 'pending',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY(requester_id,addressee_id),
    CHECK(requester_id <> addressee_id)
);

CREATE INDEX IF NOT EXISTS friendships_addressee_idx ON friendships(addressee_id,state);
