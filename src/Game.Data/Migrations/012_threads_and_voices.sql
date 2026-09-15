-- Material for better writing (user request, 2026-09-15: how do we raise the quality of the content).
--
-- A loose end is something a scene left open: a question not answered, a plan mentioned, something someone
-- said they would do. Open ones go back into later packets until a scene settles them. A save's first
-- threads are read from its recent scene log once; thread_seed records that it happened.
--
-- A voice is how a love interest talks, written once per person and kept with them.

CREATE TABLE story_thread (
    id           INTEGER PRIMARY KEY,
    save_id      TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    character_id TEXT,
    text         TEXT    NOT NULL,
    opened_day   INTEGER NOT NULL CHECK (opened_day >= 0),
    closed_day   INTEGER
);

CREATE INDEX ix_story_thread_open ON story_thread (save_id, closed_day);

CREATE TABLE thread_seed (
    save_id TEXT PRIMARY KEY REFERENCES save(id) ON DELETE CASCADE
);

ALTER TABLE character ADD COLUMN voice TEXT;
