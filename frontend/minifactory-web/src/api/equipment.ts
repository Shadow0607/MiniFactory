import { getHttp } from './http'
import type { RequestOptions } from './orders'
import type { EquipmentListResponse } from './types'

/** GET /api/equipment */
export async function listEquipment(
  lineId?: string,
  options: RequestOptions = {},
): Promise<EquipmentListResponse> {
  const res = await getHttp().get<EquipmentListResponse>('/equipment', {
    params: { lineId: lineId ? lineId : undefined },
    silent: options.silent,
  })
  return res.data
}
