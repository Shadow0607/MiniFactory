-- 清理過期資料
CREATE INDEX IF NOT EXISTS ix_idempotency_keys_expires_at ON idempotency_keys (expires_at);
