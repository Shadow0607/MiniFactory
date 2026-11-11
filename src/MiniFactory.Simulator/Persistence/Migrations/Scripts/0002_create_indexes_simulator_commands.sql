-- 依設備與狀態查詢 / ResultReporter 掃描未回報結果（reported_at IS NULL AND result_event_id IS NOT NULL ORDER BY completed_at）
CREATE INDEX IF NOT EXISTS ix_simulator_commands_equipment_id_status ON simulator_commands (equipment_id, status);
CREATE INDEX IF NOT EXISTS ix_simulator_commands_unreported ON simulator_commands (reported_at, completed_at);
