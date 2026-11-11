-- database.md 3.4 設備。與 production_jobs 的循環外鍵在 0028 / 0029 補上
CREATE TABLE IF NOT EXISTS equipment (
  id                 UUID          NOT NULL,
  line_id            VARCHAR(32)   NOT NULL,
  name               VARCHAR(50)   NOT NULL,
  capability         VARCHAR(32)   NOT NULL,
  status             VARCHAR(32)   NOT NULL,
  last_seen_at       DATETIME(6)   NULL,
  status_changed_at  DATETIME(6)   NOT NULL,
  reserved_job_id    UUID          NULL,
  version            INT UNSIGNED  NOT NULL DEFAULT 0,
  created_at         DATETIME(6)   NOT NULL,
  updated_at         DATETIME(6)   NOT NULL,
  CONSTRAINT pk_equipment PRIMARY KEY (id),
  CONSTRAINT ux_equipment_name UNIQUE (name),
  CONSTRAINT ux_equipment_reserved_job_id UNIQUE (reserved_job_id),
  CONSTRAINT ck_equipment_capability CHECK (capability IN ('Machining','Assembly','Inspection')),
  CONSTRAINT ck_equipment_status CHECK (status IN ('Idle','Running','Error','Offline'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
