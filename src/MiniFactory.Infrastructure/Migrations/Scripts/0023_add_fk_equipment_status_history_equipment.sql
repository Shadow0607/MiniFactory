-- 狀態歷史屬於設備
ALTER TABLE equipment_status_history
  ADD CONSTRAINT fk_equipment_status_history_equipment FOREIGN KEY IF NOT EXISTS fk_equipment_status_history_equipment (equipment_id) REFERENCES equipment (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
