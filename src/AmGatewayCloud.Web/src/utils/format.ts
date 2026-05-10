import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import 'dayjs/locale/zh-cn'

dayjs.extend(relativeTime)
dayjs.locale('zh-cn')

/** 格式化时间为相对时间（如"2 分钟前"） */
export function formatRelativeTime(date: string | Date): string {
  return dayjs(date).fromNow()
}

/** 格式化时间为标准格式 */
export function formatDateTime(date: string | Date): string {
  return dayjs(date).format('YYYY-MM-DD HH:mm:ss')
}

/** 格式化数值（保留指定小数位） */
export function formatNumber(value: number, digits = 1): string {
  return value.toFixed(digits)
}

/** 格式化持续时间（秒 → "1h 15m"） */
export function formatDuration(seconds: number): string {
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}
