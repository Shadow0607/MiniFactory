import type {
  AlertSeverity,
  AlertStatus,
  EquipmentStatus,
  ExecutionStatus,
  JobStatus,
  OrderStatus,
} from '@/api/types'

// Element Plus Tag 的顏色類型
export type TagType = 'primary' | 'success' | 'warning' | 'danger' | 'info'

export interface StatusMeta {
  label: string
  type: TagType
}

export const orderStatusMeta: Record<OrderStatus, StatusMeta> = {
  Created: { label: '已建立', type: 'info' },
  InProgress: { label: '進行中', type: 'primary' },
  AwaitingConfirmation: { label: '待確認', type: 'warning' },
  Completed: { label: '已完成', type: 'success' },
  NeedsReview: { label: '需人工檢視', type: 'danger' },
}

export const jobStatusMeta: Record<JobStatus, StatusMeta> = {
  Pending: { label: '等待中', type: 'info' },
  Ready: { label: '待派工', type: 'info' },
  Dispatched: { label: '已派工', type: 'primary' },
  Running: { label: '執行中', type: 'primary' },
  AwaitingConfirmation: { label: '待確認', type: 'warning' },
  Completed: { label: '已完成', type: 'success' },
  NeedsReview: { label: '需人工檢視', type: 'danger' },
}

export const executionStatusMeta: Record<ExecutionStatus, StatusMeta> = {
  Pending: { label: '未派送', type: 'info' },
  Dispatched: { label: '已送出', type: 'primary' },
  Accepted: { label: '設備已接收', type: 'primary' },
  Running: { label: '執行中', type: 'primary' },
  Unknown: { label: '結果不明（對帳中）', type: 'warning' },
  Succeeded: { label: '成功', type: 'success' },
  Failed: { label: '失敗', type: 'danger' },
  Rejected: { label: '被拒絕', type: 'warning' },
  Abandoned: { label: '已放棄追蹤', type: 'danger' },
}

export const equipmentStatusMeta: Record<EquipmentStatus, StatusMeta> = {
  Idle: { label: '閒置', type: 'success' },
  Running: { label: '運轉中', type: 'primary' },
  Error: { label: '異常', type: 'warning' },
  Offline: { label: '離線', type: 'danger' },
}

export const severityMeta: Record<AlertSeverity, StatusMeta> = {
  Info: { label: '資訊', type: 'info' },
  Warning: { label: '警告', type: 'warning' },
  Critical: { label: '嚴重', type: 'danger' },
}

export const alertStatusMeta: Record<AlertStatus, StatusMeta> = {
  Open: { label: '未處理', type: 'danger' },
  Resolved: { label: '已結案', type: 'success' },
}

/** 取得顯示資訊；後端若新增未知狀態，退回原字串與灰色，不讓畫面壞掉 */
export function resolveMeta(table: Record<string, StatusMeta>, value: string): StatusMeta {
  return table[value] ?? { label: value, type: 'info' }
}

export const ORDER_STATUS_OPTIONS = Object.keys(orderStatusMeta) as OrderStatus[]

/** 訂單終態（architecture.md §9.1）：Completed、NeedsReview 之後不會再轉出，詳情頁可停止輪詢 */
const TERMINAL_ORDER_STATUSES: ReadonlySet<string> = new Set<OrderStatus>(['Completed', 'NeedsReview'])

export function isTerminalOrderStatus(status: string | null | undefined): boolean {
  return status != null && TERMINAL_ORDER_STATUSES.has(status)
}
