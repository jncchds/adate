-- The language a save's story is written in, typed by the player at new game.
--
-- NULL for saves created before languages existed, which are English.

ALTER TABLE save ADD COLUMN narration_language TEXT;
