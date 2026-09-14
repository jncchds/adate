-- Phase 2: settings, places and the cast (phase-2 plan §10).
--
-- A save now belongs to a setting, and its places are rows: a place type, a name, some of the
-- type's details and the place's own seed. background_cache.location_id holds a place id from
-- here on; rows written before this point name place types whose ids the settings reuse, so none
-- is rewritten.
--
-- The cast is stored on the character table. Its rules are enforced here as well as in
-- CastGenerator, for the same reason migration 002 enforces the age clamp: a row outlives the
-- process that wrote it.

ALTER TABLE save ADD COLUMN setting_id TEXT;
ALTER TABLE save ADD COLUMN player_name TEXT;
ALTER TABLE save ADD COLUMN player_gender TEXT;

CREATE TABLE place (
    save_id      TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    id           TEXT    NOT NULL,
    type_id      TEXT    NOT NULL,
    name         TEXT    NOT NULL,
    details_json TEXT    NOT NULL,
    seed         INTEGER NOT NULL,
    origin       TEXT    NOT NULL CHECK (origin IN ('authored', 'story')),
    known        INTEGER NOT NULL DEFAULT 0 CHECK (known IN (0, 1)),
    first_day    INTEGER,
    created_utc  TEXT    NOT NULL,
    PRIMARY KEY (save_id, id)
) STRICT;

ALTER TABLE character ADD COLUMN name TEXT;
ALTER TABLE character ADD COLUMN role TEXT NOT NULL DEFAULT 'main_li' CHECK (role IN ('main_li', 'variant'));
ALTER TABLE character ADD COLUMN variant_of TEXT REFERENCES character(id) ON DELETE CASCADE;
ALTER TABLE character ADD COLUMN profile_id TEXT;
ALTER TABLE character ADD COLUMN variation_json TEXT;
ALTER TABLE character ADD COLUMN temper_json TEXT;
ALTER TABLE character ADD COLUMN want_id TEXT;
ALTER TABLE character ADD COLUMN aesthetic TEXT;
ALTER TABLE character ADD COLUMN route TEXT;
ALTER TABLE character ADD COLUMN home_place_id TEXT;
ALTER TABLE character ADD COLUMN canon_json TEXT;
ALTER TABLE character ADD COLUMN met_day INTEGER;
ALTER TABLE character ADD COLUMN met_place_id TEXT;
ALTER TABLE character ADD COLUMN met_encounter_id TEXT;

CREATE INDEX ix_character_variant_of ON character(variant_of);

CREATE TABLE character_outfit (
    save_id      TEXT NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    character_id TEXT NOT NULL REFERENCES character(id) ON DELETE CASCADE,
    outfit       TEXT NOT NULL,
    phrases_json TEXT NOT NULL,
    PRIMARY KEY (character_id, outfit)
) STRICT;

CREATE TABLE game_clock (
    save_id TEXT    PRIMARY KEY REFERENCES save(id) ON DELETE CASCADE,
    day     INTEGER NOT NULL CHECK (day >= 1),
    slot    TEXT    NOT NULL CHECK (slot IN ('Morning', 'Midday', 'Afternoon', 'Evening', 'Night'))
) STRICT;

CREATE TABLE flag (
    save_id TEXT NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    key     TEXT NOT NULL,
    value   TEXT NOT NULL,
    PRIMARY KEY (save_id, key)
) STRICT;

CREATE TABLE visit (
    save_id      TEXT    NOT NULL,
    day          INTEGER NOT NULL CHECK (day >= 1),
    slot         TEXT    NOT NULL CHECK (slot IN ('Morning', 'Midday', 'Afternoon', 'Evening', 'Night')),
    place_id     TEXT    NOT NULL,
    with_json    TEXT    NOT NULL,
    encounter_id TEXT,
    FOREIGN KEY (save_id, place_id) REFERENCES place(save_id, id) ON DELETE CASCADE
) STRICT;

