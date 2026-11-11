import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import * as equipmentApi from '@/api/equipment'
import { ApiError } from '@/api/errors'
import type { Equipment } from '@/api/types'

export const useEquipmentStore = defineStore('equipment', () => {
  const items = ref<Equipment[]>([])
  /** true 代表 MariaDB 不可用、資料來自 Redis 快照（api.md §4.1） */
  const stale = ref(false)
  const generatedAt = ref<string | null>(null)
  const loading = ref(false)
  const loaded = ref(false)
  let seq = 0

  /** 目前已知的產線代號（建單表單的候選值） */
  const lineIds = computed(() => [...new Set(items.value.map((e) => e.lineId))].sort())

  async function fetchAll(silent = false): Promise<void> {
    const current = ++seq
    if (!silent) loading.value = true
    try {
      const result = await equipmentApi.listEquipment(undefined, { silent })
      // 較晚送出的請求已先回來時，丟棄這筆過期結果
      if (current !== seq) return
      items.value = result.items
      stale.value = result.stale
      generatedAt.value = result.generatedAt
      loaded.value = true
    } catch (e) {
      if (!(e instanceof ApiError) || !silent) throw e
    } finally {
      if (current === seq) loading.value = false
    }
  }

  return { items, stale, generatedAt, loading, loaded, lineIds, fetchAll }
})
