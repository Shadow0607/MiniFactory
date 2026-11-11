<script setup lang="ts">
import { ref } from 'vue'
import type { Alert } from '@/api/types'
import ResolveAlertDialog from '@/components/ResolveAlertDialog.vue'
import StatusTag from '@/components/StatusTag.vue'
import { useLiveRefresh } from '@/composables/useLiveRefresh'
import { getRuntimeConfig } from '@/config/runtimeConfig'
import { REFRESH_INTERVAL_MS } from '@/constants/refresh'
import { useAlertsStore } from '@/stores/alerts'
import { formatDateTime } from '@/utils/datetime'

const alerts = useAlertsStore()
// 結案 API 後端 Phase 4.1 才上線：由 /config.json 的 features.alertResolve 控制，預設關閉
const canResolve = getRuntimeConfig().features.alertResolve
const resolveVisible = ref(false)
const target = ref<Alert | null>(null)

const { refreshNow } = useLiveRefresh((silent) => alerts.fetchList(silent), {
  intervalMs: REFRESH_INTERVAL_MS.alerts,
})

function search() {
  alerts.page = 1
  void refreshNow(false)
}

function onPageChange(page: number) {
  alerts.page = page
  void refreshNow(false)
}

function onPageSizeChange(size: number) {
  alerts.pageSize = size
  alerts.page = 1
  void refreshNow(false)
}

function openResolve(alert: Alert) {
  target.value = alert
  resolveVisible.value = true
}

function resolvedByLabel(by: Alert['resolvedBy']): string {
  if (by === 'System') return '系統自動'
  if (by === 'Operator') return '人工'
  return '—'
}
</script>

<template>
  <div>
    <div class="page-toolbar">
      <el-select
        v-model="alerts.statusFilter"
        placeholder="狀態"
        style="width: 160px"
        @change="search"
      >
        <el-option label="全部" value="" />
        <el-option label="未處理" value="Open" />
        <el-option label="已結案" value="Resolved" />
      </el-select>
      <el-select
        v-model="alerts.severityFilter"
        placeholder="嚴重度"
        style="width: 160px"
        @change="search"
      >
        <el-option label="全部嚴重度" value="" />
        <el-option label="嚴重" value="Critical" />
        <el-option label="警告" value="Warning" />
        <el-option label="資訊" value="Info" />
      </el-select>
      <span class="spacer" />
      <span>未處理告警：{{ alerts.openCount }}</span>
    </div>

    <el-table v-loading="alerts.loading" :data="alerts.items" row-key="id" empty-text="沒有告警">
      <el-table-column label="嚴重度" width="100">
        <template #default="{ row }"><StatusTag kind="severity" :value="row.severity" /></template>
      </el-table-column>
      <el-table-column prop="reason" label="原因代碼" min-width="190" show-overflow-tooltip />
      <el-table-column label="說明" min-width="280" show-overflow-tooltip>
        <template #default="{ row }">{{ row.message ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="狀態" width="100">
        <template #default="{ row }"><StatusTag kind="alert" :value="row.status" /></template>
      </el-table-column>
      <el-table-column label="相關訂單" width="110">
        <template #default="{ row }">
          <router-link
            v-if="row.relatedOrderId"
            :to="{ name: 'order-detail', params: { id: row.relatedOrderId } }"
          >
            查看
          </router-link>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column label="發生時間" min-width="170">
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="結案" min-width="200">
        <template #default="{ row }">
          <template v-if="row.status === 'Resolved'">
            <div>{{ resolvedByLabel(row.resolvedBy) }}　{{ formatDateTime(row.resolvedAt) }}</div>
            <div v-if="row.resolutionNote" class="note">{{ row.resolutionNote }}</div>
          </template>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column v-if="canResolve" label="操作" width="100" fixed="right">
        <template #default="{ row }">
          <el-button v-if="row.status === 'Open'" size="small" @click="openResolve(row as Alert)">
            結案
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-pagination
      class="page-pagination"
      background
      layout="total, sizes, prev, pager, next"
      :total="alerts.totalCount"
      :current-page="alerts.page"
      :page-size="alerts.pageSize"
      :page-sizes="[20, 50, 100]"
      @current-change="onPageChange"
      @size-change="onPageSizeChange"
    />

    <ResolveAlertDialog v-if="canResolve" v-model="resolveVisible" :alert="target" @resolved="refreshNow(true)" />
  </div>
</template>

<style scoped>
.note {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
</style>
