import client from './client'
import type { AlarmEvent, AlarmSummary, AlarmTrendPoint, PagedResult, AckRequest, SuppressRequest } from '@/types'

/** 查询报警列表（分页 + 过滤） */
export function getAlarms(params: {
  factoryId?: string
  deviceId?: string
  status?: string
  level?: string
  isStale?: boolean
  page?: number
  pageSize?: number
}) {
  return client.get<PagedResult<AlarmEvent>>('/api/alarms', { params })
}

/** 查询报警状态汇总 */
export function getAlarmSummary(factoryId?: string) {
  return client.get<AlarmSummary>('/api/alarms/summary', {
    params: factoryId ? { factoryId } : undefined,
  })
}

/** 查询报警趋势（按小时聚合） */
export function getAlarmTrend(hours = 24, factoryId?: string) {
  return client.get<AlarmTrendPoint[]>('/api/alarms/trend', {
    params: { hours, ...(factoryId ? { factoryId } : {}) },
  })
}

/** 查询单条报警 */
export function getAlarmById(id: string) {
  return client.get<AlarmEvent>(`/api/alarms/${id}`)
}

/** 确认报警 */
export function acknowledgeAlarm(id: string, data: AckRequest) {
  return client.post<AlarmEvent>(`/api/alarms/${id}/ack`, data)
}

/** 抑制报警 */
export function suppressAlarm(id: string, data: SuppressRequest) {
  return client.post<AlarmEvent>(`/api/alarms/${id}/suppress`, data)
}

/** 手动关闭报警 */
export function clearAlarm(id: string) {
  return client.post<AlarmEvent>(`/api/alarms/${id}/clear`)
}
