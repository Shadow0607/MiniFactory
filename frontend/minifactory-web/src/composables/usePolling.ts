/** 即時更新策略：目前只有輪詢；Phase 2 可加入 SignalR 實作，頁面端不需修改 */
export interface RefreshStrategy {
  start(): void
  stop(): void
}

export type RefreshTask = () => Promise<unknown> | unknown

/**
 * 以「上一次完成後再等 intervalMs」的方式輪詢（不用 setInterval，避免請求變慢時堆疊）。
 * start() 不會立即執行 task；立即更新由呼叫端決定。
 */
export function createPollingStrategy(task: RefreshTask, intervalMs: number): RefreshStrategy {
  let timer: ReturnType<typeof setTimeout> | null = null
  let running = false
  // 每次 start 換一個世代；stop 後又 start 時，舊的未完成 tick 不會再排程，避免重複計時
  let generation = 0

  function schedule(gen: number) {
    timer = setTimeout(async () => {
      timer = null
      if (!running || gen !== generation) return
      try {
        await task()
      } catch {
        // 錯誤由呼叫端（stores / 攔截器）處理，輪詢本身不中斷
      }
      if (running && gen === generation) schedule(gen)
    }, intervalMs)
  }

  return {
    start() {
      if (running) return
      running = true
      generation++
      schedule(generation)
    },
    stop() {
      running = false
      if (timer !== null) {
        clearTimeout(timer)
        timer = null
      }
    },
  }
}
