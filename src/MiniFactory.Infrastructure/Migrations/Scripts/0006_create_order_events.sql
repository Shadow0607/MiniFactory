-- database.md 3.6 訂單事件時間線。id 即 eventId，與 outbox_messages.id 相同
CREATE TABLE IF NOT EXISTS order_events (
  id             UUID              NOT NULL,
  order_id       UUID              NOT NULL,
  event_type     VARCHAR(64)       NOT NULL,
  event_version  SMALLINT UNSIGNED NOT NULL DEFAULT 1,
  payload        JSON              NOT NULL,
  occurred_at    DATETIME(6)       NOT NULL,
  CONSTRAINT pk_order_events PRIMARY KEY (id),
  CONSTRAINT ck_order_events_payload CHECK (json_valid(payload))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
