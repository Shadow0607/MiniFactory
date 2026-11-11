<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError } from '@/api/errors'
import JobProgress from '@/components/JobProgress.vue'
import StatusTag from '@/components/StatusTag.vue'
import { useLiveRefresh } from '@/composables/useLiveRefresh'
import { buildJaegerTraceUrl } from '@/config/runtimeConfig'
import { REFRESH_INTERVAL_MS } from '@/constants/refresh'
import { useOrdersStore } from '@/stores/orders'
import { formatDateTime } from '@/utils/datetime'
import { isTerminalOrderStatus } from '@/utils/status'

const route = useRoute()
const orders = useOrdersStore()

const orderId = computed(() => String(route.params.id ?? ''))
const detail = computed(() => orders.details[orderId.value] ?? null)
const notFound = ref(false)
const jaegerUrl = computed(() => buildJaegerTraceUrl(detail.value?.traceId ?? null))

// 詳情 3 秒輪詢（api 規格）；首次載入失敗才提示，背景輪詢失敗不打擾
useLiveRefresh(
  async (silent) => {
    try {
      await orders.fetchDetail(orderId.value, silent)
      notFound.value = false
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) notFound.value = true
      throw e
    }
  },
  {
    intervalMs: REFRESH_INTERVAL_MS.orderDetail,
    // 訂單進入終態（Completed / NeedsReview）後不會再變動，停止輪詢
    shouldStop: () => isTerminalOrderStatus(detail.value?.status),
  },
)
</script>

<template>
  <div v-loading="orders.detailLoading && !detail">
    <el-page-header class="back" @back="$router.push({ name: 'orders' })">
      <template #content>訂單詳情</template>
    </el-page-header>

    <el-empty v-if="notFound" description="找不到這張訂單，可能編號有誤或已被移除" />

    <template v-else-if="detail">
      <el-descriptions :column="3" border class="summary">
        <el-descriptions-item label="訂單編號">{{ detail.orderNo }}</el-descriptions-item>
        <el-descriptions-item label="狀態">
          <StatusTag kind="order" :value="detail.status" />
        </el-descriptions-item>
        <el-descriptions-item label="產線">{{ detail.lineId }}</el-descriptions-item>
        <el-descriptions-item label="產品">{{ detail.productCode }}</el-descriptions-item>
        <el-descriptions-item label="數量">{{ detail.quantity }}</el-descriptions-item>
        <el-descriptions-item label="版本">{{ detail.version }}</el-descriptions-item>
        <el-descriptions-item label="建立時間">{{ formatDateTime(detail.createdAt) }}</el-descriptions-item>
        <el-descriptions-item label="更新時間">{{ formatDateTime(detail.updatedAt) }}</el-descriptions-item>
        <el-descriptions-item label="完成時間">{{ formatDateTime(detail.completedAt) }}</el-descriptions-item>
        <el-descriptions-item label="Trace">
          <a v-if="jaegerUrl" :href="jaegerUrl" target="_blank" rel="noopener noreferrer">
            在 Jaeger 開啟
          </a>
          <span v-else>—</span>
        </el-descriptions-item>
      </el-descriptions>

      <h3>工序進度</h3>
      <JobProgress :jobs="detail.jobs" />
    </template>
  </div>
</template>

<style scoped>
.back {
  margin-bottom: 16px;
}

.summary {
  margin-bottom: 8px;
}
</style>
