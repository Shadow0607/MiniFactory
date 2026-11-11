-- 告警關聯訂單（可為 NULL）
ALTER TABLE alerts
  ADD CONSTRAINT fk_alerts_production_orders FOREIGN KEY IF NOT EXISTS fk_alerts_production_orders (order_id) REFERENCES production_orders (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
