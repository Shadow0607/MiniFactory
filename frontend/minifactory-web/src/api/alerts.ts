import { getHttp } from './http'
import type { RequestOptions } from './orders'
import type { Alert, AlertListQuery, AlertListResponse } from './types'

/** GET /api/alerts */
export async function listAlerts(
  query: AlertListQuery = {},
  options: RequestOptions = {},
): Promise<AlertListResponse> {
  const res = await getHttp().get<AlertListResponse>('/alerts', {
    params: { ...query },
    silent: options.silent,
  })
  return res.data
}

/** POST /api/alerts/{id}/resolve（需 Operator Key；note 選填，最多 500 字） */
export async function resolveAlert(id: string, note?: string): Promise<Alert> {
  const body = note && note.trim() !== '' ? { note: note.trim() } : {}
  const res = await getHttp().post<Alert>(`/alerts/${encodeURIComponent(id)}/resolve`, body)
  return res.data
}
