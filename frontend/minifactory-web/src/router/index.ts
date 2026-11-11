import { createRouter, createWebHistory, type RouteRecordRaw, type RouterHistory } from 'vue-router'

export const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/orders' },
  {
    path: '/orders',
    name: 'orders',
    component: () => import('@/views/OrdersView.vue'),
    meta: { title: '訂單列表' },
  },
  {
    path: '/orders/:id',
    name: 'order-detail',
    component: () => import('@/views/OrderDetailView.vue'),
    meta: { title: '訂單詳情' },
  },
  {
    path: '/equipment',
    name: 'equipment',
    component: () => import('@/views/EquipmentView.vue'),
    meta: { title: '設備狀態' },
  },
  {
    path: '/alerts',
    name: 'alerts',
    component: () => import('@/views/AlertsView.vue'),
    meta: { title: '告警' },
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/views/NotFoundView.vue'),
    meta: { title: '找不到頁面' },
  },
]

/** history 可注入，測試時改用 createMemoryHistory */
export function createAppRouter(history: RouterHistory = createWebHistory()) {
  const router = createRouter({ history, routes })

  router.afterEach((to) => {
    const title = typeof to.meta.title === 'string' ? to.meta.title : ''
    document.title = title ? `${title} - MiniFactory` : 'MiniFactory'
  })
  return router
}
