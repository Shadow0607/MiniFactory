# MiniFactory API 規格（第一版）

> 文件狀態：**✅ 已確認（2026-11-11，Wayne）**。端點以 `README.md`「API 草案」為準；規格未列、本文件補上的標註 **【新增】**（全部已確認採用）；仍標【待確認】者為保留預設值的細節，不阻擋實作。
> 2026-11-11 資料層改為 MariaDB：API 合約（路徑、JSON、狀態碼）不變；只有錯誤來源描述、健康檢查 component 名稱（`postgres` → `mariadb`）與新增通用 `409 conflict` 類型隨之調整。
> 範例中的 UUID、時間、traceId 皆為**示意值**（格式正確，非真實資料）。
> 相關：[architecture.md](architecture.md)、[database.md](database.md)

---

## 1. 共通規則

| 項目 | 規則 |
|-|-|
| Base URL | 經 nginx：`http://<host>:8080/api/...`；直連 API：`http://<host>:5080/api/...` |
| 路徑版本 | 依規格使用 `/api/<資源>`，**不含 `/v1`**（✅ 已確認 2026-11-11）；未來若有破壞性變更再引入版本路徑 |
| 格式 | `application/json; charset=utf-8`；欄位 camelCase；enum 以字串表示 |
| 時間 | 全部 UTC，ISO-8601 含 `Z`（規格：前端依瀏覽器時區顯示） |
| ID | UUID（v7）字串 |
| 分頁 | `?page=1&pageSize=20`；`page` ≥ 1、`pageSize` 1～100，**超出範圍回 `400 validation-failed`**（不自動修正）；回應 `{ items, page, pageSize, totalCount }` |
| 列舉型查詢參數 | `status`、`severity` 等只接受**精確的列舉名稱**（大小寫需一致，例 `InProgress`）；未知值或**含逗號**（例 `status=Open,Resolved`）一律 `400`；多值請重複參數（`status=A&status=B`） |
| 請求本文上限 | **64 KB**，超過回 `413` |
| 限流 | policy：寫入端點 `demo-write`（每 IP 每分鐘 10 次）、查詢端點 `public-read`（120 次）【待確認：數值】；設定 `RateLimiting:Enabled=false`（**預設不限制**）。啟用時超量回 `429`（附 `Retry-After`）。限流在 API Key 驗證**之前**執行，錯誤 Key 也計數；分區鍵為來源 IP（`RemoteIpAddress`） |
| Correlation | Request 可帶 `X-Correlation-Id`，未帶由 API 產生；Response 一律回寫 `X-Correlation-Id` |
| Trace | 所有錯誤回應含 `traceId`（W3C traceparent 格式）。API 對外入口**不信任**請求帶入的 `traceparent`/`tracestate`，一律開新的 root trace |
| OpenAPI | Development 環境 `GET /openapi/v1.json`，Scalar UI `/scalar`【待確認是否 Demo 時開放】 |

### 1.1 `X-Api-Key` 套用範圍

Key 由 `ApiKeyMiddleware` 依端點的 `RequireApiKey(ApiKeyKind)` metadata 檢查，在參數繫結**之前**執行，所以無 Key 的請求一律先得到 401。API 啟動時檢查兩把 Key：至少 32 字元、不得含前後空白、至少 10 個不同字元、不得為 `change-me` 等占位值、兩把不得相同，否則拒絕啟動；錯誤訊息不揭露 Key 長度。建議以 `openssl rand -hex 32` 產生。所有需要 API Key 的端點同時標有限流 policy（整合測試守護）。

兩把 Key（✅ 已確認 2026-11-11）：**Operator Key**（`Security:OperatorApiKey`，前端操作）與 **Simulator Key**（`Security:SimulatorApiKey`，設備回報）。端點只接受表中指定的那一把，拿另一把一律 `401`。

| 端點 | `X-Api-Key` | 其他限制 |
|-|-|-|
| `POST /api/orders` | Operator Key | — |
| `POST /api/simulator/equipment/{id}/scenario` | Operator Key | — |
| `POST /api/alerts/{id}/resolve` | Operator Key | — |
| `POST /api/equipment-events` | Simulator Key | 來源 IP 必須在 `Security:EquipmentEventsAllowedIps`（Compose 中為 Simulator 固定 IP）；nginx 不轉送此路徑 |
| 所有 `GET /api/*` | 不需要 | 規格：區網內開放 |
| `/health/live`、`/health/ready` | 不需要 | — |
| `/hubs/dashboard`（含 `POST /hubs/dashboard/negotiate`） | 不需要 | 唯讀推播 |
| `/openapi/*`、`/scalar` | 不需要 | 僅 Development |

- 前端不打包 Key：使用者在 Dashboard 執行寫入操作時輸入 Operator Key，存 `sessionStorage`；收到 `401` 時重新跳出輸入框。

