// Operator API Key 只存在 sessionStorage（關閉分頁即清除）。
// 不寫死、不用 Vite 環境變數（會被編進 bundle）、不寫入日誌。

const STORAGE_KEY = 'minifactory.operatorApiKey'

type StorageLike = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

export interface ApiKeyStorage {
  get(): string | null
  set(key: string): void
  clear(): void
}

/** storage 可注入以利測試；為 null（例如被瀏覽器禁用）時退回記憶體，僅本次頁面有效 */
export function createApiKeyStorage(storage: StorageLike | null): ApiKeyStorage {
  let memory: string | null = null
  return {
    get() {
      try {
        return storage ? storage.getItem(STORAGE_KEY) : memory
      } catch {
        return memory
      }
    },
    set(key) {
      memory = key
      try {
        storage?.setItem(STORAGE_KEY, key)
      } catch {
        // 儲存空間不可用時仍保留於記憶體
      }
    },
    clear() {
      memory = null
      try {
        storage?.removeItem(STORAGE_KEY)
      } catch {
        // 忽略
      }
    },
  }
}

function getSessionStorage(): StorageLike | null {
  try {
    return typeof sessionStorage === 'undefined' ? null : sessionStorage
  } catch {
    return null
  }
}

export const apiKeyStorage = createApiKeyStorage(getSessionStorage())
