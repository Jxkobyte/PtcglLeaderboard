-- The board ranks Master (Arceus) players only. The CLIENT decides who is one - only it has the
-- season config that says where Master begins - so this is just storage for that answer.
ALTER TABLE standing ADD COLUMN master INTEGER NOT NULL DEFAULT 0;
