-- How a story place looks, in the writer's words: a few visual phrases added to its type's description.
--
-- NULL for authored places and for places added before looks existed, which draw as before.

ALTER TABLE place ADD COLUMN look TEXT;
