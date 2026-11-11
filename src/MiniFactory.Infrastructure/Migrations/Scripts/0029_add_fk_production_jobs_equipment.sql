-- 循環外鍵之二：Job 指派的設備（兩側皆 nullable，建表後補）
ALTER TABLE production_jobs
  ADD CONSTRAINT fk_production_jobs_equipment FOREIGN KEY IF NOT EXISTS fk_production_jobs_equipment (assigned_equipment_id) REFERENCES equipment (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
