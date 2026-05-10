import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import type { AlarmEvent } from '@/types'
import * as alarmApi from '@/api/alarms'
import { useAppStore } from './app'

export interface DeviceInfo {
  deviceId: string
  factoryId: string
  workshopId: string
  lastDataAt: string | null
  isStale: boolean
  latestAlarm: AlarmEvent | null
}

export const useDeviceStore = defineStore('device', () => {
  const staleDevices = ref<DeviceInfo[]>([])
  const allDevices = ref<DeviceInfo[]>([])
  const loading = ref(false)

  /** 离线设备数 */
  const offlineCount = computed(() => staleDevices.value.length)

  /** 在线设备数 */
  const onlineCount = computed(() => Math.max(0, allDevices.value.length - staleDevices.value.length))

  /** 计算离线时长 */
  function formatOfflineDuration(lastDataAt: string | null): string {
    if (!lastDataAt) return '—'
    const last = new Date(lastDataAt).getTime()
    const now = Date.now()
    const diffMs = now - last
    if (diffMs < 0) return '刚刚'
    const minutes = Math.floor(diffMs / 60000)
    const hours = Math.floor(minutes / 60)
    const mins = minutes % 60
    if (hours > 0) return `${hours}h ${mins}m`
    return `${mins}m`
  }

  /** 刷新设备状态（从报警数据中提取设备信息） */
  async function refreshDeviceStatus() {
    const appStore = useAppStore()
    loading.value = true
    try {
      // 获取离线设备（isStale=true）
      const { data: staleData } = await alarmApi.getAlarms({
        factoryId: appStore.selectedFactoryId ?? undefined,
        isStale: true,
        page: 1,
        pageSize: 200,
      })

      // 用 deviceId 去重
      const seen = new Set<string>()
      const stale: DeviceInfo[] = []
      for (const alarm of staleData.items) {
        if (!seen.has(alarm.deviceId)) {
          seen.add(alarm.deviceId)
          stale.push({
            deviceId: alarm.deviceId,
            factoryId: alarm.factoryId,
            workshopId: alarm.workshopId,
            lastDataAt: alarm.triggeredAt,
            isStale: true,
            latestAlarm: alarm,
          })
        }
      }
      staleDevices.value = stale

      // 获取所有设备（不过滤 isStale）
      const { data: allData } = await alarmApi.getAlarms({
        factoryId: appStore.selectedFactoryId ?? undefined,
        page: 1,
        pageSize: 200,
      })

      const allSeen = new Set<string>()
      const all: DeviceInfo[] = []
      for (const alarm of allData.items) {
        if (!allSeen.has(alarm.deviceId)) {
          allSeen.add(alarm.deviceId)
          all.push({
            deviceId: alarm.deviceId,
            factoryId: alarm.factoryId,
            workshopId: alarm.workshopId,
            lastDataAt: alarm.triggeredAt,
            isStale: alarm.isStale,
            latestAlarm: alarm,
          })
        }
      }
      allDevices.value = all
    } catch {
      // 非阻塞
    } finally {
      loading.value = false
    }
  }

  return {
    staleDevices,
    allDevices,
    loading,
    offlineCount,
    onlineCount,
    formatOfflineDuration,
    refreshDeviceStatus,
  }
})
