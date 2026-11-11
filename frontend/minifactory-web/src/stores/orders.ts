import { defineStore } from 'pinia'
import { ref } from 'vue'
import * as ordersApi from '@/api/orders'
import { ApiError } from '@/api/errors'
import type { OrderDetail, OrderStatus, OrderSummary } from '@/api/types'

export const useOrdersStore = defineStore('orders', () => {
  // ---------- 列表 ----------
  const items = ref<OrderSummary[]>([])
  const page = ref(1)
  const pageSize = ref(20)
  const totalCount = ref(0)
  const statusFilter = ref<OrderStatus[]>([])
  const orderNoFilter = ref('')
  const listLoading = ref(false)
  const listLoaded = ref(false)
  let listSeq = 0

  /** silent=true 為背景輪詢：不顯示 loading、失敗不跳提示 */
  async function fetchList(silent = false): Promise<void> {
    const seq = ++listSeq
    if (!silent) listLoading.value = true
    try {
      const result = await ordersApi.listOrders(
        {
          status: statusFilter.value,
          orderNo: orderNoFilter.value.trim(),
          page: page.value,
          pageSize: pageSize.value,
        },
        { silent },
      )
      // 較晚送出的請求已先回來時，丟棄這筆過期結果
      if (seq !== listSeq) return
      items.value = result.items
      totalCount.value = result.totalCount
      listLoaded.value = true
    } catch (e) {
      if (!(e instanceof ApiError) || !silent) throw e
    } finally {
      if (seq === listSeq) listLoading.value = false
    }
  }

  // ---------- 詳情（以 id 為 key 快取） ----------
  const details = ref<Record<string, OrderDetail>>({})
  const detailLoading = ref(false)

  /**
   * 寫入詳情快取；較舊的 version 不覆蓋較新的（推播至少一次、可能亂序，api.md §7）。
   * 相同 version 仍覆蓋：execution 的 sendCount 等細節不一定每次都遞增 version。
   * @returns 是否有寫入
   */
  function upsertDetail(detail: OrderDetail): boolean {
    const existing = details.value[detail.id]
    if (existing && detail.version < existing.version) return false
    details.value[detail.id] = detail

    // 同步列表中的摘要，避免列表與詳情不一致
    const row = items.value.find((o) => o.id === detail.id)
    if (row && detail.version >= row.version) {
      row.status = detail.status
      row.version = detail.version
      row.updatedAt = detail.updatedAt
      row.completedJobs = detail.jobs.filter((j) => j.status === 'Completed').length
    }
    return true
  }

  async function fetchDetail(id: string, silent = false): Promise<OrderDetail | null> {
    if (!silent) detailLoading.value = true
    try {
      const detail = await ordersApi.getOrder(id, { silent })
      upsertDetail(detail)
      return details.value[id] ?? null
    } catch (e) {
      if (!(e instanceof ApiError) || !silent) throw e
      return null
    } finally {
      if (!silent) detailLoading.value = false
    }
  }

  return {
    items,
    page,
    pageSize,
    totalCount,
    statusFilter,
    orderNoFilter,
    listLoading,
    listLoaded,
    fetchList,
    details,
    detailLoading,
    upsertDetail,
    fetchDetail,
  }
})
