// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory } from 'vue-router'
import type { Alert } from '@/api/types'
import { defaultRuntimeConfig, setRuntimeConfig } from '@/config/runtimeConfig'
import { createAppRouter } from '@/router'
import AlertsView from './AlertsView.vue'

vi.mock('@/api/alerts', () => ({ listAlerts: vi.fn(), resolveAlert: vi.fn() }))
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

async function render(items: Alert[]) {
  vi.mocked(alertsApi.listAlerts).mockResolvedValue({
    items,
    page: 1,
    pageSize: 20,
    totalCount: items.length,
    openCount: items.filter((a) => a.status === 'Open').length,
  })
  const wrapper = mount(AlertsView, {
    global: {
      plugins: [ElementPlus, createAppRouter(createMemoryHistory())],
      // el-select 的彈出層在 happy-dom 會無限遞迴更新，這裡只關心表格，直接 stub
      stubs: { teleport: true, ElSelect: true, ElOption: true },
    },
  })
  await flushPromises()
  return wrapper
}

/** 表格「操作」欄的結案按鈕（表頭的「結案」是欄名，不是按鈕） */
function resolveButtons(wrapper: Awaited<ReturnType<typeof render>>) {
  return wrapper.findAll('button').filter((b) => b.text().trim() === '結案')
}

describe('AlertsView 結案按鈕（功能旗標 features.alertResolve）', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
  })
  afterEach(() => {
    setRuntimeConfig({ ...defaultRuntimeConfig, features: { ...defaultRuntimeConfig.features } })
  })

  it('預設（旗標 false）：隱藏結案按鈕與操作欄', async () => {
    const wrapper = await render([makeAlert()])
    expect(wrapper.text()).toContain('EQUIPMENT_OFFLINE') // 資料有正常顯示
    expect(resolveButtons(wrapper)).toHaveLength(0)
    expect(wrapper.text()).not.toContain('操作')
  })

  it('旗標 true：未處理的告警顯示結案按鈕，已結案的不顯示', async () => {
    setRuntimeConfig({ ...defaultRuntimeConfig, features: { alertResolve: true } })
    const wrapper = await render([
      makeAlert({ id: 'a1' }),
      makeAlert({ id: 'a2', status: 'Resolved', resolvedBy: 'System', resolvedAt: '2026-09-21T02:20:00.000Z' }),
    ])
    expect(resolveButtons(wrapper)).toHaveLength(1)
    expect(wrapper.text()).toContain('操作')
  })

  it('旗標 true：點結案按鈕會開啟結案對話框', async () => {
    setRuntimeConfig({ ...defaultRuntimeConfig, features: { alertResolve: true } })
    const wrapper = await render([makeAlert()])
    await resolveButtons(wrapper)[0]!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('告警結案')
    expect(wrapper.text()).toContain('確認結案')
  })

  it('旗標 false：不會渲染結案對話框', async () => {
    const wrapper = await render([makeAlert()])
    expect(wrapper.text()).not.toContain('確認結案')
  })
})

describe('AlertsView message 為 null（Alert.message: string | null）', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
  })

  it('說明欄顯示破折號，不顯示 null 字樣也不會出錯', async () => {
    const wrapper = await render([makeAlert({ message: null })])
    expect(wrapper.text()).toContain('EQUIPMENT_OFFLINE')
    expect(wrapper.text()).not.toContain('null')
    const row = wrapper.find('tbody tr')
    expect(row.text()).toContain('—')
  })
})
