// 报警事件 — 对应后端 AlarmEventDto
export interface AlarmEvent {
  id: string
  ruleId: string
  ruleName: string
  factoryId: string
  workshopId: string
  deviceId: string
  tag: string
  operator: string
  threshold: number
  triggeredValue: number
  level: AlarmLevel
  status: AlarmStatus
  message: string
  isStale: boolean
  acknowledgedBy: string | null
  acknowledgedAt: string | null
  suppressedBy: string | null
  suppressedReason: string | null
  suppressedAt: string | null
  clearedAt: string | null
  triggeredAt: string
}

// 报警规则 — 对应后端 AlarmRuleDto
export interface AlarmRule {
  id: string
  name: string
  factoryId: string
  deviceId: string | null
  tag: string
  operator: string
  threshold: number
  clearThreshold: number | null
  stringThreshold: string | null
  level: AlarmLevel
  delaySeconds: number
  cooldownSeconds: number
  description: string
  enabled: boolean
}

// 报警状态
export type AlarmStatus = 'Active' | 'Acked' | 'Suppressed' | 'Cleared'

// 报警级别
export type AlarmLevel = 'Info' | 'Warning' | 'Critical' | 'Fatal'

// 报警状态汇总 — 统计卡片用
export interface AlarmSummary {
  active: number
  acked: number
  suppressed: number
  cleared: number
}

// 报警趋势数据点 — 趋势图用
export interface AlarmTrendPoint {
  hour: string
  total: number
  critical: number
  warning: number
  info: number
}

// 工厂/车间树 — 侧边栏用
export interface FactoryNode {
  id: string
  name: string
  workshops: WorkshopNode[]
}

export interface WorkshopNode {
  id: string
  name: string
}

// 创建规则请求
export interface CreateAlarmRuleRequest {
  id: string
  name: string
  factoryId: string
  deviceId?: string | null
  tag: string
  operator: string
  threshold: number
  clearThreshold?: number | null
  stringThreshold?: string | null
  level: AlarmLevel
  delaySeconds: number
  cooldownSeconds: number
  description: string
  enabled: boolean
}

// 更新规则请求
export interface UpdateAlarmRuleRequest {
  name?: string
  factoryId?: string
  deviceId?: string | null
  tag?: string
  operator?: string
  threshold?: number
  clearThreshold?: number | null
  stringThreshold?: string | null
  level?: AlarmLevel
  delaySeconds?: number
  cooldownSeconds?: number
  description?: string
  enabled?: boolean
}

// 确认报警请求
export interface AckRequest {
  acknowledgedBy: string
}

// 抑制报警请求
export interface SuppressRequest {
  suppressedBy: string
  reason?: string | null
}

// 分页结果
export interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
  totalPages: number
}

// SignalR 推送消息 — 对应后端 AlarmEventMessage
export interface AlarmEventMessage {
  id: string
  ruleId: string
  ruleName: string
  factoryId: string
  workshopId: string
  deviceId: string
  tag: string
  operator: string
  threshold: number
  triggeredValue: number
  level: AlarmLevel
  status: AlarmStatus
  message: string
  isStale: boolean
  triggeredAt: string
}
