import axios, {
  AxiosError,
  type AxiosAdapter,
  type AxiosInstance,
  type InternalAxiosRequestConfig,
} from 'axios'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from './errors'
import { installInterceptors } from './interceptors'

let lastConfig: InternalAxiosRequestConfig | null = null

/** 假 adapter：依 config.url 決定回應，不碰網路 */
const adapter: AxiosAdapter = async (config) => {
  lastConfig = config
  const status = Number(config.url?.replace('/', '')) || 200
  const response = {
    status,
    statusText: '',
    data: status === 400
      ? {
          type: 'https://minifactory.dev/errors/validation-failed',
          status: 400,
          errors: { orderNo: ['必填'] },
        }
      : { ok: true },
    headers: {},
    config,
  }
  if (status >= 400) {
    throw new AxiosError('failed', 'ERR_BAD_REQUEST', config, null, response)
  }
  return response
}

describe('installInterceptors', () => {
  let http: AxiosInstance
  let getApiKey: ReturnType<typeof vi.fn<() => string | null>>
  let peekApiKey: ReturnType<typeof vi.fn<() => string | null>>
  let clearApiKey: ReturnType<typeof vi.fn<() => void>>
  let notify: ReturnType<typeof vi.fn<(e: ApiError) => void>>

  beforeEach(() => {
    lastConfig = null
    getApiKey = vi.fn(() => 'test-key')
    peekApiKey = vi.fn(() => 'test-key')
    clearApiKey = vi.fn()
    notify = vi.fn()
    http = axios.create({ adapter })
    installInterceptors(http, { getApiKey, peekApiKey, clearApiKey, notify })
  })

  it('所有請求都帶 X-Correlation-Id；GET 不帶 X-Api-Key', async () => {
    await http.get('/orders')
    expect(lastConfig?.headers.get('X-Correlation-Id')).toBeTruthy()
    expect(lastConfig?.headers.has('X-Api-Key')).toBe(false)
    expect(getApiKey).not.toHaveBeenCalled()
  })

  it('POST 帶 X-Api-Key', async () => {
    await http.post('/orders', {})
    expect(lastConfig?.headers.get('X-Api-Key')).toBe('test-key')
  })

  it('使用者沒有輸入 Key：請求被取消、不送出、不提示', async () => {
    getApiKey.mockReturnValue(null)
    const error = await http.post('/orders', {}).catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).canceled).toBe(true)
    expect(lastConfig).toBeNull()
    expect(notify).not.toHaveBeenCalled()
  })

  it('400：reject ApiError（含欄位錯誤）並提示', async () => {
    const error = (await http.post('/400', {}).catch((e: unknown) => e)) as ApiError
    expect(error).toBeInstanceOf(ApiError)
    expect(error.fieldErrors.orderNo).toEqual(['必填'])
    expect(notify).toHaveBeenCalledTimes(1)
  })

  it('401：清除已儲存的 Key 並提示', async () => {
    await http.post('/401', {}).catch(() => undefined)
    expect(clearApiKey).toHaveBeenCalledTimes(1)
    expect(notify).toHaveBeenCalledTimes(1)
  })

  it('401：請求送出的 Key 與目前儲存的 Key 不同（晚到的舊請求）時，不清除新 Key', async () => {
    // 請求以舊 Key 送出，回應晚到時使用者已輸入新 Key
    getApiKey.mockReturnValue('old-key')
    peekApiKey.mockReturnValue('new-key')
    await http.post('/401', {}).catch(() => undefined)
    expect(clearApiKey).not.toHaveBeenCalled()
    expect(notify).toHaveBeenCalledTimes(1)
  })

  it('401：沒有儲存任何 Key 時不呼叫清除', async () => {
    peekApiKey.mockReturnValue(null)
    await http.post('/401', {}).catch(() => undefined)
    expect(clearApiKey).not.toHaveBeenCalled()
  })

  it('401：未帶 X-Api-Key 的請求（GET）不會清除已儲存的 Key', async () => {
    await http.get('/401').catch(() => undefined)
    expect(clearApiKey).not.toHaveBeenCalled()
  })

  it('silent 請求：失敗不提示，但仍 reject ApiError', async () => {
    const error = await http.get('/503', { silent: true }).catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect(notify).not.toHaveBeenCalled()
  })

  it('一般 GET 失敗會提示', async () => {
    await http.get('/503').catch(() => undefined)
    expect(notify).toHaveBeenCalledTimes(1)
  })
})