---

## 2. 錯誤格式（RFC 9457 Problem Details）

```json
{
  "type": "https://minifactory.dev/errors/idempotency-conflict",
  "title": "Idempotency key reused with different payload",
  "status": 409,
  "detail": "Idempotency-Key 'create-order-ORD-20260921-001' 已存在但內容不同",
  "instance": "/api/orders",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

### 2.1 Problem `type` URI 清單

| type URI | HTTP | title | 何時發生 | 規格 |
|-|-|-|-|-|
| `https://minifactory.dev/errors/validation-failed` | 400 | One or more validation errors occurred | 欄位驗證失敗；含 `errors` 物件（`ValidationProblemDetails`） | 規格 |
| `https://minifactory.dev/errors/unauthorized` | 401 | Missing or invalid API key | 未帶或錯誤 `X-Api-Key` | 規格 |
| `https://minifactory.dev/errors/forbidden-source` | 403 | Request source not allowed | `POST /api/equipment-events` 來源 IP 不在允許清單 | 【新增】 |
| `https://minifactory.dev/errors/not-found` | 404 | Resource not found | 訂單/設備/命令/告警不存在 | 規格 |
| `https://minifactory.dev/errors/idempotency-conflict` | 409 | Idempotency key reused with different payload | 同 Key 不同內容 | 規格 |
| `https://minifactory.dev/errors/order-no-conflict` | 409 | Order number already exists | `order_no` 唯一約束觸發 | 規格（type 名稱為本文件定義） |
| `https://minifactory.dev/errors/equipment-unavailable` | 409 | Equipment is not available | 設備非 Idle / 忙碌。第一版僅 **Simulator 內部 API** 回傳（§8）；Worker 內部預留失敗不經 HTTP | 規格【待確認】 |
| `https://minifactory.dev/errors/conflict` | 409 | Conflicting data | 其他未被特定處理的唯一約束違反（MariaDB 1062）；正常流程不應出現，出現代表需補特定處理 | 【新增】 |
| `https://minifactory.dev/errors/service-unavailable` | 503 | Dependent service unavailable | MariaDB 不可用（連線失敗/逾時），或死結/鎖等待逾時重試 3 次仍失敗；附 `Retry-After: 5` | 規格（原文為 PostgreSQL） |
| `https://minifactory.dev/errors/simulator-unavailable` | 503 | Simulator unavailable | 設定情境時 Simulator 連不到；附 `Retry-After` | 【新增】 |
| `https://minifactory.dev/errors/equipment-offline` | 503 | Equipment offline | Simulator 內部 API：設備情境 Offline | 【新增】 |
| `https://minifactory.dev/errors/internal-error` | 500 | An unexpected error occurred | 未預期例外；不含堆疊 | 規格 |
| `https://minifactory.dev/errors/http-{status}`（✅ 開發確認，例 `.../http-415`） | 405、413、415、429 等 | 標準 HTTP 狀態說明（例 `Unsupported Media Type`） | **框架產生、沒有專屬業務類型的狀態碼**（方法不允許、本文過大、Content-Type 不支援、限流超量等），統一包成 Problem Details 並帶 `traceId` | 【新增】 |

驗證失敗範例：
```json
{
  "type": "https://minifactory.dev/errors/validation-failed",
  "title": "One or more validation errors occurred",
  "status": 400,
  "errors": {
    "quantity": ["quantity 必須大於 0"],
    "lineId": ["產線 'Line-99' 不存在"]
  },
  "traceId": "00-6e0c63257de34c926f9efcd03899a01f-1a2b3c4d5e6f7081-01"
}
```

---

## 3. 訂單

### 3.1 `POST /api/orders` — 建立訂單

| 項目 | 內容 |
|-|-|
| 權限 | `X-Api-Key`（Operator Key） |
| Headers | `Idempotency-Key`（**選填**，✅ 已確認；1～128 可見 ASCII；保留 24 小時）、`X-Correlation-Id`（選填） |
| 成功 | `202 Accepted` + `Location: /api/orders/{id}` |
| 錯誤 | 400、401、409 idempotency-conflict、409 order-no-conflict、413（本文 > 64 KB）、415、429（啟用限流時）、503、500 |
| 認證順序 | 未帶或錯誤 `X-Api-Key` **一律先回 401**，不會因 body 格式錯誤而先得到 400（API Key 由 middleware 在參數繫結前檢查） |

Request（規格範例）：
```http
POST /api/orders
X-Api-Key: <env>
Idempotency-Key: create-order-ORD-20260921-001
Content-Type: application/json

{
  "orderNo": "ORD-20260921-001",
  "productCode": "Motor-A",
  "quantity": 500,
  "lineId": "Line-01"
}
```

