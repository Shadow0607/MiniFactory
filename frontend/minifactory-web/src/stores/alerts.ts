import { defineStore } from 'pinia'
import { ref } from 'vue'
import * as alertsApi from '@/api/alerts'
import { ApiError } from '@/api/errors'
import type { Alert, AlertSeverity, AlertStatus } from '@/api/types'

export const useAlertsStore = defineStore('alerts', () => {
  const items = ref<Alert[]>([])
  const page = ref(1)
  const pageSize = ref(20)
  const totalCount = ref(0)
  /** 導覽列徽章用：未處理告警數（api.md §5.1 openCount，不受分頁/篩選影響） */
  const openCount = ref(0)
  const statusFilter = ref<AlertStatus | ''>('Open')
  const severityFilter = ref<AlertSeverity | ''>('')
  const loading = ref(false)
  const loaded = ref(false)
  let seq = 0

  async function fetchList(silent = false): Promise<void> {
    const current = ++seq
    if (!silent) loading.value = true
    try {
      const result = await alertsApi.listAlerts(
        {
          status: statusFilter.value || undefined,
          severity: severityFilter.value || undefined,
          page: page.value,
          pageSize: pageSize.value,
        },
        { silent },
      )
      if (current !== seq) return
      items.value = result.items
      totalCount.value = result.totalCount
      openCount.value = result.openCount
      loaded.value = true
    } catch (e) {
      if (!(e instanceof ApiError) || !silent) throw e
    } finally {
      if (current === seq) loading.value = false
    }
  }

  /** 只更新徽章數字：pageSize=1 把資料量降到最低 */
  async function refreshOpenCount(): Promise<void> {
    try {
      const result = await alertsApi.listAlerts({ status: 'Open', pageSize: 1 }, { silent: true })
      openCount.value = result.openCount
    } catch {
      // 徽章更新失敗不打擾使用者，保留舊值
    }
  }

  /** 以 id 取代列表中的告警（結案後的回應、未來的 SignalR 推播共用） */
  function upsertAlert(alert: Alert): void {
    const index = items.value.findIndex((a) => a.id === alert.id)
    if (index >= 0) {
      const previous = items.value[index]
      items.value[index] = alert
      if (previous?.status === 'Open' && alert.status === 'Resolved') {
        openCount.value = Math.max(0, openCount.value - 1)
      }
    }
  }

  /** 人工結案；呼叫前由畫面確認。冪等：已結案也會回 200，這裡照樣更新畫面 */
  async function resolve(id: string, note?: string): Promise<Alert> {
    const resolved = await alertsApi.resolveAlert(id, note)
    upsertAlert(resolved)
    return resolved
  }

  return {
    items,
    page,
    pageSize,
    totalCount,
    openCount,
    statusFilter,
    severityFilter,
    loading,
    loaded,
    fetchList,
    refreshOpenCount,
    upsertAlert,
    resolve,
  }
})
