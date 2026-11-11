-- Publisher 取待發送批次：WHERE published_at IS NULL AND dead_lettered_at IS NULL ORDER BY occurred_at, id
-- 同時涵蓋封存與清理用的 published_at 範圍條件（見 database.md 1.1）
CREATE INDEX IF NOT EXISTS ix_outbox_messages_pending ON outbox_messages (published_at, dead_lettered_at, occurred_at, id);
