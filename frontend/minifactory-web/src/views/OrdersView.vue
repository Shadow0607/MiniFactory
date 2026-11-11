<script setup lang="ts">
import { ref } from 'vue'
import CreateOrderDialog from '@/components/CreateOrderDialog.vue'
import StatusTag from '@/components/StatusTag.vue'
import { useLiveRefresh } from '@/composables/useLiveRefresh'
import { REFRESH_INTERVAL_MS } from '@/constants/refresh'
import { useOrdersStore } from '@/stores/orders'
import { formatDateTime } from '@/utils/datetime'
import { ORDER_STATUS_OPTIONS, orderStatusMeta } from '@/utils/status'

const orders = useOrdersStore()
const createVisible = ref(false)

const { refreshNow } = useLiveRefresh((silent) => orders.fetchList(silent), {
  intervalMs: REFRESH_INTERVAL_MS.orderList,
})

/** 篩選條件變更：回第 1 頁重新查詢 */
function search() {
  orders.page = 1
  void refreshNow(false)
}

function onPageChange(page: number) {
  orders.page = page
  void refreshNow(false)
}

function onPageSizeChange(size: number) {
  orders.pageSize = size
  orders.page = 1
  void refreshNow(false)
}
</script>

<template>
  <div>
    <div class="page-toolbar">
      <el-select
        v-model="orders.statusFilter"
        multiple
        clearable
        collapse-tags
        placeholder="狀態（可多選）"
        style="width: 240px"
        @change="search"
      >
        <el-option
          v-for="s in ORDER_STATUS_OPTIONS"
          :key="s"
          :label="orderStatusMeta[s].label"
          :value="s"
        />
      </el-select>
      <el-input
        v-model="orders.orderNoFilter"
        clearable
        placeholder="訂單編號（前綴搜尋）"
        style="width: 240px"
        @keyup.enter="search"
        @clear="search"
      />
      <el-button @click="search">查詢</el-button>
      <span class="spacer" />
      <el-button type="primary" @click="createVisible = true">建立訂單</el-button>
    </div>

    <el-table v-loading="orders.listLoading" :data="orders.items" row-key="id" empty-text="沒有訂單">
      <el-table-column label="訂單編號" min-width="180">
        <template #default="{ row }">
          <router-link :to="{ name: 'order-detail', params: { id: row.id } }">
            {{ row.orderNo }}
          </router-link>
        </template>
      </el-table-column>
      <el-table-column prop="productCode" label="產品" min-width="120" />
      <el-table-column prop="quantity" label="數量" width="90" />
      <el-table-column prop="lineId" label="產線" width="110" />
      <el-table-column label="狀態" width="130">
        <template #default="{ row }"><StatusTag kind="order" :value="row.status" /></template>
      </el-table-column>
      <el-table-column label="工序進度" width="100">
        <template #default="{ row }">{{ row.completedJobs }} / {{ row.totalJobs }}</template>
      </el-table-column>
      <el-table-column label="建立時間" min-width="170">
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="更新時間" min-width="170">
        <template #default="{ row }">{{ formatDateTime(row.updatedAt) }}</template>
      </el-table-column>
    </el-table>

    <el-pagination
      class="page-pagination"
      background
      layout="total, sizes, prev, pager, next"
      :total="orders.totalCount"
      :current-page="orders.page"
      :page-size="orders.pageSize"
      :page-sizes="[20, 50, 100]"
      @current-change="onPageChange"
      @size-change="onPageSizeChange"
    />

    <CreateOrderDialog v-model="createVisible" @created="refreshNow(true)" />
  </div>
</template>
