-- The clothes a scene named (user feedback, 2026-09-19: the picture showed a girl in shorts and a t-shirt
-- while the words described a dress).
--
-- The dress code says what sort of clothes suit the moment; it never said which ones, so the words and the
-- picture each chose their own. The writer now names the garments themselves and the sprite is drawn from
-- them, which needs somewhere to keep them beside the code they were chosen under.
--
-- Null for every scene written before this, and those go on being drawn from the style pack's wardrobe.
-- Figures keep theirs inside figures_json, which is JSON and needed no column.

ALTER TABLE scene_log ADD COLUMN dress_garments TEXT;
