import client from './client'
import type { AlarmRule, CreateAlarmRuleRequest, UpdateAlarmRuleRequest } from '@/types'

/** 查询规则列表 */
export function getRules(params?: { factoryId?: string; tag?: string }) {
  return client.get<AlarmRule[]>('/api/alarmrules', { params })
}

/** 查询单条规则 */
export function getRuleById(id: string) {
  return client.get<AlarmRule>(`/api/alarmrules/${id}`)
}

/** 创建规则 */
export function createRule(data: CreateAlarmRuleRequest) {
  return client.post<AlarmRule>('/api/alarmrules', data)
}

/** 更新规则 */
export function updateRule(id: string, data: UpdateAlarmRuleRequest) {
  return client.put<AlarmRule>(`/api/alarmrules/${id}`, data)
}

/** 删除规则 */
export function deleteRule(id: string) {
  return client.delete(`/api/alarmrules/${id}`)
}
