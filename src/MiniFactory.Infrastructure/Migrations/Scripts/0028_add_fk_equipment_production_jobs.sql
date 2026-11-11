-- 循環外鍵之一：設備預留的 Job（兩側皆 nullable，建表後補）
ALTER TABLE equipment
  ADD CONSTRAINT fk_equipment_production_jobs FOREIGN KEY IF NOT EXISTS fk_equipment_production_jobs (reserved_job_id) REFERENCES production_jobs (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
