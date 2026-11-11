// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus, { ElInput, ElInputNumber, ElSelect } from 'element-plus'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory } from 'vue-router'
import { ApiError } from '@/api/errors'
import type { CreateOrderResponse } from '@/api/types'
import { MAX_LINE_ID_LENGTH } from '@/constants/order'
import { createAppRouter } from '@/router'
import CreateOrderDialog from './CreateOrderDialog.vue'

vi.mock('@/api/orders', () => ({ createOrder: vi.fn() }))
vi.mock('@/api/equipment', () => ({
  listEquipment: vi.fn().mockResolvedValue({ items: [], stale: false, generatedAt: 'T' }),
}))
// 只替換提示函式，其餘 Element Plus 元件維持真實
vi.mock('element-plus', async (importOriginal) => {
  const original = await importOriginal<typeof import('element-plus')>()
  const ElMessage = Object.assign(vi.fn(), {
    info: vi.fn(),
    success: vi.fn(),
    warning: vi.fn(),
    error: vi.fn(),
  })
  return { ...original, ElMessage }
})
import { ElMessage } from 'element-plus'
import * as ordersApi from '@/api/orders'

function makeResponse(overrides: Partial<CreateOrderResponse> = {}): CreateOrderResponse {
  return {
    orderId: 'o-1',
    orderNo: 'ORD-1',
    status: 'Created',
    dispatchStatus: 'Queued',
    jobIds: ['j1'],
    createdAt: '2026-09-21T02:15:30.123Z',
    ...overrides,
  }
}

async function render() {
  const router = createAppRouter(createMemoryHistory())
  const pushSpy = vi.spyOn(router, 'push').mockResolvedValue(undefined)
  const wrapper = mount(CreateOrderDialog, {
    props: {
      modelValue: false,
      'onUpdate:modelValue': (v: boolean) => void wrapper.setProps({ modelValue: v }),
    },
    // el-select 的彈出層在 happy-dom 會無限遞迴更新，改用 stub；
    // el-input-number 會把超出 max 的值夾回範圍，stub 後才能直接測表單驗證規則。
    // 兩者都以 emit update:modelValue 模擬輸入。
    global: {
      plugins: [ElementPlus, router],
      stubs: { teleport: true, ElSelect: true, ElInputNumber: true },
    },
  })
  // 實際流程是由 false 變 true 才重置表單
  await wrapper.setProps({ modelValue: true })
  await flushPromises()
  return { wrapper, pushSpy }
}

type Wrapper = VueWrapper

function inputByPlaceholder(wrapper: Wrapper, placeholder: string) {
  const found = wrapper.findAllComponents(ElInput).find((c) => c.props('placeholder') === placeholder)
  if (!found) throw new Error(`找不到輸入框：${placeholder}`)
  return found
}

/** 填入表單；quantity 直接 emit，才能測到超出元件限制的值 */
async function fill(
  wrapper: Wrapper,
  v: { orderNo?: string; productCode?: string; quantity?: number; lineId?: string; key?: string },
) {
  if (v.orderNo !== undefined)
    inputByPlaceholder(wrapper, '例：ORD-20260921-001').vm.$emit('update:modelValue', v.orderNo)
  if (v.productCode !== undefined)
    inputByPlaceholder(wrapper, '例：Motor-A').vm.$emit('update:modelValue', v.productCode)
  if (v.quantity !== undefined)
    wrapper.findComponent(ElInputNumber).vm.$emit('update:modelValue', v.quantity)
  if (v.lineId !== undefined) wrapper.findComponent(ElSelect).vm.$emit('update:modelValue', v.lineId)
  if (v.key !== undefined) {
    const input = wrapper.findAllComponents(ElInput).find((c) => String(c.props('maxlength')) === '128')
    if (!input) throw new Error('找不到 Idempotency-Key 輸入框')
    input.vm.$emit('update:modelValue', v.key)
  }
  await flushPromises()
}

/** el-form-item 的錯誤訊息顯示有 100ms 防抖，等它過去才看得到 */
async function waitErrorDebounce() {
  await new Promise((resolve) => setTimeout(resolve, 130))
  await flushPromises()
}

