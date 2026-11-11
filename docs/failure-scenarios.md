# MiniFactory 故障情境 → 機制 → 整合測試

> 文件狀態：**✅ 已確認（2026-11-11，Wayne）**。情境 1～8 逐字對應 `README.md`「故障情境與驗收」；情境 9 起為本文件補充（【新增】）。
> 環境：Docker 28.2.2（Linux engine）、Compose v2.37.1 已確認可用，Testcontainers 可在本機、CI 與 Codespaces（docker-in-docker）執行。
> 規格要求：每個故障情境至少一支可重複執行的整合測試（Testcontainers：MariaDB、RabbitMQ、Redis；規格原文為 PostgreSQL，2026-11-11 改為 MariaDB 10.11，見 architecture.md ADR-016）。
> 資料層變更檢查結果：原有測試**沒有**依賴 PostgreSQL 專屬行為作為斷言對象；依賴的是「唯一約束」「條件更新」「SKIP LOCKED」「交易回滾」這些語意，MariaDB 皆有對應（1062、affected rows、10.6+ SKIP LOCKED）。需改寫的只有機制描述與錯誤碼；另新增 §3 情境 13（MariaDB 專屬行為）。
> 相關：[architecture.md](architecture.md)、[database.md](database.md)、[api.md](api.md)

---

## 1. 測試基礎設施

| 項目 | 做法 |
|-|-|
| 測試工具鏈（✅ Phase 1 定案） | `xunit.v3` 4.0.3（內建 `Assert`）+ Microsoft.Testing.Platform（`global.json` 的 `test.runner`）+ `coverlet.MTP` 10.1.0 + `Microsoft.Testing.Extensions.TrxReport`；**無** Shouldly、NSubstitute、`Microsoft.NET.Test.Sdk`、`coverlet.collector`。Application 單元測試用手寫 fake（`tests/MiniFactory.UnitTests/Application/Fakes.cs`） |
| 容器 | `Testcontainers.MariaDb` 4.16.0（映像 `mariadb:10.11.11`，與 NAS 對齊）、`Testcontainers.RabbitMq`、`Testcontainers.Redis`；以 xUnit v3 Assembly Fixture 共用，每個測試類別用獨立 database（`minifactory_t<n>`，以 DbUp 從空庫遷移）/ vhost / Redis DB index 隔離 |
| 資料庫帳號 | 測試也比照正式：遷移用 migrator 帳號、應用程式用 DML-only 帳號，確保權限不足的問題在測試就會暴露 |
| API | `WebApplicationFactory<ApiAssemblyMarker>`（`MiniFactory.Api`；方案中有多個 `Program` 型別，用 marker 避免衝突），環境 `Testing`；來源 IP 允許清單含 loopback；共用 `ApiFixture` |
| Worker | 以 `HostApplicationBuilder` 在測試中啟動 Worker 的 hosted services（可同時起兩個實例） |
| Simulator | `WebApplicationFactory<>` 以 Simulator 專案的 marker 型別為入口（Phase 2.1 建立，命名比照 `ApiAssemblyMarker`），Worker 的 `ISimulatorClient` 指向其 `HttpClient` |
| 斷線模擬 | Toxiproxy 容器擋在 MariaDB / RabbitMQ 前面，以「切斷 / 恢復 proxy」模擬短暫斷線，避免容器重啟後 port 改變【待確認：或改用容器 pause】 |
| 故障注入 | 測試專用介面，僅在 `Testing` 環境註冊：`IOutboxFaultInjector`（發送後、標記前拋例外）、`IDispatchFaultInjector`（Tx A commit 後、送命令前拋例外） |
| 時間 | 需要等逾時的測試把逾時設定縮到數百毫秒（Options），不在測試中 `Thread.Sleep` 長時間；以「輪詢直到條件成立或逾時」的 helper 斷言（`Eventually.AssertAsync`） |
| 命名與位置 | 方法名 `<被測對象>_<情境>_<預期>`。資料夾依實作現況：`Api/`（API 端點，含情境 1）、`Persistence/`、`Migrations/`、`Behavior/`（MariaDB 行為、權限分離）、`Support/`（fixture）；**Phase 3 起**跨 API/Worker/Simulator 的故障情境（2～11）放 `FailureScenarios/` |

---

## 2. 情境對照表（總覽）

