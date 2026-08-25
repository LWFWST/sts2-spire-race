CREATE TABLE IF NOT EXISTS entertainment_room_members (
    code CHAR(6) NOT NULL REFERENCES entertainment_rooms(code) ON DELETE CASCADE,
    player_id TEXT NOT NULL REFERENCES players(id),
    team SMALLINT NOT NULL CHECK (team IN (1,2)),
    joined_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY(code,player_id)
);

CREATE INDEX IF NOT EXISTS entertainment_room_members_player_idx
    ON entertainment_room_members(player_id);