- Idempotency-Key 格式：Dashboard 自動產生 `create-order-{orderNo}`，**保留 orderNo 原大小寫**（✅ 定案；規格範例 `create-order-ord-20260921-001` 為小寫，Key 本身區分大小寫，兩者皆合法，只是前端不再轉小寫）。其他呼叫端可自訂任何符合格式的 Key。

驗證規則：
| 欄位 | 規則 |
|-|-|
| `orderNo` | 必填，trim 後 1～50 字，僅 `A-Z a-z 0-9 - _`【待確認格式】 |
| `productCode` | 必填，1～50 字 |
| `quantity` | 必填，`> 0`（規格）；上限預設 **100000**（設定值，可調）【待確認：業務上限】 |
| `lineId` | 必填，必須是 `equipment` 中存在的 `line_id` |

Response `202`：
```json
{
  "orderId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0001",
  "orderNo": "ORD-20260921-001",
  "status": "Created",
  "dispatchStatus": "Queued",
  "jobIds": [
    "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0002",
    "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0003",
    "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0004"
  ],
  "createdAt": "2026-09-21T02:15:30.123Z"
}
```

- `dispatchStatus`：`Queued`（API 的 RabbitMQ 連線正常，派工事件將由 Outbox 發送；✅ 已確認命名）/ `Pending`（RabbitMQ 不可用，事件暫存 Outbox，恢復後補送；規格）。
  - **Phase 2.3 之前一律回 `Pending`**：此時尚未有 Outbox Publisher 與 broker 連線，事件只寫進 Outbox、不會被發送，回 `Queued` 會誤導。Phase 2.3 接上 RabbitMQ 後才依連線狀態回 `Queued`/`Pending`。
- **Idempotency 請求摘要**（`request_hash`）以下列正規化後欄位計算：`orderNo`（trim，**區分大小寫**）、`productCode`、`quantity`、`lineId`（**使用正規化後的值**）。所以同一 Key 下 `orderNo` 只差大小寫視為內容不同 → `409 idempotency-conflict`；另一方面 `order_no` 唯一約束不分大小寫（database.md §3.1）。
- **同 Key 同內容重送**：回 `202` 與第一次**完全相同**的 body（含第一次的 `dispatchStatus`），並加 header `Idempotent-Replayed: true`。
- **同 Key 不同內容**：`409 idempotency-conflict`。
- **不同 Key（或無 Key）但 orderNo 重複**：`409 order-no-conflict`：
```json
{
  "type": "https://minifactory.dev/errors/order-no-conflict",
  "title": "Order number already exists",
  "status": 409,
  "detail": "訂單編號 'ORD-20260921-001' 已存在",
  "orderNo": "ORD-20260921-001",
  "traceId": "00-..."
}
```

### 3.2 `GET /api/orders` — 訂單列表 【新增】

規格前端有 `/orders` 列表頁，但 API 草案未列列表端點。

| 項目 | 內容 |
|-|-|
| 權限 | 無 |
| Query | `status`（選填，可多值 `status=InProgress&status=AwaitingConfirmation`）、`orderNo`（前綴搜尋，選填）、`page`、`pageSize` |
| 排序 | `createdAt DESC` |
| 回應 | `200`；錯誤 400、503 |