CREATE INDEX ix_visit_save_day ON visit(save_id, day);

-- The cast rules (plan §5). The same five checks run on insert and on any update that could
-- break them.

CREATE TRIGGER character_cast_insert
BEFORE INSERT ON character
BEGIN
    SELECT RAISE(ABORT, 'A character is a variant exactly when it names the main love interest it varies.')
    WHERE (NEW.variant_of IS NULL) <> (NEW.role = 'main_li');

    SELECT RAISE(ABORT, 'A variant must belong to a main love interest in the same save.')
    WHERE NEW.variant_of IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM character m
                      WHERE m.id = NEW.variant_of AND m.save_id = NEW.save_id AND m.variant_of IS NULL);

    SELECT RAISE(ABORT, 'A variant must share its main love interest''s subject.')
    WHERE NEW.variant_of IS NOT NULL
      AND json_extract(NEW.appearance_json, '$.subject') IS NOT
          (SELECT json_extract(m.appearance_json, '$.subject') FROM character m WHERE m.id = NEW.variant_of);

    SELECT RAISE(ABORT, 'A variant of an adult main love interest must be an adult.')
    WHERE NEW.variant_of IS NOT NULL
      AND NEW.age < 18
      AND (SELECT m.age FROM character m WHERE m.id = NEW.variant_of) >= 18;

    SELECT RAISE(ABORT, 'A variant of a main love interest under 18 must have their exact age.')
    WHERE NEW.variant_of IS NOT NULL
      AND (SELECT m.age FROM character m WHERE m.id = NEW.variant_of) < 18
      AND NEW.age <> (SELECT m.age FROM character m WHERE m.id = NEW.variant_of);
END;

CREATE TRIGGER character_cast_update
BEFORE UPDATE OF variant_of, role, age, appearance_json, save_id ON character
BEGIN
    SELECT RAISE(ABORT, 'A character is a variant exactly when it names the main love interest it varies.')
    WHERE (NEW.variant_of IS NULL) <> (NEW.role = 'main_li');

    SELECT RAISE(ABORT, 'A variant must belong to a main love interest in the same save.')
    WHERE NEW.variant_of IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM character m
                      WHERE m.id = NEW.variant_of AND m.save_id = NEW.save_id AND m.variant_of IS NULL);

    SELECT RAISE(ABORT, 'A variant must share its main love interest''s subject.')
    WHERE NEW.variant_of IS NOT NULL
      AND json_extract(NEW.appearance_json, '$.subject') IS NOT
          (SELECT json_extract(m.appearance_json, '$.subject') FROM character m WHERE m.id = NEW.variant_of);

    SELECT RAISE(ABORT, 'A variant of an adult main love interest must be an adult.')
    WHERE NEW.variant_of IS NOT NULL
      AND NEW.age < 18
      AND (SELECT m.age FROM character m WHERE m.id = NEW.variant_of) >= 18;

    SELECT RAISE(ABORT, 'A variant of a main love interest under 18 must have their exact age.')
    WHERE NEW.variant_of IS NOT NULL
      AND (SELECT m.age FROM character m WHERE m.id = NEW.variant_of) < 18
      AND NEW.age <> (SELECT m.age FROM character m WHERE m.id = NEW.variant_of);
END;

-- And the main love interest cannot move out from under a stored cast: every variant's age and
-- subject were checked against the main LI as they were.
CREATE TRIGGER character_main_with_cast_update
BEFORE UPDATE OF age, appearance_json ON character
WHEN EXISTS (SELECT 1 FROM character v WHERE v.variant_of = NEW.id)
 AND (NEW.age <> OLD.age
      OR json_extract(NEW.appearance_json, '$.subject') IS NOT json_extract(OLD.appearance_json, '$.subject'))
BEGIN
    SELECT RAISE(ABORT, 'A main love interest with a stored cast cannot change age or subject.');
END;
