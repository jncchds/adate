-- What the person in a scene wears (user feedback, 2026-09-15: Samantha wore a swimsuit on the pier every time, and
-- going from the pier to the lookout together she would not have time to change).
--
-- dress is a dress code, chosen when the scene is written from the place, where they came from and what they do.
-- dress_over is something put on over it, such as a jacket the player offered in the conversation. The next slot
-- spent together starts from both. Scenes from before stay empty and are dressed as their place would dress them.

ALTER TABLE scene_log ADD COLUMN dress TEXT;

ALTER TABLE scene_log ADD COLUMN dress_over TEXT;
