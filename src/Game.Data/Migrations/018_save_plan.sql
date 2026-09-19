-- One save's own version of its setting (user request, 2026-09-19: the milestones and the places were
-- hardcoded, so every game in a setting laid out the same town and hit the same dates).
--
-- A setting file now lists roles rather than places. At the start of a game the planner fills each role,
-- dates the calendar, opens a few loose ends and rewrites the authored beats to read for the places it
-- chose. The places themselves go in `place`, which already holds everything a place is; only the
-- calendar and the rewritten prose need somewhere new.
--
-- `save_plan` records that a save has been planned, so it happens once and a save planned before the
-- model was reachable is not left half laid out.

CREATE TABLE save_plan (
    save_id TEXT PRIMARY KEY REFERENCES save(id) ON DELETE CASCADE,
    planned INTEGER NOT NULL CHECK (planned IN (0, 1))
);

CREATE TABLE save_event (
    save_id  TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    id       TEXT    NOT NULL,
    name     TEXT    NOT NULL,
    day      INTEGER NOT NULL CHECK (day >= 1),
    place_id TEXT    NOT NULL,
    slot     TEXT    NOT NULL,
    PRIMARY KEY (save_id, id)
);

CREATE TABLE save_encounter_text (
    save_id TEXT NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    id      TEXT NOT NULL,
    text    TEXT NOT NULL,
    PRIMARY KEY (save_id, id)
);
