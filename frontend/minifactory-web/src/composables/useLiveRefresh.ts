import { onBeforeUnmount, onMounted, watch } from 'vue'
import { createPollingStrategy, type RefreshStrategy } from './usePolling'
import { usePageVisibility } from './usePageVisibility'

export interface LiveRefreshOptions {
  /** 輪詢間隔（毫秒） */
  intervalMs: number
  /**
   * 更新策略工廠；預設為輪詢。
   * Phase 2 接 SignalR 時，在此提供「連線中訂閱事件、斷線時退回輪詢」的策略即可，頁面不需改動。
   */
  createStrategy?: (task: () => Promise<unknown>, intervalMs: number) => RefreshStrategy
  /**
   * 每次更新完成後呼叫；回傳 true 代表資料已到終態，不會再變動，
   * 之後停止輪詢（回到可見時也不再重啟）。例如訂單進入 Completed / NeedsReview。
   */
  shouldStop?: () => boolean
}

/**
 * 頁面的即時更新：掛載時先載入一次，之後依策略更新；分頁隱藏時暫停，回到可見時立即補抓一次再繼續。
 *
 * @param refresh 實際更新資料的函式。silent=false 為首次載入（顯示 loading、失敗跳提示），
 *                silent=true 為背景更新（不閃 loading、不跳提示）。
 */
export function useLiveRefresh(
  refresh: (silent: boolean) => Promise<unknown>,
  options: LiveRefreshOptions,
) {
  const { visible } = usePageVisibility()
  const factory = options.createStrategy ?? createPollingStrategy
  // 已進入終態：不再輪詢，也不因分頁回到可見而重啟
  let finished = false

  function checkFinished() {
    if (!finished && options.shouldStop?.()) {
      finished = true
      strategy.stop()
    }
  }

  const strategy = factory(async () => {
    const result = await refresh(true)
    checkFinished()
    return result
  }, options.intervalMs)

  /** 立即更新一次（例如建單成功後、篩選條件變更後） */
  function refreshNow(silent = false): Promise<void> {
    return Promise.resolve(refresh(silent)).then(
      () => checkFinished(),
      () => undefined, // 提示已由攔截器顯示
    )
  }

  onMounted(() => {
    // 首次載入完成前先啟動計時；若首次載入就已是終態，checkFinished 會立即停掉
    void refreshNow(false)
    if (visible.value) strategy.start()
  })

  watch(visible, (isVisible) => {
    if (finished) return
    if (isVisible) {
      void refreshNow(true)
      strategy.start()
    } else {
      strategy.stop()
    }
  })

  onBeforeUnmount(() => strategy.stop())

  return { refreshNow }
}
