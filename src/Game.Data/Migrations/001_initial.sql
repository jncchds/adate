-- Spike 0 schema (HANDOFF 5).
--
-- Every table carries save_id from day one, even while exactly one save exists
-- (HANDOFF 1.4). Adding it later would mean rewriting every query.
--
-- Cache tables carry `ceiling` for the same reason (HANDOFF 1.8): art generated at
-- one content ceiling must never be served into a session running at another, and a
-- column added after the fact cannot describe rows written before it existed.

CREATE TABLE save (
    id            TEXT    PRIMARY KEY,
    style_pack_id TEXT    NOT NULL,
    -- Fingerprint of the pack manifest as it stood when the save was created. A pack
    -- is locked per save; this detects the manifest being edited underneath one.
    pack_fingerprint TEXT NOT NULL,
    ceiling       INTEGER NOT NULL DEFAULT 0,
    created_utc   TEXT    NOT NULL
) STRICT;

CREATE TABLE character (
    id                TEXT    PRIMARY KEY,
    save_id           TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    age               INTEGER NOT NULL,
    appearance_json   TEXT    NOT NULL,
    anchor_image_hash TEXT,
    anchor_seed       INTEGER,
    -- Reserved. Populated only if the fallback ladder reaches step 4 (per-character
    -- LoRA trained from approved sprites). Unused in Spike 0.
    lora_path         TEXT,

    -- HANDOFF 1.9. Enforced here as well as in code because a character record that
    -- reaches a generation prompt without a valid adult age is not a bug worth
    -- discovering at the image layer.
    CHECK (age >= 18)
) STRICT;

CREATE INDEX ix_character_save ON character(save_id);

CREATE TABLE sprite_cache (
    hash         TEXT    PRIMARY KEY,   -- sha256 of the generation parameters
    save_id      TEXT    NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    character_id TEXT    NOT NULL REFERENCES character(id) ON DELETE CASCADE,
    outfit       TEXT    NOT NULL DEFAULT 'default',
    pose         TEXT    NOT NULL DEFAULT 'standing',
    expression   TEXT    NOT NULL,
    ceiling      INTEGER NOT NULL,
    path         TEXT    NOT NULL,
    created_utc  TEXT    NOT NULL
) STRICT;

-- The lookup the scene viewer performs on every expression change. Ceiling is part of
-- the key, not a filter applied afterwards, so a session can never see a row generated
-- under a different one.
CREATE INDEX ix_sprite_lookup
    ON sprite_cache(save_id, character_id, outfit, pose, expression, ceiling);

CREATE TABLE background_cache (
    hash        TEXT PRIMARY KEY,
    save_id     TEXT NOT NULL REFERENCES save(id) ON DELETE CASCADE,
    location_id TEXT NOT NULL,
    time_of_day TEXT NOT NULL,
    path        TEXT NOT NULL,
    created_utc TEXT NOT NULL
) STRICT;

CREATE INDEX ix_background_lookup
    ON background_cache(save_id, location_id, time_of_day);
