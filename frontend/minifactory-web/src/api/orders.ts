import { getHttp } from './http'
import type {
  CreateOrderRequest,
  CreateOrderResponse,
  OrderDetail,
  OrderEventsResponse,
  OrderListQuery,
  OrderSummary,
  PagedResult,
} from './types'

export interface RequestOptions {
  /** 背景輪詢用：失敗時不跳提示 */
  silent?: boolean
}

export interface CreateOrderResult {
  data: CreateOrderResponse
  /** 後端回 Idempotent-Replayed: true，代表同 Key 同內容的重送，回傳的是第一次的結果 */
  replayed: boolean
}

/** POST /api/orders（需 Operator Key；Idempotency-Key 選填） */
export async function createOrder(
  body: CreateOrderRequest,
  idempotencyKey?: string,
): Promise<CreateOrderResult> {
  const res = await getHttp().post<CreateOrderResponse>('/orders', body, {
    headers: idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : undefined,
  })
  return { data: res.data, replayed: String(res.headers['idempotent-replayed']) === 'true' }
}

/** GET /api/orders */
export async function listOrders(
  query: OrderListQuery = {},
  options: RequestOptions = {},
): Promise<PagedResult<OrderSummary>> {
  const res = await getHttp().get<PagedResult<OrderSummary>>('/orders', {
    params: {
      status: query.status && query.status.length > 0 ? query.status : undefined,
      orderNo: query.orderNo ? query.orderNo : undefined,
      page: query.page,
      pageSize: query.pageSize,
    },
    silent: options.silent,
  })
  return res.data
}

/** GET /api/orders/{id} */
export async function getOrder(id: string, options: RequestOptions = {}): Promise<OrderDetail> {
  const res = await getHttp().get<OrderDetail>(`/orders/${encodeURIComponent(id)}`, {
    silent: options.silent,
  })
  return res.data
}

/** GET /api/orders/{id}/events（事件時間線；after 為增量查詢用的 eventId） */
export async function getOrderEvents(
  id: string,
  after?: string,
  options: RequestOptions = {},
): Promise<OrderEventsResponse> {
  const res = await getHttp().get<OrderEventsResponse>(
    `/orders/${encodeURIComponent(id)}/events`,
    { params: after ? { after } : undefined, silent: options.silent },
  )
  return res.data
}
