import axios, { type AxiosInstance } from 'axios'
import { getRuntimeConfig } from '@/config/runtimeConfig'
import { useSystemStore } from '@/stores/system'
import { installInterceptors } from './interceptors'
import { notifyApiError } from './notifier'

/** 建立 Axios 實例；baseURL 來自執行期設定（/config.json），不寫死 */
export function createHttpClient(baseURL: string): AxiosInstance {
  const instance = axios.create({
    baseURL,
    timeout: 15000,
    headers: { Accept: 'application/json' },
    // 陣列參數序列化成 status=a&status=b（api.md §3.2），而非 axios 預設的 status[]=a
    paramsSerializer: { indexes: null },
  })

  installInterceptors(instance, {
    getApiKey: () => useSystemStore().requestApiKey(),
    peekApiKey: () => useSystemStore().apiKey,
    clearApiKey: () => useSystemStore().clearApiKey(),
    notify: notifyApiError,
  })
  return instance
}

let instance: AxiosInstance | null = null

/** 延遲建立：必須在 main.ts 讀完 /config.json、安裝 Pinia 之後才會第一次被呼叫 */
export function getHttp(): AxiosInstance {
  if (!instance) instance = createHttpClient(getRuntimeConfig().apiBaseUrl)
  return instance
}
