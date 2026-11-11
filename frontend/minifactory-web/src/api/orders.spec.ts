import { type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useSystemStore } from '@/stores/system'
import { createHttpClient } from './http'

// 以假 adapter 攔截請求，不碰網路；驗證前端送出的 URL / header 符合 api.md。
let lastConfig: InternalAxiosRequestConfig | null = null
let nextHeaders: Record<string, string> = {}

const adapter: AxiosAdapter = async (config) => {
  lastConfig = config
  return { status: 200, statusText: 'OK', data: { items: [] }, headers: nextHeaders, config }
}

const client = createHttpClient('/api')
client.defaults.adapter = adapter

vi.mock('./http', async (importOriginal) => {
  const original = await importOriginal<typeof import('./http')>()
  return { ...original, getHttp: () => client }
})

import { createOrder, getOrder, getOrderEvents, listOrders } from './orders'

describe('orders API 呼叫格式', () => {
  beforeEach(() => {
    lastConfig = null
    nextHeaders = {}
    // POST 會經過攔截器取 Operator Key；先放入測試用 Key，避免卡在輸入框
    setActivePinia(createPinia())
    useSystemStore().submitApiKey('unit-test-key')
  })

  it('createHttpClient：baseURL 來自參數，逾時 15 秒', () => {
    expect(client.defaults.baseURL).toBe('/api')
    expect(client.defaults.timeout).toBe(15000)
  })

  it('listOrders：status 陣列序列化為 status=a&status=b（不是 status[]=a）', async () => {
    await listOrders({ status: ['Created', 'InProgress'], page: 2, pageSize: 50 })
    const uri = client.getUri(lastConfig!)
    expect(uri).toContain('status=Created&status=InProgress')
    expect(uri).not.toContain('status[]')
    expect(uri).toContain('page=2')
    expect(uri).toContain('pageSize=50')
  })

  it('listOrders：空 status 陣列與空 orderNo 不送出參數', async () => {
    await listOrders({ status: [], orderNo: '' })
    const uri = client.getUri(lastConfig!)
    expect(uri).not.toContain('status')
    expect(uri).not.toContain('orderNo')
  })

  it('getOrder：id 會做 URL 編碼，避免路徑被竄改', async () => {
    await getOrder('a/b?c')
    expect(lastConfig!.url).toBe('/orders/a%2Fb%3Fc')
  })

  it('createOrder：有 idempotencyKey 時帶 Idempotency-Key header', async () => {
    await createOrder({ orderNo: 'ORD-1', productCode: 'P1', quantity: 10, lineId: 'LINE-01' }, 'key-1')
    expect(lastConfig!.headers.get('Idempotency-Key')).toBe('key-1')
    expect(lastConfig!.method).toBe('post')
  })

  it('createOrder：沒有 idempotencyKey 時不帶該 header', async () => {
    await createOrder({ orderNo: 'ORD-1', productCode: 'P1', quantity: 10, lineId: 'LINE-01' })
    expect(lastConfig!.headers.has('Idempotency-Key')).toBe(false)
  })

  it('createOrder：Idempotent-Replayed: true 時 replayed 為 true，否則為 false', async () => {
    nextHeaders = { 'idempotent-replayed': 'true' }
    const replayed = await createOrder({ orderNo: 'O', productCode: 'P', quantity: 1, lineId: 'L' }, 'k')
    expect(replayed.replayed).toBe(true)

    nextHeaders = {}
    const first = await createOrder({ orderNo: 'O', productCode: 'P', quantity: 1, lineId: 'L' }, 'k')
    expect(first.replayed).toBe(false)
  })

  it('getOrderEvents：路徑為 /orders/{id}/events，id 會編碼；after 有值才帶', async () => {
    await getOrderEvents('a/b')
    expect(lastConfig!.url).toBe('/orders/a%2Fb/events')
    expect(client.getUri(lastConfig!)).not.toContain('after')

    await getOrderEvents('o1', 'evt-9')
    expect(client.getUri(lastConfig!)).toContain('after=evt-9')
  })
})
