-- Phase 3: weather in backgrounds (phase-3 plan, step 1).
--
-- A place looks different in the rain, so backgrounds are cached per weather as well as per place
-- and time of day. Rows drawn before weather existed are clear-weather backgrounds, which is what
-- the default says: clear adds nothing to the prompt.

ALTER TABLE background_cache ADD COLUMN weather TEXT NOT NULL DEFAULT 'clear';

DROP INDEX ix_background_lookup;

CREATE INDEX ix_background_lookup
    ON background_cache(save_id, location_id, time_of_day, weather);
