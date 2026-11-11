// 依 docs/api.md 手寫的 API 型別。時間欄位皆為 UTC ISO-8601 字串（含 Z）。

/** 分頁回應（api.md §1） */
export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

// ---------- 訂單 ----------

export type OrderStatus =
  | 'Created'
  | 'InProgress'
  | 'AwaitingConfirmation'
  | 'Completed'
  | 'NeedsReview'

export type JobStatus =
  | 'Pending'
  | 'Ready'
  | 'Dispatched'
  | 'Running'
  | 'AwaitingConfirmation'
  | 'Completed'
  | 'NeedsReview'

export type ExecutionStatus =
  | 'Pending'
  | 'Dispatched'
  | 'Accepted'
  | 'Running'
  | 'Unknown'
  | 'Succeeded'
  | 'Failed'
  | 'Rejected'
  | 'Abandoned'

/** POST /api/orders 的 request body（api.md §3.1） */
export interface CreateOrderRequest {
  orderNo: string
  productCode: string
  quantity: number
  lineId: string
}

export type DispatchStatus = 'Queued' | 'Pending'

/** POST /api/orders 的 202 回應 */
export interface CreateOrderResponse {
  orderId: string
  orderNo: string
  status: OrderStatus
  dispatchStatus: DispatchStatus
  jobIds: string[]
  createdAt: string
}

/** GET /api/orders 的列表項目（api.md §3.2） */
export interface OrderSummary {
  id: string
  orderNo: string
  productCode: string
  quantity: number
  lineId: string
  status: OrderStatus
  completedJobs: number
  totalJobs: number
  version: number
  createdAt: string
  updatedAt: string
}

export interface EquipmentRef {
  id: string
  name: string
}

export interface JobExecution {
  attemptNo: number
  commandId: string
  status: ExecutionStatus
  equipment: EquipmentRef | null
  sendCount: number
  lastSentAt: string | null
  startedAt: string | null
  completedAt: string | null
  errorCode: string | null
}

export interface Job {
  id: string
  sequence: number
  operation: string
  status: JobStatus
  assignedEquipment: EquipmentRef | null
  currentAttemptNo: number
  startedAt: string | null
  completedAt: string | null
  executions: JobExecution[]
}

/** GET /api/orders/{id}（api.md §3.3） */
export interface OrderDetail {
  id: string
  orderNo: string
  productCode: string
  quantity: number
  lineId: string
  status: OrderStatus
  version: number
  /** 建單 Trace 的 trace-id（32 hex），用來組 Jaeger 連結 */
  traceId: string | null
  createdAt: string
  updatedAt: string
  completedAt: string | null
  jobs: Job[]
}

/** GET /api/orders/{id}/events 的單筆事件（api.md §3.4） */
export interface OrderEvent {
  eventId: string
  /** 例：OrderCreated、JobDispatchRequested、CommandResent */
  eventType: string
  eventVersion: number
  occurredAt: string
  /** 內容依 eventType 而異；顯示時不可以 v-html 輸出 */
  payload: Record<string, unknown>
}

/** GET /api/orders/{id}/events 的回應 */
export interface OrderEventsResponse {
  orderId: string
  items: OrderEvent[]
}

export interface OrderListQuery {
  status?: OrderStatus[]
  orderNo?: string
  page?: number
  pageSize?: number
}

// ---------- 設備 ----------

export type EquipmentStatus = 'Idle' | 'Running' | 'Error' | 'Offline'

export interface Equipment {
  id: string
  name: string
  lineId: string
  capability: string
  status: EquipmentStatus
  lastSeenAt: string | null
  statusChangedAt: string | null
  reservedJobId: string | null
  version: number
}

/** GET /api/equipment（api.md §4.1）；stale=true 代表資料來自 Redis 快照 */
export interface EquipmentListResponse {
  items: Equipment[]
  stale: boolean
  generatedAt: string
}

// ---------- 告警 ----------

export type AlertSeverity = 'Info' | 'Warning' | 'Critical'
export type AlertStatus = 'Open' | 'Resolved'
export type AlertResolvedBy = 'System' | 'Operator'

export interface Alert {
  id: string
  severity: AlertSeverity
  /** 例：EQUIPMENT_OFFLINE、JOB_NEEDS_REVIEW（architecture.md §9.5） */
  reason: string
  /** 後端可能為 null（api.md），顯示時需處理 */
  message: string | null
  status: AlertStatus
  relatedOrderId: string | null
  relatedJobId: string | null
  relatedEquipmentId: string | null
  createdAt: string
  resolvedAt: string | null
  resolvedBy: AlertResolvedBy | null
  resolutionNote: string | null
}

export interface AlertListResponse extends PagedResult<Alert> {
  /** 導覽列未處理告警數，不受分頁/篩選影響 */
  openCount: number
}

export interface AlertListQuery {
  status?: AlertStatus
  severity?: AlertSeverity
  orderId?: string
  equipmentId?: string
  page?: number
  pageSize?: number
}

// ---------- Problem Details（api.md §2） ----------

export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  traceId?: string
  /** 400 validation-failed 才有：欄位名稱 → 錯誤訊息陣列 */
  errors?: Record<string, string[]>
  [extension: string]: unknown
}