| # | 情境（規格） | 主要機制 | 整合測試 |
|-|-|-|-|
| 1 | 建立訂單後 API 回應逾時，使用者用同一 Key 重送 | `idempotency_keys` 與建單同交易，插入遇 1062 即判斷重送；`order_no` 唯一 | `CreateOrder_RetryWithSameIdempotencyKey_ReturnsSameOrderId` 等 4 支 |
| 2 | Outbox 發送後、標記前 Publisher 中斷 | 交易回滾 → 重發同 eventId；Worker Inbox + 續做邏輯；Simulator commandId 去重 | `OutboxPublisher_CrashAfterPublishBeforeMark_DoesNotDispatchTwice` 等 2 支 |
| 3 | 兩個 Worker 同時挑選 Machine-A | 設備條件更新（Idle + reserved_job_id IS NULL + version，affected rows 判斷）+ `assigned_equipment_id` UNIQUE（允許多個 NULL） | `ReserveEquipment_TwoWorkersCompeteForMachineA_OnlyOneJobReserves` 等 2 支 |
| 4 | Simulator 已接收命令但回應逾時 | commandId 先持久化；Reconciler 以同一 commandId 重送；Simulator 插入遇 1062 即回既有狀態、不重複執行 | `SendCommand_SimulatorAckTimeout_ResendsSameCommandIdAndExecutesOnce` 等 2 支 |
| 5 | Machine-A 離線，心跳超過設定期限 | Worker `HeartbeatMonitor` 條件更新 → Offline；Job → AwaitingConfirmation；告警（dedup_key） | `HeartbeatMonitor_HeartbeatExpired_MarksOfflineAndJobAwaitingConfirmation` 等 3 支 |
| 6 | Simulator 恢復後送出重複或晚到事件 | eventId 去重（Redis + inbox）；終態不可倒退；`Pending→Ready` 條件更新只成功一次 | `EquipmentEvents_DuplicateCompletedEvent_DoesNotOpenNextJobTwice` 等 3 支 |
| 7 | RabbitMQ 短暫斷線後恢復 | 建單只寫 DB/Outbox；`dispatchStatus: Pending`；Publisher 重試；Inbox 去重 | `CreateOrder_RabbitMqDown_ReturnsPendingAndDispatchesAfterRecovery` 等 2 支 |
| 8 | MariaDB 短暫斷線後恢復（規格原文：PostgreSQL） | API 503 + Retry-After；Worker nack + 暫停消費 + 探測恢復 | `CreateOrder_DatabaseDown_Returns503WithRetryAfterAndSucceedsOnRetry` 等 3 支 |
| 9 | 【新增】Redis 不可用 | Redis 只是 L1；回退 DB 唯一約束 | `Dedup_RedisDown_FallsBackToDatabaseConstraints` 等 2 支 |
| 10 | 【新增】命令確認失敗後重試 / 達上限 | 只有 Failed/Rejected 才建新 attempt/commandId；上限 → NeedsReview + 告警 | `JobFailed_BelowMaxAttempts_CreatesNewAttemptWithNewCommandId` 等 2 支 |
| 11 | 【新增】Worker 在送命令前當機 | Inbox 衝突時依 execution 狀態續送同一 commandId | `DispatchConsumer_CrashAfterReserveBeforeSend_ResumesWithSameCommandId` |
| 12 | 【新增】告警人工結案（重複點擊 / 與自動結案競爭 / 權限） | 條件更新 `WHERE status='Open'`、冪等 200；Operator Key 限定 | `ResolveAlert_OpenAlert_MarksResolvedByOperatorAndBroadcasts` 等 5 支 |
| 13 | 【新增】MariaDB 專屬行為（死結重試、生成欄位唯一、UNIQUE 多 NULL、遷移可重跑） | `TransactionRunner` 重跑 1213/1205；`open_dedup_key` PERSISTENT + UNIQUE；DbUp journal | `UnitOfWork_DeadlockDetected_RetriesWholeTransaction` 等 5 支 |

---

## 3. 情境詳述

### 情境 1：建立訂單後 API 回應逾時，使用者用同一 Key 重送
- **預期（規格）**：只有一張訂單，回傳同一個 `orderId`。
- **機制**
  1. `POST /api/orders` 在單一交易（READ COMMITTED）中，第一個語句 `INSERT idempotency_keys`，接著訂單 + Job + execution + `order_events` + outbox。
  2. 並行的第二個請求在 InnoDB 唯一索引上等待第一個交易結束，第一個提交後得到 1062。
  3. 衝突時比對 `request_hash`：相同 → 回儲存的 202 回應 + `Idempotent-Replayed: true`；不同 → `409 idempotency-conflict`。
  4. 無 Key 或不同 Key 但 `order_no` 相同 → 插入 `production_orders` 得到 MariaDB 1062 → `409 order-no-conflict`。
