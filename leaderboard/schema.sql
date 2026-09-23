-- PrizeTracker community leaderboard - Cloudflare D1 (SQLite).
--
-- The board mirrors the game's OWN season record (SeasonRank: exp, wins, losses, seasonMatches),
-- submitted by the mod as a snapshot after each match. Nothing here is computed from our match
-- log; the game's numbers are the honest comparable, and they reset with the season by
-- themselves.
--
-- Trust is made VISIBLE rather than enforced. Every snapshot is kept, checks run at write time
-- against the player's previous snapshot, and anything that fails becomes a flag on the row that
-- the board shows. A cheater is not blocked - they are labelled.

CREATE TABLE IF NOT EXISTS player (
  player_id    TEXT PRIMARY KEY,          -- random id the mod generates once and keeps
  display_name TEXT NOT NULL,
  first_seen   INTEGER NOT NULL,          -- server clock, epoch seconds - never the client's
  last_seen    INTEGER NOT NULL
);

-- Every submission, in order. The series is what makes the checks possible: monotonic counters,
-- plausible pace, and agreement between the game's totals and our own match count.
CREATE TABLE IF NOT EXISTS snapshot (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  player_id        TEXT    NOT NULL,
  season_id        INTEGER NOT NULL,
  exp              INTEGER NOT NULL,
  wins             INTEGER NOT NULL,
  losses           INTEGER NOT NULL,
  season_matches   INTEGER NOT NULL,
  consecutive_wins INTEGER NOT NULL DEFAULT 0,
  -- The matchmaking rating, a DIFFERENT number from exp: measured on a real account,
  -- exp=529 (Ultra League 4) alongside competitiveElo Standard=1500. Standard only.
  elo              INTEGER NOT NULL DEFAULT 0,
  local_matches    INTEGER NOT NULL DEFAULT 0,   -- matches OUR tracker recorded this season
  client_ts        INTEGER,                      -- kept only to compare against received_ts
  received_ts      INTEGER NOT NULL,
  flags            TEXT    NOT NULL DEFAULT '[]' -- JSON array of strings raised by this snapshot
);
CREATE INDEX IF NOT EXISTS snapshot_player_season ON snapshot (player_id, season_id, received_ts);

-- One row per (season, player), maintained at write time so reading the board is a single
-- indexed query. Workers Free allows 10 ms of CPU per request, so the board is never aggregated
-- on read.
CREATE TABLE IF NOT EXISTS standing (
  season_id        INTEGER NOT NULL,
  player_id        TEXT    NOT NULL,
  display_name     TEXT    NOT NULL,
  exp              INTEGER NOT NULL,
  wins             INTEGER NOT NULL,
  losses           INTEGER NOT NULL,
  season_matches   INTEGER NOT NULL,
  consecutive_wins INTEGER NOT NULL DEFAULT 0,
  elo              INTEGER NOT NULL DEFAULT 0,
  -- Master (Arceus) league, decided by the CLIENT: only it has the season config that says where
  -- Master begins. Below Master players are recorded but never ranked - exp separates them, and
  -- this board is an ELO board.
  master           INTEGER NOT NULL DEFAULT 0,
  snapshots        INTEGER NOT NULL,
  first_seen       INTEGER NOT NULL,
  last_seen        INTEGER NOT NULL,
  flags            TEXT    NOT NULL DEFAULT '[]', -- union of every flag ever raised this season
  -- The player's avatar as item ids, not as a picture: a few hundred bytes that let every other
  -- client build the real 3D figure locally for the podium. NULL until a client sends one, and a
  -- submission without one leaves whatever is already stored alone.
  outfit           TEXT,
  PRIMARY KEY (season_id, player_id)
);
CREATE INDEX IF NOT EXISTS standing_season_exp ON standing (season_id, exp DESC, season_matches DESC);

-- What the service knows about each season. Every client reads the season's end date from the
-- game's own config document, so they should all agree - and the stored end date is the one
-- MOST clients report, one vote per player, latest vote counting. A single client with a stale
-- or edited cache cannot set it, and the board can say how strong the agreement is.
CREATE TABLE IF NOT EXISTS season (
  season_id  INTEGER PRIMARY KEY,
  end_date   TEXT,                -- the consensus, refreshed on every vote
  first_seen INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS season_vote (
  season_id INTEGER NOT NULL,
  player_id TEXT    NOT NULL,
  end_date  TEXT    NOT NULL,
  PRIMARY KEY (season_id, player_id)
);
