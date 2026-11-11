import axios from 'axios'
import type { ProblemDetails } from './types'

/** Problem Details type URI 的最後一段（slug），例：validation-failed */
export function problemSlug(type: string | undefined): string | null {
  if (!type) return null
  const last = type.split('/').filter(Boolean).pop()
  return last ?? null
}

/** 依 slug 提供作業人員看得懂的預設訊息（後端 detail 優先，沒有才用這裡） */
const friendlyBySlug: Record<string, string> = {
  'validation-failed': '資料驗證失敗，請檢查欄位內容',
  unauthorized: 'API Key 未提供或不正確，請重新輸入',
  'forbidden-source': '此來源不允許呼叫該端點',
  'not-found': '找不到指定的資料',
  'idempotency-conflict': '相同的 Idempotency-Key 已用於內容不同的請求，請更換 Key 或還原內容',
  'order-no-conflict': '訂單編號已存在，請改用其他編號',
  'equipment-unavailable': '設備目前無法使用',
  'service-unavailable': '後端相依服務暫時無法使用，請稍後再試',
  'simulator-unavailable': '設備模擬器暫時無法連線，請稍後再試',
  'equipment-offline': '設備離線中',
  'internal-error': '伺服器發生未預期的錯誤',
}

/** 沒有 Problem Details 時，依 HTTP 狀態碼給預設訊息 */
function friendlyByStatus(status: number): string {
  if (status === 401) return friendlyBySlug.unauthorized as string
  if (status === 404) return friendlyBySlug['not-found'] as string
  if (status === 502 || status === 503 || status === 504) {
    return friendlyBySlug['service-unavailable'] as string
  }
  if (status >= 500) return friendlyBySlug['internal-error'] as string
  return `請求失敗（HTTP ${status}）`
}

export interface ApiErrorInit {
  message: string
  status?: number | null
  type?: string | null
  title?: string | null
  detail?: string | null
  traceId?: string | null
  fieldErrors?: Record<string, string[]>
  retryAfterSeconds?: number | null
  isNetworkError?: boolean
  canceled?: boolean
  problem?: ProblemDetails | null
}

/** 統一的 API 錯誤；攔截器 reject 的一律是這個型別，呼叫端不必再解析 axios 錯誤 */
export class ApiError extends Error {
  readonly status: number | null
  readonly type: string | null
  readonly title: string | null
  readonly detail: string | null
  readonly traceId: string | null
  /** 400 validation-failed 的欄位錯誤：欄位名（camelCase）→ 訊息陣列 */
  readonly fieldErrors: Record<string, string[]>
  readonly retryAfterSeconds: number | null
  /** 沒有收到任何 HTTP 回應（斷線、逾時、CORS 等） */
  readonly isNetworkError: boolean
  /** 使用者取消（例如關閉 API Key 輸入框），不應顯示錯誤提示 */
  readonly canceled: boolean
  readonly problem: ProblemDetails | null

  constructor(init: ApiErrorInit) {
    super(init.message)
    this.name = 'ApiError'
    this.status = init.status ?? null
    this.type = init.type ?? null
    this.title = init.title ?? null
    this.detail = init.detail ?? null
    this.traceId = init.traceId ?? null
    this.fieldErrors = init.fieldErrors ?? {}
    this.retryAfterSeconds = init.retryAfterSeconds ?? null
    this.isNetworkError = init.isNetworkError ?? false
    this.canceled = init.canceled ?? false
    this.problem = init.problem ?? null
  }

  get slug(): string | null {
    return problemSlug(this.type ?? undefined)
  }

  get hasFieldErrors(): boolean {
    return Object.keys(this.fieldErrors).length > 0
  }
}

/** 使用者取消輸入 API Key 時用的錯誤 */
export function createCanceledError(message = '已取消操作'): ApiError {
  return new ApiError({ message, canceled: true })
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/** 取出 errors 物件中合法的「欄位 → 字串陣列」 */
function parseFieldErrors(raw: unknown): Record<string, string[]> {
  if (!isRecord(raw)) return {}
  const result: Record<string, string[]> = {}
  for (const [field, messages] of Object.entries(raw)) {
    if (Array.isArray(messages)) {
      const texts = messages.filter((m): m is string => typeof m === 'string')
      if (texts.length > 0) result[field] = texts
    } else if (typeof messages === 'string') {
      result[field] = [messages]
    }
  }
  return result
}

function parseRetryAfter(value: unknown): number | null {
  if (typeof value !== 'string' && typeof value !== 'number') return null
  const n = Number(value)
  return Number.isFinite(n) && n >= 0 ? n : null
}

/** 把任何錯誤（axios、ApiError、其他）轉成 ApiError */
export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error

  if (axios.isAxiosError(error)) {
    const response = error.response
    if (!response) {
      // 沒有回應：斷線、逾時或被中止
      if (axios.isCancel(error)) return createCanceledError()
      const timedOut = error.code === 'ECONNABORTED' || error.code === 'ETIMEDOUT'
      return new ApiError({
        message: timedOut ? '連線逾時，請稍後再試' : '無法連線到伺服器，請確認網路或後端服務是否啟動',
        isNetworkError: true,
      })
    }

    const status = response.status
    const body: unknown = response.data
    const problem = isRecord(body) ? (body as ProblemDetails) : null
    const detail = typeof problem?.detail === 'string' ? problem.detail : null
    const title = typeof problem?.title === 'string' ? problem.title : null
    const type = typeof problem?.type === 'string' ? problem.type : null
    const slug = problemSlug(type ?? undefined)
    const headers = response.headers as Record<string, unknown> | undefined

    return new ApiError({
      message: detail ?? (slug ? friendlyBySlug[slug] : undefined) ?? friendlyByStatus(status),
      status,
      type,
      title,
      detail,
      traceId: typeof problem?.traceId === 'string' ? problem.traceId : null,
      fieldErrors: parseFieldErrors(problem?.errors),
      retryAfterSeconds: parseRetryAfter(headers?.['retry-after']),
      problem,
    })
  }

  const message = error instanceof Error ? error.message : '發生未預期的錯誤'
  return new ApiError({ message })
}

/** 供提示框使用：標題 + 多行內容（欄位錯誤逐行列出） */
export function describeApiError(error: ApiError): { title: string; lines: string[] } {
  if (error.status === 400 && error.hasFieldErrors) {
    const lines = Object.entries(error.fieldErrors).flatMap(([field, messages]) =>
      messages.map((m) => `${field}：${m}`),
    )
    return { title: '資料驗證失敗', lines }
  }
  const lines = [error.message]
  if (error.retryAfterSeconds !== null && error.status === 503) {
    lines.push(`建議 ${error.retryAfterSeconds} 秒後再試`)
  }
  if (error.traceId) lines.push(`追蹤碼：${error.traceId}`)
  return { title: error.status ? `錯誤（${error.status}）` : '連線問題', lines }
}