- **整合測試**（✅ Phase 1 已實作：`tests/MiniFactory.IntegrationTests/Api/IdempotentOrderCreationTests.cs`）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `CreateOrder_RetryWithSameIdempotencyKey_ReturnsSameOrderId` | 同 Key 同 body 連送 2 次 | 兩次都 202、`orderId` 相同、第二次有 `Idempotent-Replayed: true`；DB `production_orders` 1 筆、`production_jobs` 3 筆、`OrderCreated` outbox 1 筆 |
| `CreateOrder_ConcurrentRequestsWithSameIdempotencyKey_CreatesSingleOrder` | 同 Key 同 body 並行 10 個請求 | 全部 202 且 `orderId` 相同；DB 訂單 1 筆 |
| `CreateOrder_SameIdempotencyKeyDifferentPayload_Returns409IdempotencyConflict` | 同 Key、`quantity` 改為 600 | 409、`type` = `.../idempotency-conflict`；DB 數量仍為 500 |
| `CreateOrder_DuplicateOrderNoWithDifferentKey_Returns409OrderNoConflict` | 不同 Key、相同 `orderNo` | 409、`type` = `.../order-no-conflict`（與上一支 type 不同） |
| `CreateOrder_DuplicateOrderNoWithoutKey_Returns409OrderNoConflict` | 不帶 Key、相同 `orderNo` | 409 order-no-conflict |
| `CreateOrder_OrderNoDiffersOnlyByCase_Returns409OrderNoConflict` | `orderNo` 只差大小寫 | 409 order-no-conflict（`order_no` 唯一約束不分大小寫） |
| `CreateOrder_SameKeyAfterRetentionExpired_IsTreatedAsNewRequest` | Key 過期（24h）後再用 | 視為新請求 |
| `CreateOrder_IdempotencyKeyIsCaseSensitive` | Key 只差大小寫 | 視為不同 Key |

- **單元測試**（Application）：`IdempotencyRequestHasherTests`——對空白/欄位順序不敏感、對值變化敏感；`orderNo` 區分大小寫、`lineId` 使用正規化值（api.md §3.1）。
- **Demo**：`scripts/demo/01-idempotent-create`（同 Key 送兩次，展示同一 orderId 與 DB 計數）。

### 情境 2：Outbox 發送後、標記前 Publisher 中斷
- **預期（規格）**：訊息可再次投遞，Worker 不重複派工。
- **機制**
  1. Publisher 在交易內 `FOR UPDATE SKIP LOCKED` 取批次 → publish（confirms）→ `UPDATE published_at`。中斷 → 交易回滾 → 下輪重發**同一 eventId**。
  2. Worker：Redis `mf:dedup:worker.dispatch:{eventId}` 命中即略過；未命中 → `INSERT inbox_messages` 遇 1062（只回滾該語句，交易續用）→ 已處理則依 execution 狀態判斷（已 Accepted 以後不再送）。
  3. 即使命令被再送一次，也是同一 commandId，Simulator 不重複執行。
- **整合測試**（`FailureScenarios/OutboxRedeliveryTests`）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `OutboxPublisher_CrashAfterPublishBeforeMark_DoesNotDispatchTwice` | 啟用 `IOutboxFaultInjector`：第一批 publish 成功後拋例外；關閉注入讓下一輪正常 | RabbitMQ 收到同一 `MessageId` 2 次；`outbox_messages.published_at` 最終有值；該 Job `job_executions` 只有 1 筆；`simulator_commands` 該 commandId 1 筆且只執行 1 次（Simulator 執行計數器 = 1）；`inbox_messages` 該 eventId 1 筆 |
| `DispatchConsumer_SameMessageDeliveredTwiceWithRedisFlushed_DeduplicatedByInbox` | 直接對 queue 發送同一訊息兩次，第二次前 `FLUSHDB` | 第二次結果為略過；Simulator 執行計數 = 1 |

- **Demo**：以資料庫查詢展示 `inbox_messages` 與 `simulator_commands`（規格 Demo 第 5 步）。

### 情境 3：兩個 Worker 同時挑選 Machine-A
- **預期（規格）**：僅一筆 Job 成功預留該設備。
- **機制**
  1. `UPDATE equipment SET reserved_job_id=@job, version=version+1 WHERE id=@eq AND status='Idle' AND reserved_job_id IS NULL AND version=@v` → 影響 1 列才成功。
  2. 最終保證：`ux_production_jobs_assigned_equipment_id`（一般 UNIQUE，允許多個 NULL，等效原部分唯一索引）；違反為 1062。
  3. 判斷依據為 affected rows（連線字串 `UseAffectedRows=true`）。
  3. 失敗者 rollback → `nack(requeue:false)` → 延遲重試佇列，設備釋放後再試。
