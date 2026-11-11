import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { apiKeyStorage } from '@/utils/apiKeyStorage'
import { useSystemStore } from './system'

// 驗收 1.7：寫入類請求需 Operator Key；Key 只存在 sessionStorage / 記憶體。
describe('system store（API Key 對話框）', () => {
  beforeEach(() => {
    apiKeyStorage.clear()
    setActivePinia(createPinia())
  })

  it('requestApiKey：沒有 Key 時開啟輸入框，送出後回傳 Key 並關閉輸入框', async () => {
    const store = useSystemStore()
    const pending = store.requestApiKey()
    expect(store.apiKeyDialogVisible).toBe(true)

    store.submitApiKey('  test-key  ')
    await expect(pending).resolves.toBe('test-key') // 前後空白會被修剪
    expect(store.apiKeyDialogVisible).toBe(false)
    expect(store.apiKey).toBe('test-key')
  })

  it('requestApiKey：已有 Key 時直接回傳，不開輸入框', async () => {
    const store = useSystemStore()
    store.submitApiKey('k1')
    store.apiKeyDialogVisible = false

    await expect(store.requestApiKey()).resolves.toBe('k1')
    expect(store.apiKeyDialogVisible).toBe(false)
  })

  it('requestApiKey：多個請求同時等待時共用同一個輸入框與同一個結果', async () => {
    const store = useSystemStore()
    const a = store.requestApiKey()
    const b = store.requestApiKey()
    // Pinia 會包裝 action 回傳的 Promise，不比較物件同一性，改驗證兩者得到同一個結果
    expect(store.apiKeyDialogVisible).toBe(true)

    store.submitApiKey('shared')
    await expect(Promise.all([a, b])).resolves.toEqual(['shared', 'shared'])
  })

  it('cancelApiKeyPrompt：使用者取消回傳 null，之後可再次開啟輸入框', async () => {
    const store = useSystemStore()
    const pending = store.requestApiKey()
    store.cancelApiKeyPrompt()
    await expect(pending).resolves.toBeNull()
    expect(store.apiKeyDialogVisible).toBe(false)
    expect(store.apiKey).toBeNull()

    void store.requestApiKey()
    expect(store.apiKeyDialogVisible).toBe(true)
  })

  it('submitApiKey：空字串或全空白被忽略，輸入框保持開啟', () => {
    const store = useSystemStore()
    void store.requestApiKey()
    store.submitApiKey('   ')
    expect(store.apiKey).toBeNull()
    expect(store.apiKeyDialogVisible).toBe(true)
  })

  it('clearApiKey：清除記憶體與 storage（401 時由攔截器呼叫）', () => {
    const store = useSystemStore()
    store.submitApiKey('to-clear')
    expect(apiKeyStorage.get()).toBe('to-clear')

    store.clearApiKey()
    expect(store.apiKey).toBeNull()
    expect(apiKeyStorage.get()).toBeNull()
  })

  it('openApiKeyDialog：使用者可主動開啟輸入框（導覽列更換 Key）', () => {
    const store = useSystemStore()
    store.openApiKeyDialog()
    expect(store.apiKeyDialogVisible).toBe(true)
  })
})
