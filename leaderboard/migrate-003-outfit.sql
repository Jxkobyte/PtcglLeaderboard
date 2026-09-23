-- The avatar a player is wearing, as item ids rather than an image, so every client can
-- build the real 3D figure for the podium. Small: a few hundred bytes per player.
ALTER TABLE standing ADD COLUMN outfit TEXT;
