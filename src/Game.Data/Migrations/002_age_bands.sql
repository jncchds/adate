-- Games may declare a minimum character age of 16, so the absolute floor moves from 18
-- to 16 and the meaning of 18 changes: it is no longer who may exist, it is who may be
-- depicted above PG13.
--
-- That distinction is enforced twice on purpose. Game.Core.Content.ContentPolicy computes
-- the clamp and is the only route to a ceiling, but a cache row outlives the process that
-- wrote it, and a row is what a later session reads back. The CHECK below means a sprite
-- above PG13 for a character under 18 cannot be stored at all, whatever the code above it
-- believed at the time.
--
-- SQLite cannot alter a CHECK constraint, so the table is rebuilt.

PRAGMA foreign_keys = OFF;

CREATE TABLE character_new (
    id                TEXT    PRIMARY KEY,
    save_id           TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    age               INTEGER NOT NULL,
    appearance_json   TEXT    NOT NULL,
    anchor_image_hash TEXT,
    anchor_seed       INTEGER,
    lora_path         TEXT,

    -- The absolute floor, below which no game may go.
    CHECK (age >= 16)
) STRICT;

INSERT INTO character_new (id, save_id, age, appearance_json, anchor_image_hash, anchor_seed, lora_path)
SELECT id, save_id, age, appearance_json, anchor_image_hash, anchor_seed, lora_path FROM character;

DROP TABLE character;
ALTER TABLE character_new RENAME TO character;

CREATE INDEX ix_character_save ON character(save_id);

-- Sprites above PG13 (ceiling 0) require an adult subject. Written as a trigger rather
-- than a CHECK because the age lives on another table and CHECK cannot reach it.
CREATE TRIGGER sprite_cache_age_band_insert
BEFORE INSERT ON sprite_cache
WHEN NEW.ceiling > 0
  AND (SELECT age FROM character WHERE id = NEW.character_id) < 18
BEGIN
    SELECT RAISE(ABORT, 'A sprite above PG13 cannot be stored for a character under 18.');
END;

CREATE TRIGGER sprite_cache_age_band_update
BEFORE UPDATE ON sprite_cache
WHEN NEW.ceiling > 0
  AND (SELECT age FROM character WHERE id = NEW.character_id) < 18
BEGIN
    SELECT RAISE(ABORT, 'A sprite above PG13 cannot be stored for a character under 18.');
END;

-- And the age itself cannot be edited downward to slip past the triggers above after
-- art has already been generated at a higher ceiling.
CREATE TRIGGER character_age_band_update
BEFORE UPDATE OF age ON character
WHEN NEW.age < 18
  AND EXISTS (SELECT 1 FROM sprite_cache WHERE character_id = NEW.id AND ceiling > 0)
BEGIN
    SELECT RAISE(ABORT, 'This character has art above PG13, so their age cannot be lowered below 18.');
END;

PRAGMA foreign_keys = ON;
