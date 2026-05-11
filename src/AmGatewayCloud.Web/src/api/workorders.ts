import client from './client'
import type { WorkOrder, WorkOrderSummary, PagedResult, AssignWorkOrderRequest, CompleteWorkOrderRequest } from '@/types'

/** 查询工单列表（分页 + 过滤） */
export function getWorkOrders(params: {
  factoryId?: string
  status?: string
  assignee?: string
  page?: number
  pageSize?: number
}) {
  return client.get<PagedResult<WorkOrder>>('/api/workorders', { params })
}

/** 查询单个工单 */
export function getWorkOrderById(id: string) {
  return client.get<WorkOrder>(`/api/workorders/${id}`)
}

/** 分配工单 */
export function assignWorkOrder(id: string, data: AssignWorkOrderRequest) {
  return client.post<WorkOrder>(`/api/workorders/${id}/assign`, data)
}

/** 完成工单 */
export function completeWorkOrder(id: string, data: CompleteWorkOrderRequest) {
  return client.post<WorkOrder>(`/api/workorders/${id}/complete`, data)
}

/** 工单状态汇总 */
export function getWorkOrderSummary(factoryId?: string) {
  return client.get<WorkOrderSummary>('/api/workorders/summary', {
    params: factoryId ? { factoryId } : undefined,
  })
}
