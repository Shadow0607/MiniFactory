-- database.md 3.9 Inbox。重複訊息以主鍵 1062 偵測
CREATE TABLE IF NOT EXISTS inbox_messages (
  consumer_name  VARCHAR(64)  NOT NULL,
  message_id     UUID         NOT NULL,
  processed_at   DATETIME(6)  NOT NULL,
  CONSTRAINT pk_inbox_messages PRIMARY KEY (consumer_name, message_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