- **整合測試**（`FailureScenarios/EquipmentReservationRaceTests`）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `ReserveEquipment_TwoWorkersCompeteForMachineA_OnlyOneJobReserves` | 建兩張訂單（seq 1 都需 Machine-A）；啟動 2 個 Worker 實例；Simulator Machine-A 設長執行時間；以 `Barrier` 讓兩邊的 `ReservationService` 同時執行 | 同一時間只有 1 筆 Job 為 `Dispatched` 且 `assigned_equipment_id = Machine-A`；另一筆維持 `Ready`；`equipment.reserved_job_id` 指向勝出 Job；Simulator 不曾回 409 |
| `ReserveEquipment_ConcurrentAttempts100Times_NeverDoubleBooks` | 直接對 `ReservationService` 並行 N 次（N 個 Ready Job 搶 1 台設備），重複 100 輪 | 每輪恰好 1 個成功；除 1062 與經 `TransactionRunner` 重跑成功的 1213 外無其他例外 |

- 規格錯誤表的「409 設備不可用」：第一版在 Worker 內部轉為延遲重試，不經 HTTP（見 api.md §2.1）。
- **Demo**：`docker compose up -d --scale worker=2`，連續建兩張單，RabbitMQ 管理介面可見重試佇列。

### 情境 4：Simulator 已接收命令但回應逾時
- **預期（規格）**：重送同一 `commandId`，不執行第二次。
- **機制**
  1. commandId 在派工前已存於 `job_executions`。
  2. Worker 首送逾時（`CommandAckTimeoutMs`）→ execution 維持 `Dispatched`、設 `deadline_at`；Reconciler 到期以**同一 commandId** 重送，`send_count++`，寫 `CommandResent` 時間線事件。
  3. Simulator `INSERT INTO simulator_commands`，遇 1062（PK `command_id`）即為既有命令，立即回 `200`（`duplicate: true`）。
- **整合測試**（`FailureScenarios/CommandResendTests`）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `SendCommand_SimulatorAckTimeout_ResendsSameCommandIdAndExecutesOnce` | Machine-A 設 `Delay`（`ackDelayMs` > Worker 逾時）；建單 | `job_executions.send_count >= 2`、只有 1 筆 execution；`simulator_commands` 該 commandId 1 筆；Simulator 執行計數 = 1；Job 最終 `Completed`；`order_events` 有 `CommandResent` |
| `SendCommand_SimulatorNeverReceived_ResendViaReconcilerSucceeds` | 讓 Simulator 首次請求連線失敗（DelegatingHandler 注入） | Reconciler `GET` 得 404 → 重送同一 commandId；Job 完成 |

- **Demo**：規格 Demo 第 2 步（Machine-A 延遲情境，時間線看到 `CommandResent`、執行紀錄 `sendCount`）。

### 情境 5：Machine-A 離線，心跳超過設定期限
- **預期（規格）**：狀態顯示 Offline，Job 進入待確認／告警流程。
- **機制**
  1. Simulator `Offline` 情境停止心跳。
  2. Worker `HeartbeatMonitor`（MariaDB 無 `UPDATE ... RETURNING`）：`SELECT ... WHERE status<>'Offline' AND last_seen_at < @offlineBefore FOR UPDATE SKIP LOCKED` → 逐列條件 `UPDATE ... SET status='Offline'`（affected rows = 1 才續做）。
  3. 同交易：狀態歷史、`EquipmentStatusChanged`、告警 `EQUIPMENT_OFFLINE`（`open_dedup_key` 唯一，1062 即已存在）；預留中的 Job → `AwaitingConfirmation`、execution → `Unknown`、告警 `JOB_UNCONFIRMED`；Order → `AwaitingConfirmation`。
  4. 推播 `EquipmentStatusChanged`、`AlertRaised`、`OrderUpdated`（經 outbox → API 通知消費者）。
