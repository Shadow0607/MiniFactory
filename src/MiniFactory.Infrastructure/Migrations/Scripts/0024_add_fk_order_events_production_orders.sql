-- 事件屬於訂單
ALTER TABLE order_events
  ADD CONSTRAINT fk_order_events_production_orders FOREIGN KEY IF NOT EXISTS fk_order_events_production_orders (order_id) REFERENCES production_orders (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
