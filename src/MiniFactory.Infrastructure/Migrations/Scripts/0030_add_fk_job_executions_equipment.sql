-- 執行紀錄實際使用的設備（歷史資料，不清空）
ALTER TABLE job_executions
  ADD CONSTRAINT fk_job_executions_equipment FOREIGN KEY IF NOT EXISTS fk_job_executions_equipment (equipment_id) REFERENCES equipment (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
