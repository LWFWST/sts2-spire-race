ALTER TABLE players
    ADD COLUMN IF NOT EXISTS favorite_character TEXT NOT NULL DEFAULT 'Ironclad';
