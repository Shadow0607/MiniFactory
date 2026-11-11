-- database.md 3.7 告警。open_dedup_key 為生成欄位（PERSISTENT），取代 PostgreSQL 的部分唯一索引
-- 生成欄位不可被 INSERT 或 UPDATE 直接寫入，Dapper 的 SQL 不得列出它
CREATE TABLE IF NOT EXISTS alerts (
  id               UUID          NOT NULL,
  order_id         UUID          NULL,
  job_id           UUID          NULL,
  equipment_id     UUID          NULL,
  severity         VARCHAR(16)   NOT NULL,
  reason           VARCHAR(64)   NOT NULL,
  message          VARCHAR(500)  NULL,
  status           VARCHAR(16)   NOT NULL,
  dedup_key        VARCHAR(200)  NOT NULL,
  open_dedup_key   VARCHAR(200)  AS (IF(status = 'Open', dedup_key, NULL)) PERSISTENT,
  created_at       DATETIME(6)   NOT NULL,
  resolved_at      DATETIME(6)   NULL,
  resolved_by      VARCHAR(16)   NULL,
  resolution_note  VARCHAR(500)  NULL,
  updated_at       DATETIME(6)   NOT NULL,
  CONSTRAINT pk_alerts PRIMARY KEY (id),
  CONSTRAINT ux_alerts_open_dedup_key UNIQUE (open_dedup_key),
  CONSTRAINT ck_alerts_severity CHECK (severity IN ('Info','Warning','Critical')),
  CONSTRAINT ck_alerts_status CHECK (status IN ('Open','Resolved')),
  CONSTRAINT ck_alerts_resolved_by CHECK (resolved_by IS NULL OR resolved_by IN ('System','Operator')),
  CONSTRAINT ck_alerts_resolved_at CHECK ((status = 'Resolved') = (resolved_at IS NOT NULL)),
  CONSTRAINT ck_alerts_resolved_by_set CHECK ((status = 'Resolved') = (resolved_by IS NOT NULL))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
