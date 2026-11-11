import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhTw from 'element-plus/es/locale/lang/zh-tw'
import 'element-plus/dist/index.css'
import App from './App.vue'
import { createAppRouter } from './router'
import { loadRuntimeConfig } from './config/runtimeConfig'

async function bootstrap() {
  // 先讀執行期設定（/config.json），Axios 的 baseURL 才不會寫死在 bundle 裡
  await loadRuntimeConfig()

  const app = createApp(App)
  app.use(createPinia())
  app.use(createAppRouter())
  app.use(ElementPlus, { locale: zhTw })
  app.mount('#app')
}

void bootstrap()
