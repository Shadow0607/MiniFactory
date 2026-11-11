import { AxiosError, AxiosHeaders, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios'
import { describe, expect, it } from 'vitest'
import { ApiError, describeApiError, problemSlug, toApiError } from './errors'

/** 建立帶 HTTP 回應的 AxiosError */
function httpError(status: number, data: unknown, headers: Record<string, string> = {}) {
  const config = { headers: new AxiosHeaders() } as InternalAxiosRequestConfig
  const response = {
    status,
    statusText: '',
    data,
    headers,
    config,
  } as AxiosResponse
  return new AxiosError('failed', 'ERR_BAD_REQUEST', config, null, response)
}

describe('problemSlug', () => {
  it('取出 type URI 最後一段', () => {
    expect(problemSlug('https://minifactory.dev/errors/order-no-conflict')).toBe('order-no-conflict')
    expect(problemSlug(undefined)).toBeNull()
  })
})

describe('toApiError', () => {
  it('400 validation-failed：保留欄位錯誤與 traceId', () => {
    const err = toApiError(
      httpError(400, {
        type: 'https://minifactory.dev/errors/validation-failed',
        title: 'One or more validation errors occurred',
        status: 400,
        errors: { quantity: ['quantity 必須大於 0'], lineId: ['產線 \'Line-99\' 不存在'] },
        traceId: '00-6e0c63257de34c926f9efcd03899a01f-1a2b3c4d5e6f7081-01',
      }),
    )
    expect(err).toBeInstanceOf(ApiError)
    expect(err.status).toBe(400)
    expect(err.slug).toBe('validation-failed')
    expect(err.hasFieldErrors).toBe(true)
    expect(err.fieldErrors.quantity).toEqual(['quantity 必須大於 0'])
    expect(err.traceId).toContain('6e0c63257de34c926f9efcd03899a01f')
    expect(err.message).toBe('資料驗證失敗，請檢查欄位內容')
  })

  it('409 order-no-conflict：優先使用後端 detail', () => {
    const err = toApiError(
      httpError(409, {
        type: 'https://minifactory.dev/errors/order-no-conflict',
        title: 'Order number already exists',
        status: 409,
        detail: "訂單編號 'ORD-1' 已存在",
      }),
    )
    expect(err.message).toBe("訂單編號 'ORD-1' 已存在")
    expect(err.slug).toBe('order-no-conflict')
  })

  it('503：讀取 Retry-After', () => {
    const err = toApiError(
      httpError(
        503,
        { type: 'https://minifactory.dev/errors/service-unavailable', status: 503 },
        { 'retry-after': '5' },
      ),
    )
    expect(err.retryAfterSeconds).toBe(5)
    expect(err.message).toContain('暫時無法使用')
  })

  it('回應不是 Problem Details（例如 nginx 502 HTML）：依狀態碼給預設訊息', () => {
    const err = toApiError(httpError(502, '<html>Bad Gateway</html>'))
    expect(err.status).toBe(502)
    expect(err.type).toBeNull()
    expect(err.message).toContain('暫時無法使用')
  })

  it('沒有回應：視為連線錯誤', () => {
    const err = toApiError(new AxiosError('Network Error', 'ERR_NETWORK'))
    expect(err.isNetworkError).toBe(true)
    expect(err.status).toBeNull()
  })

  it('逾時：訊息為連線逾時', () => {
    const err = toApiError(new AxiosError('timeout', 'ECONNABORTED'))
    expect(err.isNetworkError).toBe(true)
    expect(err.message).toContain('逾時')
  })

  it('已經是 ApiError 就原樣回傳', () => {
    const original = new ApiError({ message: 'x' })
    expect(toApiError(original)).toBe(original)
  })
})

describe('describeApiError', () => {
  it('400 欄位錯誤：逐欄位一行', () => {
    const err = new ApiError({
      message: 'x',
      status: 400,
      fieldErrors: { quantity: ['必須大於 0'], lineId: ['不存在', '格式錯誤'] },
    })
    const { title, lines } = describeApiError(err)
    expect(title).toBe('資料驗證失敗')
    expect(lines).toEqual(['quantity：必須大於 0', 'lineId：不存在', 'lineId：格式錯誤'])
  })

  it('其他錯誤：包含訊息與追蹤碼', () => {
    const err = new ApiError({ message: '伺服器錯誤', status: 500, traceId: 'trace-1' })
    const { lines } = describeApiError(err)
    expect(lines).toContain('伺服器錯誤')
    expect(lines).toContain('追蹤碼：trace-1')
  })
})