- **整合測試**（`FailureScenarios/EquipmentOfflineTests`）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `HeartbeatMonitor_HeartbeatExpired_MarksOfflineAndJobAwaitingConfirmation` | Machine-A 執行中設 `Offline`；門檻縮為 1 秒 | 設備 `Offline`；歷史 1 筆 `HeartbeatTimeout`；Job `AwaitingConfirmation`；Order `AwaitingConfirmation`；告警 `EQUIPMENT_OFFLINE`、`JOB_UNCONFIRMED` 各 1 筆 Open |
| `HeartbeatMonitor_RunsOnTwoWorkers_CreatesSingleOfflineAlert` | 兩個 Worker 實例同時跑監控多輪 | `EQUIPMENT_OFFLINE` Open 告警恰 1 筆；歷史恰 1 筆 |
| `EquipmentRecovery_AfterOffline_ResolvesAlertAndCompletesJobWithNewAttempt` | 接上一情境，設回 `Normal` | 設備離開 Offline、`EQUIPMENT_OFFLINE` Resolved；Reconciler 查到命令 `Failed(EQUIPMENT_OFFLINE)` → attempt 2 新 commandId → Job `Completed`；`JOB_UNCONFIRMED` Resolved；Order 最終 `Completed` |

- **SignalR 驗證**（同類別，選做）：以 `HubConnection` 連到測試 API，斷言收到 `EquipmentStatusChanged(toStatus=Offline)` 與 `AlertRaised`。
- **Demo**：規格 Demo 第 3、4 步。

### 情境 6：Simulator 恢復後送出重複或晚到事件
- **預期（規格）**：保留正確終態與事件歷史，不重複開啟下一工序。
- **機制**
  1. `eventId` 去重：Redis → `inbox_messages(api.equipment-events)`。重複回 `result: Duplicate`。
  2. 首次收到但已過時（舊 attempt、Job 已終態、`occurredAt` 舊於目前狀態）→ `result: Ignored`，寫 inbox 與日誌，不改狀態。
  3. 下一道 Job `UPDATE ... SET status='Ready' WHERE id=@next AND status='Pending'` 只會成功一次；`(job_id, attempt_no)` 唯一保證 execution 不重建。
  4. Reconciler 以 `resultEventId` 套用的結果，與 Simulator 事後正式回報同一 eventId → `Duplicate`。
- **整合測試**（`FailureScenarios/LateAndDuplicateEventTests`）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `EquipmentEvents_DuplicateCompletedEvent_DoesNotOpenNextJobTwice` | 對 seq 1 送同一 `JobCompleted` eventId 3 次（含並行） | 回應 1 次 `Applied`、其餘 `Duplicate`；seq 2 只有 1 筆 execution；`JobDispatchRequested`（seq 2）outbox 1 筆 |
| `EquipmentEvents_LateStartedAfterCompleted_KeepsCompletedState` | 先送 `JobCompleted`，再送較早 `occurredAt` 的 `JobStarted`（不同 eventId） | 第二次回 `Ignored`；Job 仍 `Completed`；時間線無倒退事件 |
| `EquipmentEvents_ReconcilerAppliedThenSimulatorReports_TreatedAsDuplicate` | Reconciler 先以 `resultEventId` 套用，再由 Simulator 回報同一 eventId | 第二次 `Duplicate`；狀態不變 |

- **單元測試**：Job/Execution/Order 狀態機「終態不可轉出」與所有非法轉換。

### 情境 7：RabbitMQ 短暫斷線後恢復
- **預期（規格）**：訂單可正常建立、標示 `Pending`；恢復後 Outbox 補送，不遺漏、不重派。
- **機制**
  1. 建單不碰 RabbitMQ，只寫 DB + Outbox；`dispatchStatus` 依 API 的 `IBrokerStatus` 回 `Pending`。
  2. Publisher 整批失敗不標記、`attempt_count++`；達門檻建 `OUTBOX_PUBLISH_FAILING` 告警；恢復後補送。
  3. 補送若與先前部分成功重疊，由 Inbox 去重 → 不重派。
- **整合測試**（`FailureScenarios/RabbitMqOutageTests`）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `CreateOrder_RabbitMqDown_ReturnsPendingAndDispatchesAfterRecovery` | Toxiproxy 切斷 RabbitMQ；建 3 張單；恢復 | 斷線期間 202 + `dispatchStatus: "Pending"`、outbox 未發布筆數 = 6；恢復後全部 `published_at` 有值；3 張單皆完成；每個 Job 只有 1 筆 execution |
| `OutboxPublisher_BrokerDownBeyondThreshold_RaisesSingleAlertAndResolvesOnRecovery` | 斷線時間拉長至 `attempt_count` 超過門檻；恢復 | `OUTBOX_PUBLISH_FAILING` Open 1 筆 → 恢復後 Resolved；無訊息被 `dead_lettered_at`（broker 斷線不算毒訊息） |

