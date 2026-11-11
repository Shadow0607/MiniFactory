-- database.md 3.10 建單 Idempotency。Key 區分大小寫且比較精確，所以用 ascii_bin
-- resource_id 為邏輯關聯，不加外鍵
CREATE TABLE IF NOT EXISTS idempotency_keys (
  scope                 VARCHAR(64)        CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  idempotency_key       VARCHAR(128)       CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  request_hash          CHAR(64)           CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  response_status_code  SMALLINT UNSIGNED  NOT NULL,
  response_body         JSON               NOT NULL,
  resource_id           UUID               NULL,
  created_at            DATETIME(6)        NOT NULL,
  expires_at            DATETIME(6)        NOT NULL,
  CONSTRAINT pk_idempotency_keys PRIMARY KEY (scope, idempotency_key),
  CONSTRAINT ck_idempotency_keys_response_body CHECK (json_valid(response_body))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
