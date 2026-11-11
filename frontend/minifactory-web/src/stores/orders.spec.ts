import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { OrderDetail, OrderSummary } from '@/api/types'
import { useOrdersStore } from './orders'

vi.mock('@/api/orders', () => ({
  listOrders: vi.fn(),
  getOrder: vi.fn(),
  createOrder: vi.fn(),
}))
import * as ordersApi from '@/api/orders'

function makeDetail(overrides: Partial<OrderDetail> = {}): OrderDetail {
  return {
    id: 'o1',
    orderNo: 'ORD-1',
    productCode: 'Motor-A',
    quantity: 10,
    lineId: 'Line-01',
    status: 'InProgress',
    version: 5,
    traceId: null,
    createdAt: '2026-09-21T02:15:30.123Z',
    updatedAt: '2026-09-21T02:15:41.870Z',
    completedAt: null,
    jobs: [],
    ...overrides,
  }
}

function makeSummary(overrides: Partial<OrderSummary> = {}): OrderSummary {
  return {
    id: 'o1',
    orderNo: 'ORD-1',
    productCode: 'Motor-A',
    quantity: 10,
    lineId: 'Line-01',
    status: 'InProgress',
    completedJobs: 1,
    totalJobs: 3,
    version: 4,
    createdAt: '2026-09-21T02:15:30.123Z',
    updatedAt: '2026-09-21T02:15:41.870Z',
    ...overrides,
  }
}

describe('orders store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
  })

  it('upsertDetail：較舊的 version 不會覆蓋較新的', () => {
    const store = useOrdersStore()
    expect(store.upsertDetail(makeDetail({ version: 7, status: 'AwaitingConfirmation' }))).toBe(true)
    expect(store.upsertDetail(makeDetail({ version: 6, status: 'InProgress' }))).toBe(false)
    expect(store.details['o1']?.version).toBe(7)
    expect(store.details['o1']?.status).toBe('AwaitingConfirmation')
  })

  it('upsertDetail：相同 version 仍會更新（execution 細節可能未遞增 version）', () => {
    const store = useOrdersStore()
    store.upsertDetail(makeDetail({ version: 7, updatedAt: 'a' }))
    expect(store.upsertDetail(makeDetail({ version: 7, updatedAt: 'b' }))).toBe(true)
    expect(store.details['o1']?.updatedAt).toBe('b')
  })

  it('upsertDetail：同步列表中的摘要', () => {
    const store = useOrdersStore()
    store.items = [makeSummary({ version: 4 })]
    store.upsertDetail(
      makeDetail({
        version: 8,
        status: 'Completed',
        jobs: [
          { id: 'j1', sequence: 1, operation: 'Machining', status: 'Completed', assignedEquipment: null, currentAttemptNo: 1, startedAt: null, completedAt: null, executions: [] },
          { id: 'j2', sequence: 2, operation: 'Assembly', status: 'Completed', assignedEquipment: null, currentAttemptNo: 1, startedAt: null, completedAt: null, executions: [] },
        ],
      }),
    )
    expect(store.items[0]?.status).toBe('Completed')
    expect(store.items[0]?.version).toBe(8)
    expect(store.items[0]?.completedJobs).toBe(2)
  })

  it('fetchList：把回應寫入 store，並帶上篩選條件', async () => {
    vi.mocked(ordersApi.listOrders).mockResolvedValue({
      items: [makeSummary()],
      page: 1,
      pageSize: 20,
      totalCount: 1,
    })
    const store = useOrdersStore()
    store.statusFilter = ['InProgress']
    store.orderNoFilter = ' ORD '
    await store.fetchList()

    expect(store.items).toHaveLength(1)
    expect(store.totalCount).toBe(1)
    expect(store.listLoaded).toBe(true)
    expect(ordersApi.listOrders).toHaveBeenCalledWith(
      { status: ['InProgress'], orderNo: 'ORD', page: 1, pageSize: 20 },
      { silent: false },
    )
  })

  it('fetchList：較晚送出的請求先回來時，丟棄過期結果', async () => {
    let resolveFirst: (v: { items: OrderSummary[]; page: number; pageSize: number; totalCount: number }) => void = () => {}
    vi.mocked(ordersApi.listOrders)
      .mockImplementationOnce(() => new Promise((resolve) => { resolveFirst = resolve }))
      .mockResolvedValueOnce({ items: [makeSummary({ orderNo: 'NEW' })], page: 1, pageSize: 20, totalCount: 1 })

    const store = useOrdersStore()
    const first = store.fetchList(true)
    await store.fetchList(true)
    resolveFirst({ items: [makeSummary({ orderNo: 'OLD' })], page: 1, pageSize: 20, totalCount: 1 })
    await first

    expect(store.items[0]?.orderNo).toBe('NEW')
  })
})
