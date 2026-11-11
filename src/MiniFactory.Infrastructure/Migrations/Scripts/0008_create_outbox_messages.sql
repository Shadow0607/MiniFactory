-- database.md 3.8 Outbox。id 即 eventId 與 AMQP MessageId
CREATE TABLE IF NOT EXISTS outbox_messages (
  id                UUID              NOT NULL,
  event_type        VARCHAR(64)       NOT NULL,
  event_version     SMALLINT UNSIGNED NOT NULL DEFAULT 1,
  payload           JSON              NOT NULL,
  aggregate_type    VARCHAR(32)       NOT NULL,
  aggregate_id      UUID              NOT NULL,
  correlation_id    VARCHAR(64)       NULL,
  trace_parent      VARCHAR(55)       NULL,
  trace_state       VARCHAR(256)      NULL,
  occurred_at       DATETIME(6)       NOT NULL,
  published_at      DATETIME(6)       NULL,
  attempt_count     INT UNSIGNED      NOT NULL DEFAULT 0,
  last_attempt_at   DATETIME(6)       NULL,
  last_error        VARCHAR(1000)     NULL,
  dead_lettered_at  DATETIME(6)       NULL,
  CONSTRAINT pk_outbox_messages PRIMARY KEY (id),
  CONSTRAINT ck_outbox_messages_payload CHECK (json_valid(payload))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
