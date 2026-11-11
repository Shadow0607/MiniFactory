// 後端一律回 UTC；前端以瀏覽器時區顯示（不引入日期套件）。

const EMPTY = '—'

/** 格式化 UTC ISO 字串；timeZone 預設為瀏覽器時區（測試時可指定） */
export function formatDateTime(
  iso: string | null | undefined,
  options: { timeZone?: string; locale?: string } = {},
): string {
  if (!iso) return EMPTY
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return EMPTY
  return new Intl.DateTimeFormat(options.locale ?? 'zh-TW', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
    timeZone: options.timeZone,
  }).format(date)
}
