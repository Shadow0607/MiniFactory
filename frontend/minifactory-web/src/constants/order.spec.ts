import { describe, expect, it } from 'vitest'
import { MAX_ORDER_QUANTITY, mapServerFieldErrors, toFormField } from './order'

describe('建單欄位名對照', () => {
  it('Idempotency-Key 標頭名對應到 idempotencyKey', () => {
    expect(toFormField('Idempotency-Key')).toBe('idempotencyKey')
    expect(toFormField('idempotencyKey')).toBe('idempotencyKey')
  })

  it('camelCase 與 PascalCase 都能對應', () => {
    expect(toFormField('orderNo')).toBe('orderNo')
    expect(toFormField('OrderNo')).toBe('orderNo')
    expect(toFormField('LineId')).toBe('lineId')
    expect(toFormField('ProductCode')).toBe('productCode')
    expect(toFormField('Quantity')).toBe('quantity')
  })

  it('對不到的欄位回傳 null', () => {
    expect(toFormField('$.unknown')).toBeNull()
  })

  it('mapServerFieldErrors：合併訊息、略過未知欄位', () => {
    expect(
      mapServerFieldErrors({
        'Idempotency-Key': ['格式不正確'],
        Quantity: ['太大', '必須為整數'],
        other: ['x'],
      }),
    ).toEqual({ idempotencyKey: '格式不正確', quantity: '太大；必須為整數' })
  })

  it('數量上限為 100000（需與後端一致）', () => {
    expect(MAX_ORDER_QUANTITY).toBe(100000)
  })
})
