-- Whether a scene's words are the authored placeholder, because the writer was asked and failed. The player
-- is shown a warning beside them and can ask for the words again; without this the warning would be lost
-- when the save is reopened. Scenes from before are taken as written.

ALTER TABLE scene_log ADD COLUMN fallback INTEGER NOT NULL DEFAULT 0;
