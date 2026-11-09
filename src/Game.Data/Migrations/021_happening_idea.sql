-- Small happenings the model proposed for a kind of place (validated by C#, see HappeningIdeas), kept to be
-- used in a later scene there. used_at is the clock ("day|slot") of the scene that took the idea, so asking
-- again for the same scene (a rewrite, a reopened save) gives the same one back instead of using up another.

CREATE TABLE happening_idea (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  save_id       TEXT NOT NULL REFERENCES save(id) ON DELETE CASCADE,
  place_type_id TEXT NOT NULL,
  text          TEXT NOT NULL,
  proposed_day  INTEGER NOT NULL,
  used_at       TEXT
);

CREATE INDEX happening_idea_save_place ON happening_idea (save_id, place_type_id, used_at);
