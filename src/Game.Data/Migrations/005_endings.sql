-- Phase 2: endings (phase-2 plan §9, §10).
--
-- A save ends once. The row holds how it ended, with whom, on which day, and the recap shown
-- afterwards: who the player ended with, who they passed over, who left and why, and what the
-- person they chose was like.

CREATE TABLE player_profile (
    save_id      TEXT    PRIMARY KEY REFERENCES save(id) ON DELETE CASCADE,
    outcome      TEXT    NOT NULL CHECK (outcome IN ('Together', 'Alone', 'LeftAlone')),
    partner_id   TEXT    REFERENCES character(id) ON DELETE CASCADE,
    ended_day    INTEGER NOT NULL CHECK (ended_day >= 1),
    summary_json TEXT    NOT NULL,
    created_utc  TEXT    NOT NULL,
    CHECK ((outcome = 'Together') = (partner_id IS NOT NULL))
) STRICT;
