import { describe, expect, it } from 'vitest'
import { buildJaegerTraceUrl, normalizeRuntimeConfig } from '@/config/runtimeConfig'
import { createApiKeyStorage } from './apiKeyStorage'
import { formatDateTime } from './datetime'
import { generateId } from './id'
import { isTerminalOrderStatus, resolveMeta, orderStatusMeta } from './status'

describe('formatDateTime', () => {
  it('UTC 轉成指定時區顯示', () => {
    expect(formatDateTime('2026-09-21T02:15:30.123Z', { timeZone: 'Asia/Taipei' })).toContain('10:15:30')
    expect(formatDateTime('2026-09-21T02:15:30.123Z', { timeZone: 'UTC' })).toContain('02:15:30')
  })

  it('空值或無效字串回傳破折號', () => {
    expect(formatDateTime(null)).toBe('—')
    expect(formatDateTime('not-a-date')).toBe('—')
  })
})

describe('apiKeyStorage', () => {
  it('寫入、讀取、清除', () => {
    const data = new Map<string, string>()
    const storage = createApiKeyStorage({
      getItem: (k) => data.get(k) ?? null,
      setItem: (k, v) => void data.set(k, v),
      removeItem: (k) => void data.delete(k),
    })
    expect(storage.get()).toBeNull()
    storage.set('abc')
    expect(storage.get()).toBe('abc')
    storage.clear()
    expect(storage.get()).toBeNull()
  })

  it('沒有 storage 時退回記憶體', () => {
    const storage = createApiKeyStorage(null)
    storage.set('abc')
    expect(storage.get()).toBe('abc')
  })
})

describe('runtimeConfig', () => {
  it('缺漏欄位用預設值，並去掉結尾斜線', () => {
    expect(normalizeRuntimeConfig({})).toEqual({
      apiBaseUrl: '/api',
      jaegerUiUrl: null,
      features: { alertResolve: false },
    })
    expect(normalizeRuntimeConfig({ apiBaseUrl: 'http://x/api/', jaegerUiUrl: 'http://j:16686' })).toEqual({
      apiBaseUrl: 'http://x/api',
      jaegerUiUrl: 'http://j:16686',
      features: { alertResolve: false },
    })
    expect(normalizeRuntimeConfig(null).apiBaseUrl).toBe('/api')
  })

  it('功能旗標：features.alertResolve 只有布林 true 才開啟，預設 false', () => {
    expect(normalizeRuntimeConfig({ features: { alertResolve: true } }).features.alertResolve).toBe(true)
    expect(normalizeRuntimeConfig({ features: { alertResolve: false } }).features.alertResolve).toBe(false)
    expect(normalizeRuntimeConfig({ features: { alertResolve: 'true' } }).features.alertResolve).toBe(false)
    expect(normalizeRuntimeConfig({ features: null }).features.alertResolve).toBe(false)
    expect(normalizeRuntimeConfig({}).features.alertResolve).toBe(false)
  })

  it('組 Jaeger 連結；拒絕非 http(s) 協定', () => {
    const config = {
      apiBaseUrl: '/api',
      jaegerUiUrl: 'http://localhost:16686',
      features: { alertResolve: false },
    }
    expect(buildJaegerTraceUrl('abc123', config)).toBe('http://localhost:16686/trace/abc123')
    expect(buildJaegerTraceUrl(null, config)).toBeNull()
    expect(buildJaegerTraceUrl('abc', { ...config, jaegerUiUrl: 'javascript:alert(1)' })).toBeNull()
    expect(buildJaegerTraceUrl('abc', { ...config, jaegerUiUrl: null })).toBeNull()
  })
})

describe('isTerminalOrderStatus', () => {
  it('Completed、NeedsReview 為終態；其餘與空值不是', () => {
    expect(isTerminalOrderStatus('Completed')).toBe(true)
    expect(isTerminalOrderStatus('NeedsReview')).toBe(true)
    for (const s of ['Created', 'InProgress', 'AwaitingConfirmation', 'Unknown-New']) {
      expect(isTerminalOrderStatus(s)).toBe(false)
    }
    expect(isTerminalOrderStatus(null)).toBe(false)
    expect(isTerminalOrderStatus(undefined)).toBe(false)
  })
})

describe('其他工具', () => {
  it('generateId 產生 UUID 格式', () => {
    expect(generateId()).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  })

  it('resolveMeta 遇到未知狀態退回原字串', () => {
    expect(resolveMeta(orderStatusMeta, 'Completed').label).toBe('已完成')
    expect(resolveMeta(orderStatusMeta, 'Brand-New')).toEqual({ label: 'Brand-New', type: 'info' })
  })
})
