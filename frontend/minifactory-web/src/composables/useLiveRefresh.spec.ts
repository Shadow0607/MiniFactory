// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { useLiveRefresh } from './useLiveRefresh'

/** 掛一個只使用 useLiveRefresh 的元件 */
function mountWith(refresh: () => Promise<unknown>, shouldStop?: () => boolean) {
  return mount(
    defineComponent({
      setup() {
        useLiveRefresh(refresh, { intervalMs: 3000, shouldStop })
        return () => h('div')
      },
    }),
  )
}

describe('useLiveRefresh shouldStop（訂單終態停止輪詢）', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('未到終態：每 3 秒繼續輪詢', async () => {
    const refresh = vi.fn().mockResolvedValue(undefined)
    const wrapper = mountWith(refresh, () => false)
    await vi.advanceTimersByTimeAsync(0)
    expect(refresh).toHaveBeenCalledTimes(1) // 首次載入
    await vi.advanceTimersByTimeAsync(3000)
    await vi.advanceTimersByTimeAsync(3000)
    expect(refresh).toHaveBeenCalledTimes(3)
    wrapper.unmount()
  })

  it('輪詢中變成終態：該次更新後不再輪詢', async () => {
    let terminal = false
    const refresh = vi.fn().mockImplementation(async () => {
      // 第 2 次呼叫（第一次輪詢）拿到終態
      if (refresh.mock.calls.length >= 2) terminal = true
    })
    const wrapper = mountWith(refresh, () => terminal)
    await vi.advanceTimersByTimeAsync(0)
    await vi.advanceTimersByTimeAsync(3000)
    expect(refresh).toHaveBeenCalledTimes(2)

    await vi.advanceTimersByTimeAsync(30000)
    expect(refresh).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('首次載入就是終態：不會啟動輪詢', async () => {
    const refresh = vi.fn().mockResolvedValue(undefined)
    const wrapper = mountWith(refresh, () => true)
    await vi.advanceTimersByTimeAsync(0)
    await vi.advanceTimersByTimeAsync(30000)
    expect(refresh).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })

  it('沒有提供 shouldStop：維持原本行為（持續輪詢）', async () => {
    const refresh = vi.fn().mockResolvedValue(undefined)
    const wrapper = mountWith(refresh)
    await vi.advanceTimersByTimeAsync(0)
    await vi.advanceTimersByTimeAsync(9000)
    expect(refresh).toHaveBeenCalledTimes(4)
    wrapper.unmount()
  })
})
