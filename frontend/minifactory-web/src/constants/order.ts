// 建單表單的欄位限制與後端欄位名對照。

/**
 * 訂單數量上限。
 * 必須與後端設定一致（後端預設 100000）。
 * 【待確認：業務上限】後端調整時，這裡要同步修改。
 */
export const MAX_ORDER_QUANTITY = 100000

/** 產線代號長度上限（與後端一致：32 字） */
export const MAX_LINE_ID_LENGTH = 32

/** 建單表單欄位（對應 CreateOrderDialog 的 form 欄位名） */
export type OrderFormField =
  | 'orderNo'
  | 'productCode'
  | 'quantity'
  | 'lineId'
  | 'idempotencyKey'

/**
 * 後端驗證錯誤的欄位名 → 表單欄位名。
 * key 一律小寫比對（後端可能回 camelCase、PascalCase，或 HTTP 標頭名 Idempotency-Key）。
 */
const SERVER_FIELD_TO_FORM_FIELD: Record<string, OrderFormField> = {
  orderno: 'orderNo',
  productcode: 'productCode',
  quantity: 'quantity',
  lineid: 'lineId',
  'idempotency-key': 'idempotencyKey',
  idempotencykey: 'idempotencyKey',
}

/** 轉換後端欄位名；對不到表單欄位時回傳 null（只由全域提示顯示） */
export function toFormField(serverField: string): OrderFormField | null {
  return SERVER_FIELD_TO_FORM_FIELD[serverField.trim().toLowerCase()] ?? null
}

/** 把後端欄位錯誤整理成「表單欄位 → 訊息」；對不到的欄位略過 */
export function mapServerFieldErrors(
  fieldErrors: Record<string, string[]>,
): Partial<Record<OrderFormField, string>> {
  const mapped: Partial<Record<OrderFormField, string>> = {}
  for (const [field, messages] of Object.entries(fieldErrors)) {
    const target = toFormField(field)
    if (!target) continue
    const text = messages.join('；')
    mapped[target] = mapped[target] ? `${mapped[target]}；${text}` : text
  }
  return mapped
}
