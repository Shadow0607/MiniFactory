-- 告警關聯設備（可為 NULL）
ALTER TABLE alerts
  ADD CONSTRAINT fk_alerts_equipment FOREIGN KEY IF NOT EXISTS fk_alerts_equipment (equipment_id) REFERENCES equipment (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