### 情境 8：MariaDB 短暫斷線後恢復（規格原文：PostgreSQL 短暫斷線後恢復）
- **預期（規格）**：寫入端點回 `503` 並可重試；Worker 暫停消費、恢復後從斷點續處理，不跳過事件。
- **機制**
  1. API：連線類例外 → `503 service-unavailable` + `Retry-After`；不啟用 EF retry strategy。
  2. `GET /api/equipment` 回 Redis `last-known` + `stale: true`。
  3. Worker：處理中遇 DB 失敗 → `nack(requeue:true)` → `BasicCancel` 暫停 → 每 5 秒探測 → 恢復後 `BasicConsume`。
  4. Simulator 回報遇 503 → 以同一 eventId 稍後重送。
- **整合測試**（`FailureScenarios/DatabaseOutageTests`；以 Toxiproxy 切斷 MariaDB 3306）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `CreateOrder_DatabaseDown_Returns503WithRetryAfterAndSucceedsOnRetry` | 切斷 MariaDB；建單；恢復；同 Key 重送 | 第一次 503、`type` = `.../service-unavailable`、有 `Retry-After`；重送 202；訂單 1 筆 |
| `GetEquipment_DatabaseDown_ReturnsCachedSnapshotWithStaleFlag` | 先查一次（建快照）→ 切斷 → 再查 | 200、`stale: true`、內容與快照一致 |
| `DispatchConsumer_DatabaseDown_PausesConsumingAndResumesWithoutSkipping` | 佇列中有 3 則派工訊息時切斷 MariaDB；恢復 | 斷線期間 queue 的 ready 訊息數不減（未被 ack 丟失）；恢復後 3 個 Job 都 `Dispatched` 以上；Worker 日誌有 pause/resume |

### 情境 9：Redis 不可用 【新增】
- **規格依據**：「Redis 不可用」降級行為、「Redis 只是效能優化層」。
- **機制**：Redis 操作設短逾時並吞例外記 Warning；去重回退 DB 唯一約束；設備查詢直接讀 DB。

| 測試名稱 | 斷言 |
|-|-|
| `Dedup_RedisDown_FallsBackToDatabaseConstraints` | 停 Redis 後送重複 `eventId` → 仍回 `Duplicate`；重複派工訊息不重派 |
| `GetEquipment_RedisDown_ReadsFromDatabase` | 200、`stale: false` |

### 情境 10：命令確認失敗後重試 / 達上限 【新增】
- **規格依據**：§重試 5。

| 測試名稱 | 斷言 |
|-|-|
| `JobFailed_BelowMaxAttempts_CreatesNewAttemptWithNewCommandId` | Machine-A `Error`（failNextCount=1）→ attempt 1 Failed、attempt 2 新 commandId → Job Completed |
| `JobFailed_ReachesMaxAttempts_MarksNeedsReviewAndRaisesAlert` | failNextCount ≥ MaxAttempts → Job `NeedsReview`、Order `NeedsReview`、`JOB_NEEDS_REVIEW` 告警 1 筆；不再派工 |

### 情境 11：Worker 在預留後、送命令前當機 【新增】
- **機制**：Inbox 衝突時不直接略過，依 execution 狀態（仍 `Dispatched` 且 `send_count = 0`）續送同一 commandId；Reconciler 兜底。

| 測試名稱 | 斷言 |
|-|-|
| `DispatchConsumer_CrashAfterReserveBeforeSend_ResumesWithSameCommandId` | 啟用 `IDispatchFaultInjector`；訊息重投遞後命令送出、commandId 不變、Job 完成 |

### 情境 12：告警人工結案 【新增】
- **依據**：Wayne 確認第一版提供 `POST /api/alerts/{id}/resolve`（api.md §5.2、architecture.md §9.5）；實作於 Phase 4.1。
- **機制**：條件更新 `WHERE id=@id AND status='Open'`；影響 0 列且已 Resolved → 冪等 200；同交易寫 outbox `AlertResolved`；只關閉告警，不改變 Job/Order/設備狀態；端點只接受 Operator Key。
- **整合測試**（`AlertResolutionTests`）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `ResolveAlert_OpenAlert_MarksResolvedByOperatorAndBroadcasts` | 製造 `JOB_NEEDS_REVIEW` 告警（情境 10）後以 Operator Key 結案並附 note | 200、`status=Resolved`、`resolvedBy=Operator`、`resolutionNote` 正確；outbox 有 `AlertResolved` 1 筆；SignalR 收到 `AlertResolved`；**Job 與 Order 仍為 `NeedsReview`** |
| `ResolveAlert_CalledTwiceConcurrently_IsIdempotent` | 同一告警並行送 2 次結案 | 兩次都 200、回傳內容相同；`resolved_at` 只寫一次；`AlertResolved` outbox 只有 1 筆 |
| `ResolveAlert_RacingWithAutoResolve_KeepsFirstResolution` | 設備恢復觸發自動結案的同時送人工結案 | 最終只有一種 `resolvedBy`（先成功者）；另一方不覆寫；`AlertResolved` outbox 1 筆 |
| `ResolveAlert_WhileConditionPersists_DoesNotRecreateUntilNextTransition` | 設備仍 Offline 時人工結案 `EQUIPMENT_OFFLINE`；HeartbeatMonitor 再跑數輪；之後恢復再離線 | 持續 Offline 期間不新增告警；下一次轉為 Offline 才新增 1 筆 Open |
| `ResolveAlert_WithoutKeyOrWithSimulatorKey_Returns401` | 不帶 Key、帶 Simulator Key 各送一次 | 皆 401、`type` = `.../unauthorized`；告警仍 Open |

