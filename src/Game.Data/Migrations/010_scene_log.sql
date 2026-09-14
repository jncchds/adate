-- Every scene as the player saw it (user feedback, 2026-09-15).
--
-- A turn's scene used to live only on screen: leaving and coming back rebuilt it from scratch, at the
-- wrong place and without the person, and regenerated what was already drawn. Each turn now adds a row
-- when it is taken, and the picture, the person, the written words, the player's reply and the reaction
-- are added as they arrive. The newest row that is not closed is the scene the player is in; Continue
-- closes it, and so does taking the next turn.
--
-- outcome_json holds the turn as decided, so a scene whose writing was interrupted can be finished.

CREATE TABLE scene_log (
    id              INTEGER PRIMARY KEY,
    save_id         TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    day             INTEGER NOT NULL CHECK (day >= 1),
    slot            TEXT    NOT NULL CHECK (slot IN ('Morning', 'Midday', 'Afternoon', 'Evening', 'Night')),
    place_id        TEXT    NOT NULL,
    encounter_id    TEXT,
    outcome_json    TEXT    NOT NULL,
    written         INTEGER NOT NULL DEFAULT 0 CHECK (written IN (0, 1)),
    text            TEXT    NOT NULL,
    background_path TEXT,
    character_id    TEXT,
    speaker         TEXT,
    expression      TEXT,
    sprite_path     TEXT,
    reply           TEXT,
    reaction        TEXT,
    popup           TEXT,
    agreed          TEXT,
    closed          INTEGER NOT NULL DEFAULT 0 CHECK (closed IN (0, 1)),
    created_utc     TEXT    NOT NULL
) STRICT;

CREATE INDEX scene_log_by_save ON scene_log (save_id, id);
