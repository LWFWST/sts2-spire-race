ALTER TABLE entertainment_rooms
    ADD COLUMN IF NOT EXISTS coordination_mode TEXT NOT NULL DEFAULT 'server'
        CHECK (coordination_mode IN ('server','p2p')),
    ADD COLUMN IF NOT EXISTS state TEXT NOT NULL DEFAULT 'waiting'
        CHECK (state IN ('waiting','starting','started')),
    ADD COLUMN IF NOT EXISTS started_at TIMESTAMPTZ;

ALTER TABLE entertainment_room_members
    ADD COLUMN IF NOT EXISTS is_ready BOOLEAN NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS character_id TEXT NOT NULL DEFAULT 'Ironclad';