```json
{
  "items": [
    {
      "id": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0001",
      "orderNo": "ORD-20260921-001",
      "productCode": "Motor-A",
      "quantity": 500,
      "lineId": "Line-01",
      "status": "InProgress",
      "completedJobs": 1,
      "totalJobs": 3,
      "version": 4,
      "createdAt": "2026-09-21T02:15:30.123Z",
      "updatedAt": "2026-09-21T02:15:41.870Z"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

### 3.3 `GET /api/orders/{id}` — 訂單與工序進度

| 項目 | 內容 |
|-|-|
| 權限 | 無 |
| 路徑參數 | `id`：訂單 UUID |
| 回應 | `200`；錯誤 404、503 |

```json
{
  "id": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0001",
  "orderNo": "ORD-20260921-001",
  "productCode": "Motor-A",
  "quantity": 500,
  "lineId": "Line-01",
  "status": "AwaitingConfirmation",
  "version": 7,
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
  "createdAt": "2026-09-21T02:15:30.123Z",
  "updatedAt": "2026-09-21T02:16:05.002Z",
  "completedAt": null,
  "jobs": [
    {
      "id": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0002",
      "sequence": 1,
      "operation": "Machining",
      "status": "Completed",
      "assignedEquipment": null,
      "currentAttemptNo": 1,
      "startedAt": "2026-09-21T02:15:31.400Z",
      "completedAt": "2026-09-21T02:15:41.500Z",
      "executions": [
        {
          "attemptNo": 1,
          "commandId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0101",
          "status": "Succeeded",
          "equipment": { "id": "01937f00-0000-7000-8000-00000000000a", "name": "Machine-A" },
          "sendCount": 2,
          "lastSentAt": "2026-09-21T02:15:34.600Z",
          "startedAt": "2026-09-21T02:15:31.400Z",
          "completedAt": "2026-09-21T02:15:41.500Z",
          "errorCode": null
        }
      ]
    },
    {
      "id": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0003",
      "sequence": 2,
      "operation": "Assembly",
      "status": "AwaitingConfirmation",
      "assignedEquipment": { "id": "01937f00-0000-7000-8000-00000000000b", "name": "Machine-B" },
      "currentAttemptNo": 1,
      "startedAt": "2026-09-21T02:15:42.010Z",
      "completedAt": null,
      "executions": [
        {
          "attemptNo": 1,
          "commandId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0102",
          "status": "Unknown",
          "equipment": { "id": "01937f00-0000-7000-8000-00000000000b", "name": "Machine-B" },
          "sendCount": 1,
          "lastSentAt": "2026-09-21T02:15:41.900Z",
          "startedAt": "2026-09-21T02:15:42.010Z",
          "completedAt": null,
          "errorCode": null
        }
      ]
    },
    {
      "id": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0004",
      "sequence": 3,
      "operation": "Inspection",
      "status": "Pending",
      "assignedEquipment": null,
      "currentAttemptNo": 0,
      "startedAt": null,
      "completedAt": null,
      "executions": []
    }
  ]
}
```
- `traceId`：建單 Trace 的 trace-id（32 hex），前端組成 Jaeger 連結。

### 3.4 `GET /api/orders/{id}/events` — 事件時間線

| 項目 | 內容 |
|-|-|
| 權限 | 無 |
| Query | `after`（選填，eventId；增量查詢用）。`after` 指定的事件**不存在或屬於其他訂單**時回 `404 not-found`（不回空清單，避免前端誤以為沒有新事件） |
| 排序 | `occurredAt ASC, id ASC` |
| 回應 | `200`；錯誤 404、503 |

```json
{
  "orderId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0001",
  "items": [
    {
      "eventId": "01937f5a-2c4f-7000-8000-000000000201",
      "eventType": "OrderCreated",
      "eventVersion": 1,
      "occurredAt": "2026-09-21T02:15:30.123Z",
      "payload": { "orderId": "01937f5a-...-0001", "orderNo": "ORD-20260921-001", "jobIds": ["...0002", "...0003", "...0004"] }
    },
    {
      "eventId": "01937f5a-2c4f-7000-8000-000000000202",
      "eventType": "JobDispatchRequested",
      "eventVersion": 1,
      "occurredAt": "2026-09-21T02:15:30.123Z",
      "payload": { "jobId": "...0002", "equipmentId": null, "commandId": "...0101", "attemptNo": 1 }
    },
    {
      "eventId": "01937f5a-2c50-7000-8000-000000000203",
      "eventType": "CommandResent",
      "eventVersion": 1,
      "occurredAt": "2026-09-21T02:15:34.600Z",
      "payload": { "jobId": "...0002", "commandId": "...0101", "attemptNo": 1, "sendCount": 2 }
    }
  ]
}
```

---

## 4. 設備

### 4.1 `GET /api/equipment` — 設備目前狀態

| 項目 | 內容 |
|-|-|
| 權限 | 無 |
| Query | `lineId`（選填） |
| 回應 | `200`；MariaDB 不可用且 Redis 有快照 → `200` + `stale: true`；兩者皆無 → `503` |

```json
{
  "items": [
    {
      "id": "01937f00-0000-7000-8000-00000000000a",
      "name": "Machine-A",
      "lineId": "Line-01",
      "capability": "Machining",
      "status": "Idle",
      "lastSeenAt": "2026-09-21T02:16:04.990Z",
      "statusChangedAt": "2026-09-21T02:15:41.500Z",
      "reservedJobId": null,
      "version": 12
    },
    {
      "id": "01937f00-0000-7000-8000-00000000000b",
      "name": "Machine-B",
      "lineId": "Line-01",
      "capability": "Assembly",
      "status": "Offline",
      "lastSeenAt": "2026-09-21T02:15:48.010Z",
      "statusChangedAt": "2026-09-21T02:16:03.100Z",
      "reservedJobId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0003",
      "version": 9
    }
  ],
  "stale": false,
  "generatedAt": "2026-09-21T02:16:05.010Z"
}
```

---

## 5. 告警

### 5.1 `GET /api/alerts` — 未處理 / 歷史告警

| 項目 | 內容 |
|-|-|
| 權限 | 無 |
| Query | `status`（`Open` / `Resolved`，不帶 = 全部）、`severity`、`orderId`、`equipmentId`、`page`、`pageSize` |
| 排序 | `createdAt DESC` |
| 回應 | `200`；錯誤 400、503 |

```json
{
  "items": [
    {
      "id": "01937f5b-1111-7000-8000-000000000301",
      "severity": "Critical",
      "reason": "EQUIPMENT_OFFLINE",
      "message": "Machine-B 超過 15 秒未回報心跳",
      "status": "Open",
      "relatedOrderId": null,
      "relatedJobId": null,
      "relatedEquipmentId": "01937f00-0000-7000-8000-00000000000b",
      "createdAt": "2026-09-21T02:16:03.100Z",
      "resolvedAt": null,
      "resolvedBy": null,
      "resolutionNote": null
    },
    {
      "id": "01937f5b-1111-7000-8000-000000000302",
      "severity": "Warning",
      "reason": "JOB_UNCONFIRMED",
      "message": "工序 Assembly（ORD-20260921-001）結果待確認",
      "status": "Open",
      "relatedOrderId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0001",
      "relatedJobId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0003",
      "relatedEquipmentId": "01937f00-0000-7000-8000-00000000000b",
      "createdAt": "2026-09-21T02:16:03.100Z",
      "resolvedAt": null,
      "resolvedBy": null,
      "resolutionNote": null
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 2,
  "openCount": 2
}
```
- `openCount`：導覽列未處理告警數（不受分頁/篩選影響）。【新增欄位】
- `resolvedBy`：`System`（自動結案）/ `Operator`（人工結案）/ `null`（未結案）。

### 5.2 `POST /api/alerts/{id}/resolve` — 告警人工結案 【新增】

規格未列；✅ 已確認第一版要做（2026-11-11）。NeedsReview 的人工處置（重派、改狀態）**不做**。

| 項目 | 內容 |
|-|-|
| 權限 | `X-Api-Key`（Operator Key） |
| 路徑參數 | `id`：告警 UUID |
| 行為 | 條件更新 `WHERE status='Open'` → `Resolved`、`resolved_by='Operator'`；同交易寫 outbox `AlertResolved`（訂單相關告警另寫 `order_events`）；commit 後推播 SignalR `AlertResolved` |
| 冪等 | 告警已是 `Resolved`（不論系統或人工結案）→ `200` 回傳目前內容，不覆寫原結案資訊、不重複推播 |
| 副作用限制 | **只關閉告警**，不改變 Job / Order / 設備狀態（例：`JOB_NEEDS_REVIEW` 結案後 Job 仍為 `NeedsReview`） |
| 回應 | `200`；錯誤 400、401、404、503、500 |

Request（body 可省略）：
```json
{ "note": "已確認 Machine-B 為人為斷線，Demo 用" }
```

| 欄位 | 規則 |
|-|-|
| `note` | 選填，最多 500 字 |

Response `200`：
```json
{
  "id": "01937f5b-1111-7000-8000-000000000301",
  "severity": "Critical",
  "reason": "EQUIPMENT_OFFLINE",
  "message": "Machine-B 超過 15 秒未回報心跳",
  "status": "Resolved",
  "relatedOrderId": null,
  "relatedJobId": null,
  "relatedEquipmentId": "01937f00-0000-7000-8000-00000000000b",
  "createdAt": "2026-09-21T02:16:03.100Z",
  "resolvedAt": "2026-09-21T02:18:30.000Z",
  "resolvedBy": "Operator",
  "resolutionNote": "已確認 Machine-B 為人為斷線，Demo 用"
}
```

- 結案「條件仍存在」的告警（如設備仍 Offline）是允許的；告警只在狀態轉換時建立，條件持續期間不會重建（architecture.md §9.5）。前端結案前以確認對話框提醒。

---

## 6. Simulator 回報與情境

### 6.1 `POST /api/equipment-events` — 接收 Simulator 回報

| 項目 | 內容 |
|-|-|
| 權限 | `X-Api-Key`（Simulator Key）+ 來源 IP 限制 |
| Headers | `traceparent`（Simulator 帶回命令的 trace context）、`X-Correlation-Id` |
| 去重 | 依 `eventId`：Redis 前置檢查 → `inbox_messages(consumer_name='api.equipment-events')`；`Heartbeat` 不去重 |
| 回應 | `200`（含 `result`）；錯誤 400、401、403、404、503 |

共同 envelope：
```json
{
  "eventId": "01937f5a-3000-7000-8000-000000000401",
  "eventType": "JobCompleted",
  "eventVersion": 1,
  "occurredAt": "2026-09-21T02:15:41.500Z",
  "equipmentId": "01937f00-0000-7000-8000-00000000000a",
  "payload": { }
}
```

各 `eventType` 的 `payload`：

| eventType | payload | API 行為 |
|-|-|-|
| `JobStarted` | `{ "jobId", "commandId", "startedAt" }` | execution → Running；Job Dispatched/AwaitingConfirmation → Running；Order → InProgress |
| `JobCompleted` | `{ "jobId", "commandId", "completedAt", "result": { } }` | execution → Succeeded；Job → Completed；釋放設備；下一道 Job `Pending→Ready` + 新 execution + `JobDispatchRequested`；最後一道則 Order → Completed |
| `JobFailed` | `{ "jobId", "commandId", "errorCode", "attemptNo", "failedAt" }` | execution → Failed；未達上限 → Job Ready + 新 attempt；達上限 → NeedsReview + 告警 |
| `EquipmentStatusChanged` | `{ "fromStatus", "toStatus" }` | 依 `occurredAt > status_changed_at` 更新設備；寫歷史；Error 告警 / 結案 |
| `Heartbeat` | `{ "status": "Idle" }` | 更新 `last_seen_at`；設備為 Offline 時轉為回報狀態並結案 `EQUIPMENT_OFFLINE`；不寫 inbox |

`JobCompleted` 範例：
```json
{
  "eventId": "01937f5a-3000-7000-8000-000000000401",
  "eventType": "JobCompleted",
  "eventVersion": 1,
  "occurredAt": "2026-09-21T02:15:41.500Z",
  "equipmentId": "01937f00-0000-7000-8000-00000000000a",
  "payload": {
    "jobId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0002",
    "commandId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0101",
    "completedAt": "2026-09-21T02:15:41.500Z",
    "result": { "durationMs": 10000 }
  }
}
```

Response `200`：
```json
{ "eventId": "01937f5a-3000-7000-8000-000000000401", "result": "Applied" }
```

| `result` | 意義 |
|-|-|
| `Applied` | 已套用並改變狀態 |
| `Duplicate` | `eventId` 已處理過（Redis 或 inbox 命中），未做任何事 |
| `Ignored` | 首次收到但不改變狀態：晚到/舊 attempt 的回報、Job 已終態、狀態回報比目前舊。仍寫 inbox，並記 Warning 日誌（規格：不可倒退狀態） |

- `commandId` 對不到任何 execution 或 `equipmentId` 不存在 → `404 not-found`（Simulator 視為 4xx，不重送）。
- 未知 `eventVersion` → `400 validation-failed`。
- MariaDB 不可用 → `503`，Simulator 稍後以**同一 eventId** 重送。
- Simulator 重送規則：只有 5xx / 逾時才重送；4xx 記錄後停止。

### 6.2 `POST /api/simulator/equipment/{id}/scenario` — 設定設備情境

| 項目 | 內容 |
|-|-|
| 權限 | `X-Api-Key`（Operator Key） |
| 路徑參數 | `id`：設備 UUID |
| 行為 | API 驗證設備存在後轉呼叫 Simulator `PUT /sim/equipment/{id}/scenario` |
| 回應 | `200`（回傳 Simulator 套用後的情境）；錯誤 400、401、404、503 simulator-unavailable |
| 備註 | 第二版才限制非 Production 環境（規格）；第一版可用設定 `Simulator:ScenarioEndpointEnabled` 關閉 |

Request：
```json
{ "mode": "Delay", "ackDelayMs": 5000, "executionDurationMs": 10000 }
```
```json
{ "mode": "Error", "errorCode": "SPINDLE_OVERHEAT", "failNextCount": 1, "errorRecoveryMs": 3000 }
```
```json
{ "mode": "Offline" }
```
```json
{ "mode": "Normal" }
```

| 欄位 | 適用 mode | 規則 |
|-|-|-|
| `mode` | 全部 | 必填：`Normal` / `Delay` / `Error` / `Offline` |
| `ackDelayMs` | Delay | 0～60000 |
| `executionDurationMs` | Normal / Delay | 100～300000；未填用 Simulator 預設 |
| `errorCode` | Error | 1～64 字，預設 `SIMULATED_ERROR` |
| `failNextCount` | Error | 1～10，預設 1 |
| `errorRecoveryMs` | Error | 0～60000，預設 3000 |

Response `200`：
```json
{
  "equipmentId": "01937f00-0000-7000-8000-00000000000a",
  "equipmentName": "Machine-A",
  "scenario": { "mode": "Delay", "ackDelayMs": 5000, "executionDurationMs": 10000 },
  "appliedAt": "2026-09-21T02:20:00.000Z"
}
```

---

## 7. SignalR

- Hub URL：`/hubs/dashboard`（經 nginx 同源；支援 WebSockets，fallback SSE/Long Polling）
- 認證：無
- Client → Server 方法：第一版無（全部廣播）。擴充點：`SubscribeOrder(orderId)` 加入群組 `orders:{orderId}`。
- 傳遞語意：**at-least-once、可能亂序**；前端以 `version`（訂單）、`occurredAt`（設備）、`alertId`（告警）去重並忽略舊值。
- 重連後前端應重新 `GET` 目前頁面資料（斷線期間的推播不補送）。

| 事件名稱 | 觸發時機 | payload |
|-|-|-|
| `OrderUpdated` | 訂單或其任一 Job / execution 變化 | 見下 |
| `EquipmentStatusChanged` | 設備狀態轉換、預留/釋放 | 見下 |
| `AlertRaised` | 告警建立 | 見下 |
| `AlertResolved` 【新增】 | 告警結案（系統自動或人工 `POST /api/alerts/{id}/resolve`） | 見下 |

`OrderUpdated`（訂單摘要快照，前端以 `version` 比較；詳情頁收到後重抓 `/events` 增量）：
```json
{
  "orderId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0001",
  "orderNo": "ORD-20260921-001",
  "status": "InProgress",
  "version": 5,
  "updatedAt": "2026-09-21T02:15:42.010Z",
  "jobs": [
    { "jobId": "...0002", "sequence": 1, "operation": "Machining", "status": "Completed", "assignedEquipmentId": null, "currentAttemptNo": 1 },
    { "jobId": "...0003", "sequence": 2, "operation": "Assembly", "status": "Running", "assignedEquipmentId": "...000b", "currentAttemptNo": 1 },
    { "jobId": "...0004", "sequence": 3, "operation": "Inspection", "status": "Pending", "assignedEquipmentId": null, "currentAttemptNo": 0 }
  ]
}
```

`EquipmentStatusChanged`：
```json
{
  "equipmentId": "01937f00-0000-7000-8000-00000000000b",
  "name": "Machine-B",
  "fromStatus": "Running",
  "toStatus": "Offline",
  "reservedJobId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0003",
  "lastSeenAt": "2026-09-21T02:15:48.010Z",
  "occurredAt": "2026-09-21T02:16:03.100Z",
  "version": 9
}
```

`AlertRaised`（欄位依規格事件 payload，另加顯示用欄位）：
```json
{
  "alertId": "01937f5b-1111-7000-8000-000000000301",
  "severity": "Critical",
  "reason": "EQUIPMENT_OFFLINE",
  "message": "Machine-B 超過 15 秒未回報心跳",
  "relatedOrderId": null,
  "relatedEquipmentId": "01937f00-0000-7000-8000-00000000000b",
  "createdAt": "2026-09-21T02:16:03.100Z"
}
```

`AlertResolved`：
```json
{ "alertId": "01937f5b-1111-7000-8000-000000000301", "reason": "EQUIPMENT_OFFLINE", "resolvedAt": "2026-09-21T02:17:10.000Z", "resolvedBy": "System" }
```

---

## 8. Simulator 內部 API（僅 Docker 內部網路）

- Base URL：`http://simulator:8080/sim`
- 呼叫者：Worker（命令、查詢）、API（情境轉呼叫）
- 認證：第一版不驗證（不發佈到主機埠，只在 `backend` 網路可達）。【待確認：是否也加 API Key】
- 回應中的 `status` 為 Simulator 命令狀態：`Accepted` / `Running` / `Succeeded` / `Failed` / `Rejected`

### 8.1 `POST /sim/commands` — 執行命令（以 commandId 去重）

Request：
```json
{
  "commandId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0101",
  "equipmentId": "01937f00-0000-7000-8000-00000000000a",
  "jobId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0002",
  "attemptNo": 1,
  "operation": "Machining",
  "parameters": { "orderNo": "ORD-20260921-001", "productCode": "Motor-A", "quantity": 500 }
}
```
Headers：`traceparent`、`X-Correlation-Id`

| 狀態碼 | 意義 | body |
|-|-|-|
| `202 Accepted` | 新命令，已持久化並排入執行 | `{ "commandId", "status": "Accepted", "acceptedAt", "duplicate": false }` |
| `200 OK` | **commandId 已存在，不重新執行**，回傳目前狀態 | `{ "commandId", "status": "Running", "acceptedAt", "startedAt", "completedAt": null, "errorCode": null, "resultEventId": null, "duplicate": true }` |
| `409 Conflict` | 設備忙碌，已記錄為 `Rejected`（確認未執行） | Problem `equipment-unavailable`，含 `commandId` |
| `503` | 設備情境 Offline，**未持久化**（確認未接收） | Problem `equipment-offline` |
| `400` | 驗證失敗（例：equipmentId 不存在、operation 與設備 capability 不符） | Problem `validation-failed` |

- `Delay` 情境：新命令持久化後延遲 `ackDelayMs` 才回 202；**重複命令立即回 200**，因此 Worker 重送可很快得知「已接收、執行中」。

### 8.2 `GET /sim/commands/{commandId}` — 查詢命令狀態（對帳用）

| 狀態碼 | 意義 |
|-|-|
| `200` | 命令存在 |
| `404` | Simulator 從未收到此命令（Worker 可安全以同一 commandId 重送） |
| `503` | 該設備情境 Offline（結果暫時無法取得） |

```json
{
  "commandId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0102",
  "equipmentId": "01937f00-0000-7000-8000-00000000000b",
  "jobId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0003",
  "attemptNo": 1,
  "status": "Failed",
  "acceptedAt": "2026-09-21T02:15:41.950Z",
  "startedAt": "2026-09-21T02:15:42.010Z",
  "completedAt": "2026-09-21T02:16:00.000Z",
  "errorCode": "EQUIPMENT_OFFLINE",
  "resultEventId": "01937f5a-3000-7000-8000-000000000402",
  "result": null
}
```
- Worker 對帳拿到終態時，以 `resultEventId` 作為 eventId 呼叫 API 端同一套回報處理邏輯；之後 Simulator 正式回報同一 eventId 會得到 `Duplicate`。

### 8.3 `PUT /sim/equipment/{equipmentId}/scenario` — 設定情境
Request / Response 同 §6.2。設備不存在 → `404`。

### 8.4 `GET /sim/equipment` — 查詢模擬設備（除錯用）【新增】
```json
{
  "items": [
    {
      "equipmentId": "01937f00-0000-7000-8000-00000000000a",
      "name": "Machine-A",
      "capability": "Machining",
      "status": "Running",
      "currentCommandId": "01937f5a-2c4e-7a10-8b6d-3f2a1c9e0101",
      "scenario": { "mode": "Normal", "executionDurationMs": 5000 }
    }
  ]
}
```

### 8.5 健康檢查
`GET /health/live`、`GET /health/ready`（同 §9 格式）。

---

## 9. 健康檢查（API / Worker / Simulator）

| 端點 | 用途 | 成功 | 失敗 |
|-|-|-|-|
| `GET /health/live` | Liveness，Docker healthcheck 使用 | `200` | — |
| `GET /health/ready` | Readiness：MariaDB、RabbitMQ、Redis（Simulator 只檢查 MariaDB `minifactory_sim` 與 Redis） | `200` | 任一失敗 `503`（規格；原文為 PostgreSQL） |

`/health/ready` 回應（Dashboard 系統狀態橫幅使用）：
```json
{
  "status": "Unhealthy",
  "checkedAt": "2026-09-21T02:30:00.000Z",
  "components": {
    "mariadb": { "status": "Healthy", "durationMs": 4 },
    "rabbitmq": { "status": "Unhealthy", "durationMs": 2001, "description": "connection refused" },
    "redis": { "status": "Healthy", "durationMs": 1 }
  }
}
```
- `description` 只放錯誤類別，不含主機名稱/帳號/連線字串。

---

## 10. 端點總表

| 方法 | 路徑 | 說明 | 權限 | 成功 | 錯誤碼 | 來源 |
|-|-|-|-|-|-|-|
| POST | `/api/orders` | 建立訂單（Idempotency-Key 選填） | Operator Key | 202 | 400, 401, 409, 503, 500 | 規格 |
| GET | `/api/orders` | 訂單列表 | 無 | 200 | 400, 503 | 【新增】 |
| GET | `/api/orders/{id}` | 訂單與工序進度 | 無 | 200 | 404, 503 | 規格 |
| GET | `/api/orders/{id}/events` | 事件時間線 | 無 | 200 | 404, 503 | 規格 |
| GET | `/api/equipment` | 設備狀態 | 無 | 200 | 503 | 規格 |
| GET | `/api/alerts` | 告警 | 無 | 200 | 400, 503 | 規格 |
| POST | `/api/alerts/{id}/resolve` | 告警人工結案（冪等） | Operator Key | 200 | 400, 401, 404, 503, 500 | 【新增】 |
| POST | `/api/simulator/equipment/{id}/scenario` | 設定情境（轉呼叫 Simulator） | Operator Key | 200 | 400, 401, 404, 503 | 規格 |
| POST | `/api/equipment-events` | Simulator 回報（eventId 去重） | Simulator Key + 來源 IP | 200 | 400, 401, 403, 404, 503 | 規格 |
| GET | `/health/live`、`/health/ready` | 健康檢查 | 無 | 200 | 503 | 規格 |
| — | `/hubs/dashboard` | SignalR | 無 | — | — | 規格 |
| POST | `/sim/commands` | 執行命令（內部） | 內部網路 | 202/200 | 400, 409, 503 | 【新增】 |
| GET | `/sim/commands/{commandId}` | 查詢命令（內部） | 內部網路 | 200 | 404, 503 | 【新增】 |
| PUT | `/sim/equipment/{id}/scenario` | 設定情境（內部） | 內部網路 | 200 | 400, 404 | 【新增】 |
| GET | `/sim/equipment` | 模擬設備清單（內部） | 內部網路 | 200 | — | 【新增】 |
