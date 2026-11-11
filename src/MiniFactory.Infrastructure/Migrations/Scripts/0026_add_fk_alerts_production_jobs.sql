-- 告警關聯 Job（可為 NULL）
ALTER TABLE alerts
  ADD CONSTRAINT fk_alerts_production_jobs FOREIGN KEY IF NOT EXISTS fk_alerts_production_jobs (job_id) REFERENCES production_jobs (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
