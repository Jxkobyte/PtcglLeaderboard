-- Adds the matchmaking rating alongside exp. Safe to re-run only once per table: SQLite has no
-- ADD COLUMN IF NOT EXISTS, so a second run errors with "duplicate column name", which is
-- harmless and means it is already applied.
ALTER TABLE snapshot ADD COLUMN elo INTEGER NOT NULL DEFAULT 0;
ALTER TABLE standing ADD COLUMN elo INTEGER NOT NULL DEFAULT 0;
