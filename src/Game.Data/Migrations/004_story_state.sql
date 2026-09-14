-- Phase 2: story state (phase-2 plan §6, §8, §10).
--
-- Facts are append-only: a fact is never edited, only superseded once by the fact that replaced it,
-- so the history of what the story said stays readable. Which contradictions are allowed is decided
-- in C# (FactLedger), because it depends on the predicate list, which is content.
--
-- A relationship stage only moves forward. Leaving is an ending rule, not a stage (plan §9).

ALTER TABLE character ADD COLUMN story_json TEXT;

CREATE TABLE fact (
    id            INTEGER PRIMARY KEY,
    save_id       TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    subject       TEXT    NOT NULL,
    predicate     TEXT    NOT NULL,
    object        TEXT    NOT NULL,
    level         TEXT    NOT NULL CHECK (level IN ('Core', 'Established', 'Claimed')),
    source        TEXT    NOT NULL,
    day           INTEGER NOT NULL CHECK (day >= 0),
    explained_by  TEXT,
    superseded_by INTEGER REFERENCES fact(id)
) STRICT;

CREATE INDEX ix_fact_current ON fact(save_id, subject, predicate) WHERE superseded_by IS NULL;

CREATE TRIGGER fact_append_only
BEFORE UPDATE ON fact
WHEN NEW.save_id IS NOT OLD.save_id
  OR NEW.subject IS NOT OLD.subject
  OR NEW.predicate IS NOT OLD.predicate
  OR NEW.object IS NOT OLD.object
  OR NEW.level IS NOT OLD.level
  OR NEW.source IS NOT OLD.source
  OR NEW.day IS NOT OLD.day
  OR OLD.superseded_by IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'Facts are append-only: a fact can only be superseded, and only once.');
END;

CREATE TABLE fact_knowledge (
    fact_id INTEGER NOT NULL REFERENCES fact(id) ON DELETE CASCADE,
    knower  TEXT    NOT NULL,
    day     INTEGER NOT NULL CHECK (day >= 0),
    PRIMARY KEY (fact_id, knower)
) STRICT;

CREATE TABLE rel_state (
    save_id      TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    character_id TEXT    NOT NULL REFERENCES character(id) ON DELETE CASCADE,
    affection    INTEGER NOT NULL CHECK (affection BETWEEN -100 AND 100),
    trust        INTEGER NOT NULL CHECK (trust BETWEEN -100 AND 100),
    attraction   INTEGER NOT NULL CHECK (attraction BETWEEN -100 AND 100),
    suspicion    INTEGER NOT NULL CHECK (suspicion BETWEEN 0 AND 100),
    stage        TEXT    NOT NULL CHECK (stage IN ('Stranger', 'Acquaintance', 'Friend', 'Dating', 'Committed')),
    gain_day     INTEGER NOT NULL DEFAULT 0,
    day_gain     INTEGER NOT NULL DEFAULT 0,
    dealbreaker  INTEGER NOT NULL DEFAULT 0 CHECK (dealbreaker IN (0, 1)),
    PRIMARY KEY (save_id, character_id)
) STRICT;

CREATE TRIGGER rel_state_stage_forward
BEFORE UPDATE OF stage ON rel_state
WHEN (CASE NEW.stage WHEN 'Stranger' THEN 0 WHEN 'Acquaintance' THEN 1 WHEN 'Friend' THEN 2 WHEN 'Dating' THEN 3 ELSE 4 END)
   < (CASE OLD.stage WHEN 'Stranger' THEN 0 WHEN 'Acquaintance' THEN 1 WHEN 'Friend' THEN 2 WHEN 'Dating' THEN 3 ELSE 4 END)
BEGIN
    SELECT RAISE(ABORT, 'A relationship stage only moves forward.');
END;

CREATE TABLE schedule (
    save_id      TEXT NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    character_id TEXT NOT NULL PRIMARY KEY REFERENCES character(id) ON DELETE CASCADE,
    entries_json TEXT NOT NULL
) STRICT;

CREATE TABLE promise (
    save_id      TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    id           TEXT    NOT NULL,
    character_id TEXT    NOT NULL REFERENCES character(id) ON DELETE CASCADE,
    kind         TEXT    NOT NULL CHECK (kind IN ('Meet', 'Call', 'Bring', 'KeepSecret', 'Help')),
    made_day     INTEGER NOT NULL CHECK (made_day >= 1),
    due_day      INTEGER NOT NULL,
    due_slot     TEXT    CHECK (due_slot IS NULL OR due_slot IN ('Morning', 'Midday', 'Afternoon', 'Evening', 'Night')),
    place_id     TEXT,
    status       TEXT    NOT NULL DEFAULT 'Open' CHECK (status IN ('Open', 'Kept', 'Broken')),
    resolved_day INTEGER,
    PRIMARY KEY (save_id, id),
    CHECK (due_day >= made_day),
    CHECK (kind <> 'Meet' OR place_id IS NOT NULL)
) STRICT;

CREATE TRIGGER promise_resolved_once
BEFORE UPDATE OF status ON promise
WHEN OLD.status <> 'Open'
BEGIN
    SELECT RAISE(ABORT, 'A promise is kept or broken once.');
END;

CREATE TABLE turn_log (
    id           INTEGER PRIMARY KEY,
    save_id      TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    day          INTEGER NOT NULL CHECK (day >= 1),
    slot         TEXT    NOT NULL,
    kind         TEXT    NOT NULL,
    payload_json TEXT    NOT NULL,
    created_utc  TEXT    NOT NULL
) STRICT;

CREATE INDEX ix_turn_log_save ON turn_log(save_id, id);
