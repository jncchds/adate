-- A moment worth seeing, drawn as one picture with the people in it (user request, 2026-10-17: on special
-- occasions the love interests are drawn into the scene itself instead of standing on the place as cut-outs).
-- The writer calls for one in a scene's words or in any answer, and it stands until the words end it or the
-- people on the stage change. Kept with the scene so reopening the save shows the same picture: its shot, who
-- is in it and the picture once drawn, as JSON. Null when no moment is on screen, and for every scene before.

ALTER TABLE scene_log ADD COLUMN moment_json TEXT;
