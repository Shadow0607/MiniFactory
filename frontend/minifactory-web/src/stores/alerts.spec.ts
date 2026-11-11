import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Alert } from '@/api/types'
import { useAlertsStore } from './alerts'

vi.mock('@/api/alerts', () => ({
  listAlerts: vi.fn(),
  resolveAlert: vi.fn(),
}))
import * as alertsApi from '@/api/alerts'

function makeAlert(overrides: Partial<Alert> = {}): Alert {
  return {
    id: 'a1',
    severity: 'Critical',
    reason: 'EQUIPMENT_OFFLINE',
    message: 'Machine-B 超過 15 秒未回報心跳',
    status: 'Open',
    relatedOrderId: null,
    relatedJobId: null,
    relatedEquipmentId: 'e1',
    createdAt: '2026-09-21T02:16:03.100Z',
    resolvedAt: null,
    resolvedBy: null,
    resolutionNote: null,
    ...overrides,
  }
}

describe('alerts store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
  })

  it('fetchList：寫入列表與 openCount', async () => {
    vi.mocked(alertsApi.listAlerts).mockResolvedValue({
      items: [makeAlert()],
      page: 1,
      pageSize: 20,
      totalCount: 1,
      openCount: 3,
    })
    const store = useAlertsStore()
    await store.fetchList()
    expect(store.items).toHaveLength(1)
    expect(store.openCount).toBe(3)
  })

  it('refreshOpenCount：只更新數字；失敗時保留舊值', async () => {
    const store = useAlertsStore()
    vi.mocked(alertsApi.listAlerts).mockResolvedValueOnce({
      items: [],
      page: 1,
      pageSize: 1,
      totalCount: 0,
      openCount: 4,
    })
    await store.refreshOpenCount()
    expect(store.openCount).toBe(4)

    vi.mocked(alertsApi.listAlerts).mockRejectedValueOnce(new Error('boom'))
    await store.refreshOpenCount()
    expect(store.openCount).toBe(4)
  })

  it('resolve：更新該筆告警，openCount 減 1', async () => {
    const store = useAlertsStore()
    store.items = [makeAlert()]
    store.openCount = 2
    vi.mocked(alertsApi.resolveAlert).mockResolvedValue(
      makeAlert({ status: 'Resolved', resolvedBy: 'Operator', resolvedAt: '2026-09-21T02:18:30.000Z' }),
    )

    await store.resolve('a1', '人為斷線')

    expect(alertsApi.resolveAlert).toHaveBeenCalledWith('a1', '人為斷線')
    expect(store.items[0]?.status).toBe('Resolved')
    expect(store.openCount).toBe(1)
  })

  it('upsertAlert：已是結案的告警再次更新，不會重複扣 openCount', () => {
    const store = useAlertsStore()
    store.items = [makeAlert({ status: 'Resolved', resolvedBy: 'System' })]
    store.openCount = 2
    store.upsertAlert(makeAlert({ status: 'Resolved', resolvedBy: 'System' }))
    expect(store.openCount).toBe(2)
  })
})
