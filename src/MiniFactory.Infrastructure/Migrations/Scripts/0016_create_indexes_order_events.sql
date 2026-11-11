-- 訂單時間線依時間排序 / 清理腳本依時間刪除
CREATE INDEX IF NOT EXISTS ix_order_events_order_id_occurred_at ON order_events (order_id, occurred_at, id);
CREATE INDEX IF NOT EXISTS ix_order_events_occurred_at ON order_events (occurred_at);
