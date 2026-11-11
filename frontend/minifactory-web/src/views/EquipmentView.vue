<script setup lang="ts">
import StatusTag from '@/components/StatusTag.vue'
import { useLiveRefresh } from '@/composables/useLiveRefresh'
import { REFRESH_INTERVAL_MS } from '@/constants/refresh'
import { useEquipmentStore } from '@/stores/equipment'
import { formatDateTime } from '@/utils/datetime'

const equipment = useEquipmentStore()

useLiveRefresh((silent) => equipment.fetchAll(silent), {
  intervalMs: REFRESH_INTERVAL_MS.equipment,
})
</script>

<template>
  <div>
    <el-alert
      v-if="equipment.stale"
      type="warning"
      show-icon
      :closable="false"
      class="stale"
      title="資料庫暫時無法使用，目前顯示的是快取快照，狀態可能不是最新"
    />
    <div class="meta">資料產生時間：{{ formatDateTime(equipment.generatedAt) }}</div>

    <el-table
      v-loading="equipment.loading"
      :data="equipment.items"
      row-key="id"
      empty-text="沒有設備資料"
    >
      <el-table-column prop="name" label="設備" min-width="130" />
      <el-table-column prop="lineId" label="產線" width="110" />
      <el-table-column prop="capability" label="能力" min-width="120" />
      <el-table-column label="狀態" width="110">
        <template #default="{ row }"><StatusTag kind="equipment" :value="row.status" /></template>
      </el-table-column>
      <el-table-column label="派工預留" width="110">
        <template #default="{ row }">
          <el-tag v-if="row.reservedJobId" type="primary" effect="plain">已預留</el-tag>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column label="最後回報" min-width="170">
        <template #default="{ row }">{{ formatDateTime(row.lastSeenAt) }}</template>
      </el-table-column>
      <el-table-column label="狀態變更時間" min-width="170">
        <template #default="{ row }">{{ formatDateTime(row.statusChangedAt) }}</template>
      </el-table-column>
    </el-table>
  </div>
</template>

<style scoped>
.stale {
  margin-bottom: 12px;
}

.meta {
  margin-bottom: 8px;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
</style>
