-- Job 屬於訂單
ALTER TABLE production_jobs
  ADD CONSTRAINT fk_production_jobs_production_orders FOREIGN KEY IF NOT EXISTS fk_production_jobs_production_orders (order_id) REFERENCES production_orders (id)
  ON DELETE RESTRICT ON UPDATE RESTRICT;
