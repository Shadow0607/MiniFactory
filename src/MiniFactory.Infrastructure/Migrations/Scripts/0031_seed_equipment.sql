-- database.md 5：Line-01 的三台設備，固定 UUID（Simulator 設定檔須使用同一組 ID）
-- 初始狀態 Offline，收到第一次心跳才轉 Idle。時間欄位使用固定的種子時間（遷移不依賴伺服器時鐘）
-- 冪等：重跑時 ON DUPLICATE KEY UPDATE id = id 不改變任何資料
INSERT INTO equipment (id, line_id, name, capability, status, last_seen_at, status_changed_at, reserved_job_id, version, created_at, updated_at)
VALUES
  ('01a1c7fc-2938-7775-9d83-762b1a40f2ed', 'Line-01', 'Machine-A', 'Machining',  'Offline', NULL, '2026-11-11 00:00:00.000000', NULL, 0, '2026-11-11 00:00:00.000000', '2026-11-11 00:00:00.000000'),
  ('01a1c7fc-2943-77c3-88ed-d3e28c659891', 'Line-01', 'Machine-B', 'Assembly',   'Offline', NULL, '2026-11-11 00:00:00.000000', NULL, 0, '2026-11-11 00:00:00.000000', '2026-11-11 00:00:00.000000'),
  ('01a1c7fc-294d-7f72-a080-cd8cbc0c6912', 'Line-01', 'Machine-C', 'Inspection', 'Offline', NULL, '2026-11-11 00:00:00.000000', NULL, 0, '2026-11-11 00:00:00.000000', '2026-11-11 00:00:00.000000')
ON DUPLICATE KEY UPDATE id = id;
