-- 單一設備的歷史查詢 / 清理腳本依時間刪除
CREATE INDEX IF NOT EXISTS ix_equipment_status_history_equipment_id_occurred_at ON equipment_status_history (equipment_id, occurred_at);
CREATE INDEX IF NOT EXISTS ix_equipment_status_history_occurred_at ON equipment_status_history (occurred_at);
