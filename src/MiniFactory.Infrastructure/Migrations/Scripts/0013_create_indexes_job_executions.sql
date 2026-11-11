-- Reconciler 掃描：status IN (進行中) AND deadline_at < now
CREATE INDEX IF NOT EXISTS ix_job_executions_status_deadline ON job_executions (status, deadline_at);
