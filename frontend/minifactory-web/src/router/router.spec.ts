// @vitest-environment happy-dom
import { createMemoryHistory } from 'vue-router'
import { describe, expect, it } from 'vitest'
import { createAppRouter, routes } from './index'

describe('路由', () => {
  const cases: Array<[string, string]> = [
    ['/orders', 'orders'],
    ['/orders/abc-123', 'order-detail'],
    ['/equipment', 'equipment'],
    ['/alerts', 'alerts'],
  ]

  for (const [path, name] of cases) {
    it(`${path} 可解析為 ${name}，且頁面元件可載入`, async () => {
      const router = createAppRouter(createMemoryHistory())
      await router.push(path)
      expect(router.currentRoute.value.name).toBe(name)

      // 惰性載入的頁面元件實際 import 一次，確認路徑沒寫錯
      const record = routes.find((r) => r.name === name)
      const loader = record?.component as (() => Promise<unknown>) | undefined
      expect(typeof loader).toBe('function')
      expect(await loader!()).toBeTruthy()
    })
  }

  it('/orders/:id 帶出 id 參數', async () => {
    const router = createAppRouter(createMemoryHistory())
    await router.push('/orders/abc-123')
    expect(router.currentRoute.value.params.id).toBe('abc-123')
  })

  it('/ 導向 /orders', async () => {
    const router = createAppRouter(createMemoryHistory())
    await router.push('/')
    expect(router.currentRoute.value.fullPath).toBe('/orders')
  })

  it('未知路徑落到 not-found', async () => {
    const router = createAppRouter(createMemoryHistory())
    await router.push('/no/such/page')
    expect(router.currentRoute.value.name).toBe('not-found')
  })

  it('切換路由後更新文件標題', async () => {
    const router = createAppRouter(createMemoryHistory())
    await router.push('/alerts')
    expect(document.title).toBe('告警 - MiniFactory')
  })
})
