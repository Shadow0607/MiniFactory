import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import type { Equipment } from '@/api/types'
import { useEquipmentStore } from './equipment'

vi.mock('@/api/equipment', () => ({ listEquipment: vi.fn() }))
import * as equipmentApi from '@/api/equipment'

function makeEquipment(overrides: Partial<Equipment> = {}): Equipment {
  return {
    id: 'e1',
    name: 'Machine-A',
    lineId: 'LINE-01',
    capability: 'Machining',
    status: 'Idle',
    lastSeenAt: null,
    statusChangedAt: null,
    reservedJobId: null,
    version: 0,
    ...overrides,
  }
}

function makeApiError(): ApiError {
  return new ApiError({ status: 503, message: '服務暫時無法使用' })
}

describe('equipment store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
  })

  it('fetchAll：寫入設備、stale、generatedAt，並標記 loaded', async () => {
    vi.mocked(equipmentApi.listEquipment).mockResolvedValue({
      items: [makeEquipment()],
      stale: true,
      generatedAt: '2026-09-21T02:00:00.000Z',
    })
    const store = useEquipmentStore()
    expect(store.loaded).toBe(false)

    await store.fetchAll()
    expect(store.items).toHaveLength(1)
    expect(store.stale).toBe(true)
    expect(store.generatedAt).toBe('2026-09-21T02:00:00.000Z')
    expect(store.loaded).toBe(true)
    expect(store.loading).toBe(false)
  })

  it('lineIds：去重並排序（建單表單的候選值）', async () => {
    vi.mocked(equipmentApi.listEquipment).mockResolvedValue({
      items: [
        makeEquipment({ id: '1', lineId: 'LINE-02' }),
        makeEquipment({ id: '2', lineId: 'LINE-01' }),
        makeEquipment({ id: '3', lineId: 'LINE-02' }),
      ],
      stale: false,
      generatedAt: '2026-09-21T02:00:00.000Z',
    })
    const store = useEquipmentStore()
    await store.fetchAll()
    expect(store.lineIds).toEqual(['LINE-01', 'LINE-02'])
  })

  it('lineIds：沒有設備時為空陣列', () => {
    expect(useEquipmentStore().lineIds).toEqual([])
  })

  it('fetchAll（非背景）失敗：拋出錯誤，loading 仍會復原', async () => {
    vi.mocked(equipmentApi.listEquipment).mockRejectedValue(makeApiError())
    const store = useEquipmentStore()
    await expect(store.fetchAll()).rejects.toBeInstanceOf(ApiError)
    expect(store.loading).toBe(false)
    expect(store.loaded).toBe(false)
  })

  it('fetchAll（silent 背景輪詢）遇到 ApiError：吞掉並保留舊資料', async () => {
    const store = useEquipmentStore()
    vi.mocked(equipmentApi.listEquipment).mockResolvedValueOnce({
      items: [makeEquipment()],
      stale: false,
      generatedAt: '2026-09-21T02:00:00.000Z',
    })
    await store.fetchAll()

    vi.mocked(equipmentApi.listEquipment).mockRejectedValueOnce(makeApiError())
    await expect(store.fetchAll(true)).resolves.toBeUndefined()
    expect(store.items).toHaveLength(1)
  })

  it('fetchAll（silent）遇到非 ApiError（程式錯誤）：仍會往外拋', async () => {
    vi.mocked(equipmentApi.listEquipment).mockRejectedValue(new TypeError('bug'))
    await expect(useEquipmentStore().fetchAll(true)).rejects.toBeInstanceOf(TypeError)
  })

  it('fetchAll（silent）：不改動 loading 旗標', async () => {
    vi.mocked(equipmentApi.listEquipment).mockResolvedValue({
      items: [],
      stale: false,
      generatedAt: '2026-09-21T02:00:00.000Z',
    })
    const store = useEquipmentStore()
    await store.fetchAll(true)
    expect(store.loading).toBe(false)
    expect(store.loaded).toBe(true)
  })

  it('fetchAll：較晚送出的請求先回來時，丟棄較早請求的過期結果', async () => {
    const resolvers: Array<(v: Awaited<ReturnType<typeof equipmentApi.listEquipment>>) => void> = []
    vi.mocked(equipmentApi.listEquipment).mockImplementation(
      () => new Promise((resolve) => resolvers.push(resolve)),
    )
    const store = useEquipmentStore()
    const first = store.fetchAll()
    const second = store.fetchAll(true)

    // 第二個請求先回來
    resolvers[1]!({ items: [makeEquipment({ id: 'new' })], stale: false, generatedAt: 'T2' })
    await second
    // 第一個（過期）請求後回來
    resolvers[0]!({ items: [makeEquipment({ id: 'old' })], stale: true, generatedAt: 'T1' })
    await first

    expect(store.items.map((e) => e.id)).toEqual(['new'])
    expect(store.stale).toBe(false)
    expect(store.generatedAt).toBe('T2')
    expect(store.loading).toBe(false)
  })
})
