import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPollingStrategy } from './usePolling'

describe('createPollingStrategy', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('start 後每隔 intervalMs 執行一次；start 本身不立即執行', async () => {
    const task = vi.fn().mockResolvedValue(undefined)
    const poller = createPollingStrategy(task, 3000)
    poller.start()
    expect(task).not.toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(3000)
    expect(task).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(3000)
    expect(task).toHaveBeenCalledTimes(2)
    poller.stop()
  })

  it('stop 後不再執行（分頁隱藏時暫停）', async () => {
    const task = vi.fn().mockResolvedValue(undefined)
    const poller = createPollingStrategy(task, 1000)
    poller.start()
    await vi.advanceTimersByTimeAsync(1000)
    poller.stop()
    await vi.advanceTimersByTimeAsync(5000)
    expect(task).toHaveBeenCalledTimes(1)
  })

  it('task 失敗不會中斷輪詢', async () => {
    const task = vi.fn().mockRejectedValue(new Error('boom'))
    const poller = createPollingStrategy(task, 1000)
    poller.start()
    await vi.advanceTimersByTimeAsync(3000)
    expect(task).toHaveBeenCalledTimes(3)
    poller.stop()
  })

  it('上一次尚未完成時不會重疊執行', async () => {
    let finish: () => void = () => {}
    const task = vi.fn(() => new Promise<void>((resolve) => { finish = resolve }))
    const poller = createPollingStrategy(task, 1000)
    poller.start()
    await vi.advanceTimersByTimeAsync(5000)
    expect(task).toHaveBeenCalledTimes(1) // 仍在等第一次完成

    finish()
    await vi.advanceTimersByTimeAsync(1000)
    expect(task).toHaveBeenCalledTimes(2)
    poller.stop()
  })

  it('執行中 stop 再 start：不會產生兩條計時', async () => {
    let finish: () => void = () => {}
    const task = vi.fn(() => new Promise<void>((resolve) => { finish = resolve }))
    const poller = createPollingStrategy(task, 1000)
    poller.start()
    await vi.advanceTimersByTimeAsync(1000) // 第 1 次執行中
    poller.stop()
    poller.start()
    finish() // 舊世代完成，不應再排程
    await vi.advanceTimersByTimeAsync(1000)
    expect(task).toHaveBeenCalledTimes(2) // 新世代的第一次
    poller.stop()
  })
})