async function clickSubmit(wrapper: Wrapper) {
  const button = wrapper.findAll('button').find((b) => b.text().replace(/\s/g, '') === '建立訂單')
  if (!button) throw new Error('找不到送出按鈕')
  await button.trigger('click')
  await flushPromises()
  await waitErrorDebounce()
}

const validForm = { orderNo: 'ORD-1', productCode: 'Motor-A', quantity: 10, lineId: 'Line-01' }

describe('CreateOrderDialog', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
  })

  it('空白表單送出：顯示必填錯誤，不呼叫 API', async () => {
    const { wrapper } = await render()
    await clickSubmit(wrapper)
    const text = wrapper.text()
    expect(text).toContain('請輸入訂單編號')
    expect(text).toContain('請輸入產品代碼')
    expect(text).toContain('請選擇或輸入產線代號')
    expect(ordersApi.createOrder).not.toHaveBeenCalled()
  })

  it('訂單編號含非法字元：顯示格式錯誤', async () => {
    const { wrapper } = await render()
    await fill(wrapper, { ...validForm, orderNo: 'ORD 1!' })
    await clickSubmit(wrapper)
    expect(wrapper.text()).toContain('訂單編號只能包含英文字母、數字、- 與 _')
    expect(ordersApi.createOrder).not.toHaveBeenCalled()
  })

  it('Idempotency-Key 含空白：顯示格式錯誤，不送出', async () => {
    const { wrapper } = await render()
    await fill(wrapper, { ...validForm, key: 'bad key' })
    await clickSubmit(wrapper)
    expect(wrapper.text()).toContain('Idempotency-Key 需為 1～128 個可見 ASCII 字元')
    expect(ordersApi.createOrder).not.toHaveBeenCalled()
  })

  it('Idempotency-Key 含中文：顯示格式錯誤', async () => {
    const { wrapper } = await render()
    await fill(wrapper, { ...validForm, key: '訂單一' })
    await clickSubmit(wrapper)
    expect(wrapper.text()).toContain('Idempotency-Key 需為 1～128 個可見 ASCII 字元')
  })

  it('quantity 超過 100000 或不合法：顯示範圍錯誤，不送出', async () => {
    const { wrapper } = await render()
    for (const quantity of [100001, 0, -5, 1.5]) {
      await fill(wrapper, { ...validForm, quantity })
      await clickSubmit(wrapper)
      expect(wrapper.text()).toMatch(/數量(不可超過 100000|必須是大於 0 的整數)/)
    }
    expect(ordersApi.createOrder).not.toHaveBeenCalled()
  })

  it('quantity 100001 顯示「不可超過 100000」', async () => {
    const { wrapper } = await render()
    await fill(wrapper, { ...validForm, quantity: 100001 })
    await clickSubmit(wrapper)
    expect(wrapper.text()).toContain('數量不可超過 100000')
  })

  it('數量欄位的 max 為 100000', async () => {
    const { wrapper } = await render()
    expect(wrapper.findComponent(ElInputNumber).props('max')).toBe(100000)
  })

  it('quantity 剛好 100000：通過驗證並送出（trim 欄位、自動產生 Idempotency-Key）', async () => {
    vi.mocked(ordersApi.createOrder).mockResolvedValue({ data: makeResponse(), replayed: false })
    const { wrapper, pushSpy } = await render()
    await fill(wrapper, { ...validForm, productCode: ' Motor-A ', quantity: 100000 })
    await clickSubmit(wrapper)

    expect(ordersApi.createOrder).toHaveBeenCalledTimes(1)
    expect(ordersApi.createOrder).toHaveBeenCalledWith(
      { orderNo: 'ORD-1', productCode: 'Motor-A', quantity: 100000, lineId: 'Line-01' },
      'create-order-ORD-1',
    )
    expect(ElMessage.success).toHaveBeenCalled()
    expect(wrapper.emitted('created')?.[0]).toEqual(['o-1'])
    expect(pushSpy).toHaveBeenCalledWith({ name: 'order-detail', params: { id: 'o-1' } })
    expect(wrapper.props('modelValue')).toBe(false)
  })

  it('前後有空白的合法欄位：先 trim 再驗證，通過後送出 trim 後的值', async () => {
    vi.mocked(ordersApi.createOrder).mockResolvedValue({ data: makeResponse(), replayed: false })
    const { wrapper } = await render()
    await fill(wrapper, {
      orderNo: '  ORD-1  ',
      productCode: ' Motor-A ',
      quantity: 10,
      lineId: ' Line-01 ',
      key: '  my-key-1  ',
    })
    await clickSubmit(wrapper)

    expect(ordersApi.createOrder).toHaveBeenCalledWith(
      { orderNo: 'ORD-1', productCode: 'Motor-A', quantity: 10, lineId: 'Line-01' },
      'my-key-1',
    )
  })

  it('純空白視為未填，不送出', async () => {
    const { wrapper } = await render()
    await fill(wrapper, { orderNo: '   ', productCode: '  ', quantity: 10, lineId: ' ' })
    await clickSubmit(wrapper)
    expect(wrapper.text()).toContain('請輸入訂單編號')
    expect(wrapper.text()).toContain('請輸入產品代碼')
    expect(ordersApi.createOrder).not.toHaveBeenCalled()
  })

  it('使用者自訂 Idempotency-Key 時以該 Key 送出', async () => {
    vi.mocked(ordersApi.createOrder).mockResolvedValue({ data: makeResponse(), replayed: false })
    const { wrapper } = await render()
    await fill(wrapper, { ...validForm, key: 'my-key-1' })
    await clickSubmit(wrapper)
    expect(vi.mocked(ordersApi.createOrder).mock.calls[0]?.[1]).toBe('my-key-1')
  })

  it('dispatchStatus=Pending：顯示中性 info 提示，不顯示 warning', async () => {
    vi.mocked(ordersApi.createOrder).mockResolvedValue({
      data: makeResponse({ dispatchStatus: 'Pending' }),
      replayed: false,
    })
    const { wrapper } = await render()
    await fill(wrapper, validForm)
    await clickSubmit(wrapper)

    expect(ElMessage.info).toHaveBeenCalledWith('訂單已建立，派工事件已排入 Outbox，等待派送')
    expect(ElMessage.warning).not.toHaveBeenCalled()
  })

  it('dispatchStatus=Queued：不顯示派送提示', async () => {
    vi.mocked(ordersApi.createOrder).mockResolvedValue({ data: makeResponse(), replayed: false })
    const { wrapper } = await render()
    await fill(wrapper, validForm)
    await clickSubmit(wrapper)
    expect(ElMessage.info).not.toHaveBeenCalled()
    expect(ElMessage.warning).not.toHaveBeenCalled()
  })

  it('重送（Idempotent-Replayed）：提示先前已送出過', async () => {
    vi.mocked(ordersApi.createOrder).mockResolvedValue({ data: makeResponse(), replayed: true })
    const { wrapper } = await render()
    await fill(wrapper, validForm)
    await clickSubmit(wrapper)
    expect(ElMessage.info).toHaveBeenCalledWith('此請求先前已送出過，已回傳第一次建立的結果')
    expect(ElMessage.success).not.toHaveBeenCalled()
  })

  it('400 的 Idempotency-Key 欄位錯誤：顯示在 Idempotency-Key 欄位下，對話框保持開啟', async () => {
    vi.mocked(ordersApi.createOrder).mockRejectedValue(
      new ApiError({
        status: 400,
        message: '資料驗證失敗',
        fieldErrors: { 'Idempotency-Key': ['長度不可超過 128'], OrderNo: ['已存在'] },
      }),
    )
    const { wrapper } = await render()
    await fill(wrapper, validForm)
    await clickSubmit(wrapper)

    const items = wrapper.findAll('.el-form-item')
    const keyItem = items.find((i) => i.text().includes('Idempotency-Key'))
    const orderNoItem = items.find((i) => i.text().includes('訂單編號'))
    expect(keyItem?.find('.el-form-item__error').text()).toBe('長度不可超過 128')
    expect(orderNoItem?.find('.el-form-item__error').text()).toBe('已存在')
    expect(wrapper.props('modelValue')).toBe(true)
  })

  it('修改欄位後清掉伺服器錯誤', async () => {
    vi.mocked(ordersApi.createOrder).mockRejectedValue(
      new ApiError({ status: 400, message: 'x', fieldErrors: { 'Idempotency-Key': ['伺服器錯誤訊息'] } }),
    )
    const { wrapper } = await render()
    await fill(wrapper, validForm)
    await clickSubmit(wrapper)
    expect(wrapper.text()).toContain('伺服器錯誤訊息')

    await fill(wrapper, { key: 'other-key' })
    await waitErrorDebounce()
    expect(wrapper.text()).not.toContain('伺服器錯誤訊息')
  })

  it('只清除被修改欄位的伺服器錯誤，其他欄位的錯誤保留', async () => {
    vi.mocked(ordersApi.createOrder).mockRejectedValue(
      new ApiError({
        status: 400,
        message: 'x',
        fieldErrors: { 'Idempotency-Key': ['金鑰錯誤訊息'], OrderNo: ['訂單編號錯誤訊息'] },
      }),
    )
    const { wrapper } = await render()
    await fill(wrapper, validForm)
    await clickSubmit(wrapper)
    expect(wrapper.text()).toContain('金鑰錯誤訊息')
    expect(wrapper.text()).toContain('訂單編號錯誤訊息')

    await fill(wrapper, { key: 'other-key' })
    await waitErrorDebounce()
    expect(wrapper.text()).not.toContain('金鑰錯誤訊息')
    expect(wrapper.text()).toContain('訂單編號錯誤訊息')
  })

  it('自動產生的 Idempotency-Key 保留 orderNo 大小寫：ORD-1 與 ord-1 Key 不同', async () => {
    vi.mocked(ordersApi.createOrder).mockResolvedValue({ data: makeResponse(), replayed: false })
    const { wrapper } = await render()
    await fill(wrapper, { ...validForm, orderNo: 'ORD-1' })
    await clickSubmit(wrapper)
    await fill(wrapper, { orderNo: 'ord-1' })
    await clickSubmit(wrapper)

    const calls = vi.mocked(ordersApi.createOrder).mock.calls
    expect(calls).toHaveLength(2)
    expect(calls[0]?.[1]).toBe('create-order-ORD-1')
    expect(calls[1]?.[1]).toBe('create-order-ord-1')
    expect(calls[0]?.[1]).not.toBe(calls[1]?.[1])
  })

  it('orderNo 最長 50 字時自動 Key 不超過 128 字', async () => {
    vi.mocked(ordersApi.createOrder).mockResolvedValue({ data: makeResponse(), replayed: false })
    const { wrapper } = await render()
    await fill(wrapper, { ...validForm, orderNo: 'A'.repeat(50) })
    await clickSubmit(wrapper)
    const key = vi.mocked(ordersApi.createOrder).mock.calls[0]?.[1] as string
    expect(key).toBe(`create-order-${'A'.repeat(50)}`)
    expect(key.length).toBeLessThanOrEqual(128)
  })

  it('lineId 超過 32 字：顯示長度錯誤，不送出', async () => {
    const { wrapper } = await render()
    await fill(wrapper, { ...validForm, lineId: 'L'.repeat(MAX_LINE_ID_LENGTH + 1) })
    await clickSubmit(wrapper)
    expect(wrapper.text()).toContain(`產線代號最多 ${MAX_LINE_ID_LENGTH} 字`)
    expect(ordersApi.createOrder).not.toHaveBeenCalled()
  })

  it('lineId 剛好 32 字：通過驗證並送出', async () => {
    vi.mocked(ordersApi.createOrder).mockResolvedValue({ data: makeResponse(), replayed: false })
    const { wrapper } = await render()
    const lineId = 'L'.repeat(MAX_LINE_ID_LENGTH)
    await fill(wrapper, { ...validForm, lineId })
    await clickSubmit(wrapper)
    expect(MAX_LINE_ID_LENGTH).toBe(32)
    expect(vi.mocked(ordersApi.createOrder).mock.calls[0]?.[0].lineId).toBe(lineId)
  })

  it('送出中鎖定：未完成前再次點擊不會重複呼叫 API', async () => {
    let resolve!: (v: Awaited<ReturnType<typeof ordersApi.createOrder>>) => void
    vi.mocked(ordersApi.createOrder).mockImplementation(() => new Promise((r) => (resolve = r)))
    const { wrapper } = await render()
    await fill(wrapper, validForm)
    await clickSubmit(wrapper)
    await clickSubmit(wrapper)
    expect(ordersApi.createOrder).toHaveBeenCalledTimes(1)

    resolve({ data: makeResponse(), replayed: false })
    await flushPromises()
  })
})
