-- How a save's pictures look: a preset id such as realistic, or a style the player typed at new game.
--
-- NULL for saves created before visual styles existed, which are drawn in the pack's own style (anime).

ALTER TABLE save ADD COLUMN visual_style TEXT;
