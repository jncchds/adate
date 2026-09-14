-- Phase 2: memory (phase-2 plan §8, §10).
--
-- Each written scene leaves a short summary with its embedding and salience tags. Scenes compact
-- into day summaries and days into week summaries; a compacted row stays, pointing at the summary
-- that replaced it. Memories tagged first or conflict are never compacted, enforced here as well
-- as in MemoryRetrieval.

CREATE TABLE memory (
    id             INTEGER PRIMARY KEY,
    save_id        TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    scope          TEXT    NOT NULL CHECK (scope IN ('Scene', 'Day', 'Week')),
    day            INTEGER NOT NULL CHECK (day >= 1),
    summary        TEXT    NOT NULL,
    people_json    TEXT    NOT NULL,
    tags_json      TEXT    NOT NULL,
    embedding      BLOB,
    compacted_into INTEGER REFERENCES memory(id),
    created_utc    TEXT    NOT NULL
) STRICT;

CREATE INDEX ix_memory_standing ON memory(save_id, day) WHERE compacted_into IS NULL;

CREATE TRIGGER memory_keeps_firsts_and_conflicts
BEFORE UPDATE OF compacted_into ON memory
WHEN NEW.compacted_into IS NOT NULL
 AND EXISTS (SELECT 1 FROM json_each(OLD.tags_json) WHERE value IN ('first', 'conflict'))
BEGIN
    SELECT RAISE(ABORT, 'A memory tagged first or conflict is never compacted.');
END;

CREATE TRIGGER memory_compacted_once
BEFORE UPDATE OF compacted_into ON memory
WHEN OLD.compacted_into IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'A memory is compacted once.');
END;
