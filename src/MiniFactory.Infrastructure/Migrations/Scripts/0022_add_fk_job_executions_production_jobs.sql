-- 執行紀錄屬於 Job
ALTER TABLE job_executions
  ADD CONSTRAINT fk_job_executions_production_jobs FOREIGN KEY IF NOT EXISTS fk_job_executions_production_jobs (job_id) REFERENCES production_jobs (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
