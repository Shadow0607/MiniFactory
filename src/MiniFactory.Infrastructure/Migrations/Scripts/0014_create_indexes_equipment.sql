-- 依產線與能力挑選設備 / 心跳逾時掃描
CREATE INDEX IF NOT EXISTS ix_equipment_line_id_capability ON equipment (line_id, capability);
CREATE INDEX IF NOT EXISTS ix_equipment_status_last_seen_at ON equipment (status, last_seen_at);