另補 API Key 分流驗證（`ApiKeySeparationTests`）：`EquipmentEvents_WithOperatorKey_Returns401`、`CreateOrder_WithSimulatorKey_Returns401`。

- **Demo**：規格 Demo 第 4 步恢復設備後，在告警頁示範人工結案殘留告警。

### 情境 13：MariaDB 專屬行為 【新增，2026-11-11 資料層變更】
- **依據**：architecture.md §10.7（UoW、READ COMMITTED、錯誤碼）、database.md §1.1（部分索引的替代做法）、ADR-018（DbUp）。
- **整合測試**（✅ Phase 1 已實作：`Behavior/MariaDbBehaviorTests`，另有 `Persistence/UnitOfWorkTests`、`Migrations/MigrationTests`、`Migrations/SchemaTests`、`Migrations/ExplainTests`、`Behavior/RoleSeparationTests`）

| 測試名稱 | 步驟 | 斷言 |
|-|-|-|
| `UnitOfWork_DeadlockDetected_RetriesWholeTransaction` | 兩個交易以相反順序更新兩筆 `equipment` 列，人為製造死結 | 其中一方收到 1213 後由 `TransactionRunner` 整段重跑成功；最終資料正確、無部分寫入 |
| `UnitOfWork_DuplicateKeyInsideTransaction_TransactionRemainsUsable` | 同一交易內先插入重複 inbox（1062），再寫其他資料並提交 | 1062 只回滾該語句；其他寫入成功提交（驗證「inbox 1062 後同交易續做」的前提） |
| `Alerts_GeneratedOpenDedupKey_AllowsOnlyOneOpenPerKey` | 同 `dedup_key` 插入第二筆 Open → 結案第一筆 → 再插入 | 第二筆 1062；結案後可插入；`open_dedup_key` 隨狀態變為 NULL |
| `Equipment_UniqueNullableColumns_AllowMultipleNulls` | 多筆 `assigned_equipment_id`/`reserved_job_id` 為 NULL；再讓兩筆指向同一設備 | NULL 可並存；同值第二筆 1062 |
| `Outbox_SkipLocked_ConcurrentBatchesDoNotOverlap` | 兩個交易同時取 outbox 批次 | 兩批無重疊 |
| `Migrations_FromEmptyDatabase_AppliedEveryScriptInOrderViaMfMigrator`、`Migrations_SecondRun_ExecutesNothing`、`Migrations_ScriptsThemselvesAreRerunnable_WhenJournalIsLost`（`MigrationTests`） | 空 database 以 `mf_migrator` 跑 DbUp；再跑一次；刪 journal 後重跑 | 第一次全部依序成功、第二次無腳本執行；腳本本身可重跑 |
| `Indexes_MatchDatabaseMd_ExactlyWithPrimaryAndUniqueConstraints` 等（`SchemaTests`） | 比對 `information_schema` 的表、索引（含 FK 自動索引 `fk_*`）、外鍵、CHECK、生成欄位、UUID / DATETIME(6) 欄位 | 與 database.md 一致 |

另：`Persistence_UuidRoundTrip_GuidEqualsAfterReadBack`（✅ 已通過）驗證 MariaDB 原生 `UUID` 與 `Guid` 的寫入/讀回、UUIDv7 字串顯示；結果為不需 `GuidFormat`、不需 `CHAR(36)` 退路。

---

## 4. 單元測試範圍（對應規格「測試覆蓋目標」）

