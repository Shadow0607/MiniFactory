// 執行期設定：啟動時讀取 /config.json（由 nginx 容器依環境變數產生）。
// 不把環境相關網址編進 bundle（architecture.md §15）。

export interface RuntimeConfig {
  /** API 基底路徑；同源部署為 '/api' */
  apiBaseUrl: string
  /** 「在 Jaeger 開啟」連結的基底網址；未設定則不顯示連結 */
  jaegerUiUrl: string | null
  /** 功能旗標：後端尚未實作的功能先以旗標關閉，待後端上線再於 config.json 開啟 */
  features: FeatureFlags
}

export interface FeatureFlags {
  /** 告警「結案」（POST /api/alerts/{id}/resolve，後端 Phase 4.1 才實作）；預設 false */
  alertResolve: boolean
}

export const defaultRuntimeConfig: RuntimeConfig = {
  apiBaseUrl: '/api',
  jaegerUiUrl: null,
  features: { alertResolve: false },
}

let current: RuntimeConfig = { ...defaultRuntimeConfig }

/** 把 config.json 的原始內容正規化；欄位缺漏或型別不對就用預設值 */
export function normalizeRuntimeConfig(raw: unknown): RuntimeConfig {
  const obj = (typeof raw === 'object' && raw !== null ? raw : {}) as Record<string, unknown>
  const apiBaseUrl =
    typeof obj.apiBaseUrl === 'string' && obj.apiBaseUrl.trim() !== ''
      ? obj.apiBaseUrl.trim().replace(/\/+$/, '')
      : defaultRuntimeConfig.apiBaseUrl
  const jaegerUiUrl =
    typeof obj.jaegerUiUrl === 'string' && obj.jaegerUiUrl.trim() !== ''
      ? obj.jaegerUiUrl.trim()
      : null
  const rawFeatures =
    typeof obj.features === 'object' && obj.features !== null
      ? (obj.features as Record<string, unknown>)
      : {}
  // 只有明確的布林 true 才開啟；字串 "true" 等其他型別一律視為關閉
  const features: FeatureFlags = { alertResolve: rawFeatures.alertResolve === true }
  return { apiBaseUrl, jaegerUiUrl, features }
}

/** 啟動時呼叫一次；讀不到 config.json 時退回預設值，不阻擋應用程式啟動 */
export async function loadRuntimeConfig(fetchFn: typeof fetch = fetch): Promise<RuntimeConfig> {
  try {
    const res = await fetchFn('/config.json', { cache: 'no-store' })
    if (res.ok) {
      current = normalizeRuntimeConfig(await res.json())
    }
  } catch {
    // 忽略：使用預設值
  }
  return current
}

export function getRuntimeConfig(): RuntimeConfig {
  return current
}

/** 直接覆寫目前設定（主要供測試切換功能旗標；正式流程只透過 loadRuntimeConfig） */
export function setRuntimeConfig(config: RuntimeConfig): void {
  current = config
}

/** 組 Jaeger trace 連結；只接受 http(s)，避免設定被竄改成 javascript: 之類的 URL */
export function buildJaegerTraceUrl(traceId: string | null, config = current): string | null {
  if (!traceId || !config.jaegerUiUrl) return null
  try {
    const base = new URL(config.jaegerUiUrl)
    if (base.protocol !== 'http:' && base.protocol !== 'https:') return null
    return `${base.origin}${base.pathname.replace(/\/+$/, '')}/trace/${encodeURIComponent(traceId)}`
  } catch {
    return null
  }
}
