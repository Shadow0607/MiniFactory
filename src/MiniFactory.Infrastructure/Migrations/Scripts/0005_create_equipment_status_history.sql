-- database.md 3.5 設備狀態歷史（大量追加，主鍵用 BIGINT 自增）
CREATE TABLE IF NOT EXISTS equipment_status_history (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  equipment_id  UUID            NOT NULL,
  from_status   VARCHAR(32)     NULL,
  to_status     VARCHAR(32)     NOT NULL,
  source        VARCHAR(32)     NOT NULL,
  occurred_at   DATETIME(6)     NOT NULL,
  CONSTRAINT pk_equipment_status_history PRIMARY KEY (id),
  CONSTRAINT ck_equipment_status_history_to_status CHECK (to_status IN ('Idle','Running','Error','Offline')),
  CONSTRAINT ck_equipment_status_history_source CHECK (source IN ('Report','Heartbeat','HeartbeatTimeout'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
