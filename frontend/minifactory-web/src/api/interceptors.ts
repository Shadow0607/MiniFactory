import type { AxiosInstance } from 'axios'
import { ApiError, createCanceledError, toApiError } from './errors'
import { generateId } from '@/utils/id'

declare module 'axios' {
  interface AxiosRequestConfig {
    /** true 時錯誤不顯示提示（例如背景輪詢），仍會 reject ApiError */
    silent?: boolean
  }
}

export interface InterceptorDeps {
  /** 取得 Operator API Key；使用者取消輸入時回傳 null */
  getApiKey: () => string | null | Promise<string | null>
  /** 讀取目前已儲存的 Key（不會跳出輸入框）；用來判斷 401 是否來自目前這把 Key */
  peekApiKey: () => string | null
  /** 收到 401 時清除已儲存的 Key，下次寫入會重新要求輸入 */
  clearApiKey: () => void
  /** 顯示錯誤提示 */
  notify: (error: ApiError) => void
}

const WRITE_METHODS = new Set(['post', 'put', 'patch', 'delete'])

/** 取出該請求實際送出的 X-Api-Key（AxiosHeaders 或純物件皆可） */
function readSentApiKey(error: unknown): string | null {
  const headers = (error as { config?: { headers?: unknown } } | null)?.config?.headers
  if (!headers) return null
  const value =
    typeof (headers as { get?: unknown }).get === 'function'
      ? (headers as { get: (name: string) => unknown }).get('X-Api-Key')
      : (headers as Record<string, unknown>)['X-Api-Key']
  return typeof value === 'string' && value !== '' ? value : null
}

/** 安裝共用攔截器：Correlation-Id、寫入時帶 X-Api-Key、Problem Details 轉 ApiError 並提示 */
export function installInterceptors(instance: AxiosInstance, deps: InterceptorDeps): void {
  instance.interceptors.request.use(async (config) => {
    if (!config.headers.has('X-Correlation-Id')) {
      config.headers.set('X-Correlation-Id', generateId())
    }

    const method = (config.method ?? 'get').toLowerCase()
    if (WRITE_METHODS.has(method)) {
      const key = await deps.getApiKey()
      if (!key) throw createCanceledError('尚未輸入 API Key，已取消操作')
      config.headers.set('X-Api-Key', key)
    }
    return config
  })

  instance.interceptors.response.use(
    (response) => response,
    (error: unknown) => {
      const apiError = toApiError(error)
      // 只有「該請求送出的 Key」等於目前儲存的 Key 才清除；
      // 避免晚到的舊請求 401 把使用者剛輸入的新 Key 清掉
      if (apiError.status === 401) {
        const sentKey = readSentApiKey(error)
        if (sentKey && sentKey === deps.peekApiKey()) deps.clearApiKey()
      }
      const silent = (error as { config?: { silent?: boolean } } | null)?.config?.silent === true
      if (!silent && !apiError.canceled) deps.notify(apiError)
      return Promise.reject(apiError)
    },
  )
}