| 對象 | 測試類別 | 狀態 |
|-|-|-|
| Order / Job / Execution / Equipment / Alert 狀態機合法與非法轉換 | `OrderStateMachineTests`、`JobStateMachineTests`、`ExecutionStateMachineTests`、`EquipmentStateMachineTests`、`AlertStateMachineTests`（`UnitTests/Domain/StateMachineTests.cs`） | ✅ Phase 1 |
| 實體行為與事件 envelope | `ProductionOrderTests`、`ProductionJobTests`、`JobExecutionTests`、`EquipmentTests`、`AlertTests`、`ProcessRouteTests`、`EventEnvelopeTests`、`DomainIsolationTests` | ✅ Phase 1 |
| 狀態字串與 enum 一致 | 單元：`StatusValuesTests`（`*_MatchesCheckConstraint`）；整合：`SchemaTests.CheckConstraints_StatusValueLists_MatchDomainEnums`（比對實際 DB 的 CHECK 清單） | ✅ Phase 1 |
| Idempotency 請求摘要比對 | `IdempotencyRequestHasherTests` | ✅ Phase 1 |
| 建單驗證與 handler | `CreateOrderValidatorTests`、`CreateOrderHandlerTests` | ✅ Phase 1 |
| JSON 序列化、分頁與查詢參數驗證 | `JsonDefaultsTests`、`PagingTests`、`QueryValidationTests` | ✅ Phase 1 |
| 訂單狀態計算（由 Job 狀態推導） | `OrderStatusCalculatorTests` | Phase 2/3 |
| 重試決策（何時新 attempt、何時 NeedsReview） | `RetryPolicyTests` | Phase 3 |
| commandId / eventId 規則（重送沿用、確認失敗才換新） | `CommandIdentityRulesTests` | Phase 2/3 |
| 回報是否過時（occurredAt 比較、舊 attempt） | `EquipmentReportStalenessTests` | Phase 2/3 |

目標：Domain 層行覆蓋率 ≥ 80%（規格）。覆蓋率指令（✅ Phase 1 定案，Microsoft.Testing.Platform + coverlet.MTP）：

```bash
dotnet test --coverlet --coverlet-output-format cobertura
```

規格寫的 `--collect:"XPlat Code Coverage"` 是 VSTest + coverlet.collector 的用法，在本專案的 MTP 執行器下**已失效**（README 待同步，architecture.md §20.1 #19）。

### 4.1 前端元件測試的取捨（✅ 已定案，記錄已知缺口）
- 前端元件測試（Vitest + `@vue/test-utils` + happy-dom）中，Element Plus 的 `ElSelect`、`ElInputNumber` 以 **stub** 取代：這兩個元件在 happy-dom 下的彈出層與鍵盤行為不穩定，stub 讓測試專注在本專案自己的表單邏輯與 API 呼叫。
- 因此下列**真實互動未被覆蓋**：
  - `el-select` 以 `allow-create` 建立新選項（例如輸入不在清單中的產品代碼）；
  - `el-input-number` 輸入超過上限時夾回上限（例如 quantity 超過 `Orders:MaxQuantity`）。
- 後端仍有對應的驗證與整合測試（超出上限回 400），所以缺口只在「前端互動是否如預期」。
- **列為 E2E 待補（Phase 4，Demo 腳本或瀏覽器 E2E 工具【待確認：工具選擇】）**。

---

## 5. Demo 腳本對應（Phase 4.3，`scripts/demo/*.sh`）

腳本語言：**只做 bash 一份**（✅ 已確認 2026-11-11），可在 Git Bash（Windows）、GitHub Codespaces、Linux 執行。相依工具僅 `curl`、`jq`（Codespaces 映像需確認已安裝，否則由 post-create 安裝）；Operator Key 由 `.env` 讀取，腳本不在輸出中印出 Key。

| 規格 Demo 步驟 | 腳本 | 對應情境 |
|-|-|-|
| （前置）建立示範資料 | `seed-demo.sh`（冪等；Codespaces post-create 自動執行） | 1 |
| 1. 建立訂單，查看三道工序與設備 | `01-create-order.sh` | 1 |
| 2. Machine-A 延遲：逾時、同一命令重送、執行紀錄 | `02-delay-machine-a.sh` | 4 |
| 3. Machine-B 離線：告警、訂單等待確認 | `03-offline-machine-b.sh` | 5 |
| 4. 恢復設備、完成剩餘工序、時間線與 Trace（含告警人工結案示範） | `04-recover-and-trace.sh` | 5、6、12 |
| 5. 證明重複請求沒有第二次派工 | `05-prove-no-duplicate.sh`（重送建單 + 查 `inbox_messages`/`simulator_commands`） | 1、2 |

【待確認：Demo 情境下的逾時參數是否另用 `docker-compose.demo.yml` 調短；預設沿用 architecture.md §14.10 的值】
