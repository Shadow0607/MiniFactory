// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { useSystemStore } from '@/stores/system'
import { apiKeyStorage } from '@/utils/apiKeyStorage'
import ApiKeyDialog from './ApiKeyDialog.vue'

function render() {
  return mount(ApiKeyDialog, {
    global: { plugins: [ElementPlus], stubs: { teleport: true } },
  })
}

function buttonByText(wrapper: ReturnType<typeof render>, text: string) {
  const button = wrapper.findAll('button').find((b) => b.text().includes(text))
  if (!button) throw new Error(`找不到按鈕：${text}`)
  return button
}

describe('ApiKeyDialog', () => {
  beforeEach(() => {
    apiKeyStorage.clear()
    setActivePinia(createPinia())
  })

  it('沒有 Key 的寫入請求會開啟對話框；輸入欄位為密碼型態', async () => {
    const system = useSystemStore()
    const wrapper = render()
    expect(wrapper.find('input').exists()).toBe(false)

    void system.requestApiKey()
    await flushPromises()
    expect(wrapper.find('input').attributes('type')).toBe('password')
  })

  it('未輸入內容時「確定」鎖住；輸入後送出會回傳 Key 並關閉', async () => {
    const system = useSystemStore()
    const wrapper = render()
    const pending = system.requestApiKey()
    await flushPromises()

    expect(buttonByText(wrapper, '確定').attributes('disabled')).toBeDefined()

    await wrapper.find('input').setValue('  my-key  ')
    expect(buttonByText(wrapper, '確定').attributes('disabled')).toBeUndefined()
    await buttonByText(wrapper, '確定').trigger('click')

    await expect(pending).resolves.toBe('my-key') // 前後空白會被去掉
    expect(system.apiKeyDialogVisible).toBe(false)
    expect(system.apiKey).toBe('my-key')
  })

  it('按 Enter 也能送出', async () => {
    const system = useSystemStore()
    const wrapper = render()
    const pending = system.requestApiKey()
    await flushPromises()

    const input = wrapper.find('input')
    await input.setValue('enter-key')
    await input.trigger('keyup.enter')
    await expect(pending).resolves.toBe('enter-key')
  })

  it('取消：請求得到 null，且不儲存 Key', async () => {
    const system = useSystemStore()
    const wrapper = render()
    const pending = system.requestApiKey()
    await flushPromises()

    await wrapper.find('input').setValue('will-cancel')
    await buttonByText(wrapper, '取消').trigger('click')

    await expect(pending).resolves.toBeNull()
    expect(system.apiKey).toBeNull()
    expect(system.apiKeyDialogVisible).toBe(false)
  })

  it('重新開啟時輸入框是空的，不殘留上一次輸入', async () => {
    const system = useSystemStore()
    const wrapper = render()
    system.openApiKeyDialog()
    await flushPromises()
    await wrapper.find('input').setValue('leftover')
    await buttonByText(wrapper, '取消').trigger('click')
    await flushPromises()

    system.openApiKeyDialog()
    await flushPromises()
    expect((wrapper.find('input').element as HTMLInputElement).value).toBe('')
  })
})
