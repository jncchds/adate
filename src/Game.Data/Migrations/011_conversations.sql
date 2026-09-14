-- Scenes become conversations (user feedback, 2026-09-15: one reply and a reaction felt hollow).
--
-- The player may reply several times within a scene. The waiting scene counts the replies given so far,
-- and the scene log keeps every exchange (the player's words, the answer, a popup, an agreed meeting)
-- as a JSON array; its single reply and reaction columns from migration 010 are no longer written.

ALTER TABLE pending_scene ADD COLUMN replies INTEGER NOT NULL DEFAULT 0 CHECK (replies >= 0);

ALTER TABLE scene_log ADD COLUMN exchanges_json TEXT;
