<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import ApiKeyDialog from '@/components/ApiKeyDialog.vue'
import { REFRESH_INTERVAL_MS } from '@/constants/refresh'
import { useLiveRefresh } from '@/composables/useLiveRefresh'
import { useAlertsStore } from '@/stores/alerts'
import { useSystemStore } from '@/stores/system'

const route = useRoute()
const alerts = useAlertsStore()
const system = useSystemStore()

// 選單高亮：/orders/:id 也算在「訂單」底下
const activeMenu = computed(() => `/${route.path.split('/')[1] ?? ''}`)

// 導覽列的未處理告警數：背景輪詢，分頁隱藏時暫停
useLiveRefresh(() => alerts.refreshOpenCount(), { intervalMs: REFRESH_INTERVAL_MS.navBadge })
</script>

<template>
  <el-container class="app-root">
    <el-header class="app-header">
      <div class="brand">MiniFactory</div>
      <el-menu :default-active="activeMenu" mode="horizontal" router class="app-menu">
        <el-menu-item index="/orders">訂單</el-menu-item>
        <el-menu-item index="/equipment">設備</el-menu-item>
        <el-menu-item index="/alerts">
          <span>告警</span>
          <el-badge
            :value="alerts.openCount"
            :max="99"
            :hidden="alerts.openCount === 0"
            class="nav-badge"
          />
        </el-menu-item>
      </el-menu>
      <div class="header-actions">
        <el-tag :type="system.apiKey ? 'success' : 'info'" effect="plain">
          Operator Key：{{ system.apiKey ? '已輸入' : '未輸入' }}
        </el-tag>
        <el-button size="small" @click="system.openApiKeyDialog()">
          {{ system.apiKey ? '更換' : '輸入' }}
        </el-button>
        <el-button v-if="system.apiKey" size="small" @click="system.clearApiKey()">清除</el-button>
      </div>
    </el-header>

    <el-main class="app-main">
      <router-view :key="route.fullPath" />
    </el-main>

    <ApiKeyDialog />
  </el-container>
</template>

<style>
body {
  margin: 0;
}

.app-root {
  min-height: 100vh;
}

.app-header {
  display: flex;
  align-items: center;
  gap: 24px;
  padding: 0 20px;
  border-bottom: 1px solid var(--el-border-color-light);
}

.brand {
  font-size: 20px;
  font-weight: 700;
  white-space: nowrap;
}

.app-menu {
  flex: 1;
  border-bottom: none;
}

.nav-badge {
  margin-left: 6px;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.app-main {
  max-width: 1280px;
  width: 100%;
  margin: 0 auto;
  box-sizing: border-box;
}

.page-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
  margin-bottom: 16px;
}

.page-toolbar .spacer {
  flex: 1;
}

.page-pagination {
  margin-top: 16px;
  justify-content: flex-end;
}
</style>
