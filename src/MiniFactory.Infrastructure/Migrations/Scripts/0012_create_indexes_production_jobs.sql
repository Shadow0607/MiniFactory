-- 進行中 Job 查詢（PostgreSQL 部分索引的替代做法，見 database.md 1.1）
CREATE INDEX IF NOT EXISTS ix_production_jobs_status ON production_jobs (status);
