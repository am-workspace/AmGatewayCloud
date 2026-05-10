import type { AlarmLevel, AlarmStatus } from '@/types'

// 报警级别颜色映射
export const ALARM_LEVEL_COLOR: Record<AlarmLevel, string> = {
  Fatal: '#cf1322',
  Critical: '#ff4d4f',
  Warning: '#faad14',
  Info: '#1890ff',
}

// 报警级别标签颜色（Ant Design Tag color）
export const ALARM_LEVEL_TAG_COLOR: Record<AlarmLevel, string> = {
  Fatal: 'error',
  Critical: 'error',
  Warning: 'warning',
  Info: 'processing',
}

// 报警状态颜色映射
export const ALARM_STATUS_COLOR: Record<AlarmStatus, string> = {
  Active: '#ff4d4f',
  Acked: '#faad14',
  Suppressed: '#1890ff',
  Cleared: '#52c41a',
}

// 报警状态标签颜色
export const ALARM_STATUS_TAG_COLOR: Record<AlarmStatus, string> = {
  Active: 'error',
  Acked: 'warning',
  Suppressed: 'processing',
  Cleared: 'success',
}

// 运算符选项
export const OPERATOR_OPTIONS = [
  { label: '>', value: '>' },
  { label: '>=', value: '>=' },
  { label: '<', value: '<' },
  { label: '<=', value: '<=' },
  { label: '==', value: '==' },
  { label: '!=', value: '!=' },
]

// 报警级别选项
export const ALARM_LEVEL_OPTIONS = [
  { label: '信息', value: 'Info' },
  { label: '警告', value: 'Warning' },
  { label: '严重', value: 'Critical' },
  { label: '致命', value: 'Fatal' },
]

// 报警状态选项
export const ALARM_STATUS_OPTIONS = [
  { label: '活跃', value: 'Active' },
  { label: '已确认', value: 'Acked' },
  { label: '已抑制', value: 'Suppressed' },
  { label: '已恢复', value: 'Cleared' },
]

// 默认分页大小
export const DEFAULT_PAGE_SIZE = 20
