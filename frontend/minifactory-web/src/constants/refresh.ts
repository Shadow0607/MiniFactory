// 輪詢間隔（architecture.md §14.10）：詳情 3 秒、告警 5 秒為規格；列表與設備 5 秒為建議值。
export const REFRESH_INTERVAL_MS = {
  orderDetail: 3000,
  alerts: 5000,
  orderList: 5000,
  equipment: 5000,
  /** 導覽列告警徽章 */
  navBadge: 5000,
} as const
