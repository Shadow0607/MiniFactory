import { fileURLToPath, URL } from 'node:url'
import vue from '@vitejs/plugin-vue'
import { loadEnv } from 'vite'
import { defineConfig } from 'vitest/config'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  // 開發用 proxy 目標；只用於 dev server，不會被編進 bundle。
  // 預設直連 API（architecture.md / api.md §1：直連 API 埠 5080），可用 VITE_DEV_API_TARGET 覆寫。
  const env = loadEnv(mode, process.cwd(), '')
  const apiTarget = env.VITE_DEV_API_TARGET || 'http://localhost:5080'

  return {
    plugins: [vue()],
    resolve: {
      alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
    },
    server: {
      port: 5173,
      proxy: {
        // 與 nginx 一致：/api、/hubs（含 WebSocket）、/health 轉給 API，前端同源不需 CORS
        '/api': { target: apiTarget, changeOrigin: true },
        '/hubs': { target: apiTarget, changeOrigin: true, ws: true },
        '/health': { target: apiTarget, changeOrigin: true },
      },
    },
    build: {
      // Element Plus 全量引入，主 chunk 較大，調高警告門檻
      chunkSizeWarningLimit: 1500,
    },
    test: {
      environment: 'node',
      include: ['src/**/*.spec.ts'],
      // 路由測試第一次載入頁面（含 element-plus）需要較久的轉換時間
      testTimeout: 20000,
      // element-plus 的 ESM build 會 default import 只有 CJS 的 async-validator（Node 直接載入時
      // 取得的不是建構子，表單驗證會靜默失敗）；交給 Vite 轉換即可正確解析
      server: { deps: { inline: ['element-plus'] } },
    },
  }
})
