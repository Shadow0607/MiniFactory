<script setup lang="ts">
import { computed } from 'vue'
import type { Job, JobStatus } from '@/api/types'
import { formatDateTime } from '@/utils/datetime'
import StatusTag from './StatusTag.vue'

const props = defineProps<{ jobs: Job[] }>()

const sortedJobs = computed(() => [...props.jobs].sort((a, b) => a.sequence - b.sequence))

type StepStatus = 'wait' | 'process' | 'success' | 'error'

function stepStatus(status: JobStatus): StepStatus {
  switch (status) {
    case 'Completed':
      return 'success'
    case 'NeedsReview':
      return 'error'
    case 'Pending':
      return 'wait'
    default:
      return 'process'
  }
}

/** el-steps 的 active：第一道尚未完成的工序（全部完成則為總數） */
const activeIndex = computed(() => {
  const idx = sortedJobs.value.findIndex((j) => j.status !== 'Completed')
  return idx === -1 ? sortedJobs.value.length : idx
})
</script>

<template>
  <div>
    <el-steps :active="activeIndex" align-center class="steps">
      <el-step
        v-for="job in sortedJobs"
        :key="job.id"
        :title="`${job.sequence}. ${job.operation}`"
        :status="stepStatus(job.status)"
      />
    </el-steps>

    <el-card v-for="job in sortedJobs" :key="job.id" class="job-card" shadow="never">
      <template #header>
        <div class="job-header">
          <strong>第 {{ job.sequence }} 道：{{ job.operation }}</strong>
          <StatusTag kind="job" :value="job.status" />
        </div>
      </template>
      <el-descriptions :column="3" size="small">
        <el-descriptions-item label="指派設備">
          {{ job.assignedEquipment?.name ?? '—' }}
        </el-descriptions-item>
        <el-descriptions-item label="目前嘗試次數">{{ job.currentAttemptNo }}</el-descriptions-item>
        <el-descriptions-item label="開始時間">{{ formatDateTime(job.startedAt) }}</el-descriptions-item>
        <el-descriptions-item label="完成時間">{{ formatDateTime(job.completedAt) }}</el-descriptions-item>
      </el-descriptions>

      <el-table
        v-if="job.executions.length > 0"
        :data="job.executions"
        size="small"
        class="exec-table"
        row-key="commandId"
      >
        <el-table-column prop="attemptNo" label="嘗試" width="70" />
        <el-table-column label="狀態" width="170">
          <template #default="{ row }">
            <StatusTag kind="execution" :value="row.status" />
          </template>
        </el-table-column>
        <el-table-column label="設備" width="120">
          <template #default="{ row }">{{ row.equipment?.name ?? '—' }}</template>
        </el-table-column>
        <el-table-column prop="sendCount" label="送出次數" width="90" />
        <el-table-column label="最後送出" min-width="150">
          <template #default="{ row }">{{ formatDateTime(row.lastSentAt) }}</template>
        </el-table-column>
        <el-table-column label="錯誤碼" min-width="140">
          <template #default="{ row }">{{ row.errorCode ?? '—' }}</template>
        </el-table-column>
        <el-table-column prop="commandId" label="Command ID" min-width="260" show-overflow-tooltip />
      </el-table>
      <div v-else class="no-exec">尚無執行紀錄</div>
    </el-card>
  </div>
</template>

<style scoped>
.steps {
  margin: 8px 0 24px;
}

.job-card {
  margin-bottom: 12px;
}

.job-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.exec-table {
  margin-top: 8px;
}

.no-exec {
  margin-top: 8px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
</style>
