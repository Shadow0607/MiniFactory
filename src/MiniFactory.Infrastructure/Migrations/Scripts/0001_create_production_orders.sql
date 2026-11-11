-- database.md 3.1 生產訂單。PK / 唯一 / CHECK 在此，索引與外鍵由後續腳本加入
CREATE TABLE IF NOT EXISTS production_orders (
  id              UUID          NOT NULL,
  order_no        VARCHAR(50)   NOT NULL,
  product_code    VARCHAR(50)   NOT NULL,
  quantity        INT UNSIGNED  NOT NULL,
  line_id         VARCHAR(32)   NOT NULL,
  status          VARCHAR(32)   NOT NULL,
  version         INT UNSIGNED  NOT NULL DEFAULT 0,
  correlation_id  VARCHAR(64)   NOT NULL,
  trace_parent    VARCHAR(55)   NULL,
  created_at      DATETIME(6)   NOT NULL,
  updated_at      DATETIME(6)   NOT NULL,
  completed_at    DATETIME(6)   NULL,
  CONSTRAINT pk_production_orders PRIMARY KEY (id),
  CONSTRAINT ux_production_orders_order_no UNIQUE (order_no),
  CONSTRAINT ck_production_orders_quantity CHECK (quantity > 0),
  CONSTRAINT ck_production_orders_status CHECK (status IN ('Created','InProgress','AwaitingConfirmation','Completed','NeedsReview'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
