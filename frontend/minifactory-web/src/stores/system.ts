import { defineStore } from 'pinia'
import { ref } from 'vue'
import { apiKeyStorage } from '@/utils/apiKeyStorage'

/**
 * 系統層狀態：Operator API Key 與輸入對話框。
 * Key 只存在 sessionStorage / 記憶體，不進入 bundle、不寫日誌。
 * Phase 2 接 SignalR 時，連線狀態（Connected/Reconnecting/Disconnected）也放這裡。
 */
export const useSystemStore = defineStore('system', () => {
  const apiKey = ref<string | null>(apiKeyStorage.get())
  const apiKeyDialogVisible = ref(false)

  let pending: Promise<string | null> | null = null
  let resolvePending: ((key: string | null) => void) | null = null

  /** 取得 Operator Key；沒有就跳出輸入框，使用者取消回傳 null。同時多個請求共用同一個輸入框 */
  function requestApiKey(): Promise<string | null> {
    if (apiKey.value) return Promise.resolve(apiKey.value)
    if (pending) return pending
    apiKeyDialogVisible.value = true
    pending = new Promise<string | null>((resolve) => {
      resolvePending = resolve
    })
    return pending
  }

  function settle(key: string | null) {
    apiKeyDialogVisible.value = false
    const resolve = resolvePending
    pending = null
    resolvePending = null
    resolve?.(key)
  }

  function submitApiKey(input: string) {
    const key = input.trim()
    if (!key) return
    apiKey.value = key
    apiKeyStorage.set(key)
    settle(key)
  }

  function cancelApiKeyPrompt() {
    settle(null)
  }

  /** 登出 / 收到 401 時清除 */
  function clearApiKey() {
    apiKey.value = null
    apiKeyStorage.clear()
  }

  /** 讓使用者主動開啟輸入框更換 Key（導覽列按鈕） */
  function openApiKeyDialog() {
    apiKeyDialogVisible.value = true
  }

  return {
    apiKey,
    apiKeyDialogVisible,
    requestApiKey,
    submitApiKey,
    cancelApiKeyPrompt,
    clearApiKey,
    openApiKeyDialog,
  }
})
