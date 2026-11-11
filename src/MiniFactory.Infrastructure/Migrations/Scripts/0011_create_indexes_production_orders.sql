-- 訂單列表依建立時間排序 / 依狀態篩選
CREATE INDEX IF NOT EXISTS ix_production_orders_created_at ON production_orders (created_at);
CREATE INDEX IF NOT EXISTS ix_production_orders_status ON production_orders (status);
