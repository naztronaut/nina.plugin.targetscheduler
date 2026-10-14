/*
*/

ALTER TABLE exposureplan ADD COLUMN skipWhilePrioritized INTEGER DEFAULT 0;

PRAGMA user_version = 25;
