-- 告警列表與未讀數 / 依訂單查詢 / 依設備查詢
CREATE INDEX IF NOT EXISTS ix_alerts_status_created_at ON alerts (status, created_at);
CREATE INDEX IF NOT EXISTS ix_alerts_order_id ON alerts (order_id);
CREATE INDEX IF NOT EXISTS ix_alerts_equipment_id ON alerts (equipment_id);
