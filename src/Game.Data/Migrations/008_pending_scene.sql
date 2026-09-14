-- Phase 3: choices inside written scenes (phase-3 plan, step 4).
--
-- A written scene with someone present ends with replies the writer proposed. The scene waits here,
-- one per save, until the player picks one or writes their own; turns are refused meanwhile.

CREATE TABLE pending_scene (
    save_id      TEXT    PRIMARY KEY REFERENCES save(id) ON DELETE CASCADE,
    day          INTEGER NOT NULL CHECK (day >= 1),
    slot         TEXT    NOT NULL CHECK (slot IN ('Morning', 'Midday', 'Afternoon', 'Evening', 'Night')),
    place_id     TEXT    NOT NULL,
    encounter_id TEXT,
    with_json    TEXT    NOT NULL,
    text         TEXT    NOT NULL,
    choices_json TEXT    NOT NULL,
    created_utc  TEXT    NOT NULL
) STRICT;
