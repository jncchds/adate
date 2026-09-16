-- Who stands on a scene's stage, left to right, as the words last left it: each person's id, name, style,
-- expression, outfit and picture. A scene with two people shows both, and someone who leaves is no longer
-- drawn. Null for scenes from before, which read their one person from character_id and the columns beside it.

ALTER TABLE scene_log ADD COLUMN figures_json TEXT;
