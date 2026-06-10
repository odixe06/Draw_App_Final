-- 006_readd_snapshots.sql
-- Re-add the Snapshots table (dropped by 005_drop_snapshots.sql).
-- Snapshot = checkpoint JSON cua board, dung cho view-only time-travel + checkpoint dinh ky.
-- Server cung tu tao bang nay luc runtime (DbManager.EnsureSnapshotsTableAsync) nen migration
-- nay chu yeu de fresh-install / Neon-managed apply khop voi database_setup.sql.

CREATE TABLE IF NOT EXISTS Snapshots (
    id            SERIAL PRIMARY KEY,
    room_id       INT REFERENCES Rooms(id) ON DELETE CASCADE,
    snapshot_data JSONB       NOT NULL,
    thumbnail     TEXT        DEFAULT '',
    taken_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_snapshots_room_id  ON Snapshots(room_id);
CREATE INDEX IF NOT EXISTS idx_snapshots_taken_at ON Snapshots(taken_at);
