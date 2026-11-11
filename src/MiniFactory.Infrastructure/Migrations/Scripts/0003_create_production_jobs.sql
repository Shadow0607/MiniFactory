-- database.md 3.2 生產工單 Job。外鍵由後續腳本加入
CREATE TABLE IF NOT EXISTS production_jobs (
  id                     UUID              NOT NULL,
  order_id               UUID              NOT NULL,
  sequence               SMALLINT UNSIGNED NOT NULL,
  operation              VARCHAR(32)       NOT NULL,
  status                 VARCHAR(32)       NOT NULL,
  assigned_equipment_id  UUID              NULL,
  current_attempt_no     INT UNSIGNED      NOT NULL DEFAULT 0,
  version                INT UNSIGNED      NOT NULL DEFAULT 0,
  created_at             DATETIME(6)       NOT NULL,
  updated_at             DATETIME(6)       NOT NULL,
  started_at             DATETIME(6)       NULL,
  completed_at           DATETIME(6)       NULL,
  CONSTRAINT pk_production_jobs PRIMARY KEY (id),
  CONSTRAINT ux_production_jobs_order_id_sequence UNIQUE (order_id, sequence),
  CONSTRAINT ux_production_jobs_assigned_equipment_id UNIQUE (assigned_equipment_id),
  CONSTRAINT ck_production_jobs_sequence CHECK (sequence >= 1),
  CONSTRAINT ck_production_jobs_operation CHECK (operation IN ('Machining','Assembly','Inspection')),
  CONSTRAINT ck_production_jobs_status CHECK (status IN ('Pending','Ready','Dispatched','Running','AwaitingConfirmation','Completed','NeedsReview'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
