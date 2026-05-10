import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { FactoryNode } from '@/types'
import * as factoryApi from '@/api/factories'
import { useAlarmStore } from './alarm'
import { useDeviceStore } from './device'
import { joinFactory, leaveFactory } from '@/composables/useAlarmSignalR'

export const useAppStore = defineStore('app', () => {
  const selectedFactoryId = ref<string | null>(null)
  const selectedWorkshopId = ref<string | null>(null)
  const sidebarCollapsed = ref(false)
  const factoryTree = ref<FactoryNode[]>([])
  const factoryTreeLoading = ref(false)
  const soundEnabled = ref(
    localStorage.getItem('alarm-sound-enabled') !== 'false'
  )

  /** 选择工厂 → 切换 SignalR 分组 + 刷新各 Store */
  async function selectFactory(id: string | null) {
    const oldFactoryId = selectedFactoryId.value
    selectedFactoryId.value = id
    selectedWorkshopId.value = null

    // SignalR 分组切换
    if (oldFactoryId) await leaveFactory(oldFactoryId)
    if (id) await joinFactory(id)

    // 同步 alarmStore 的工厂 ID（用于 summary/trend 查询）
    const alarmStore = useAlarmStore()
    alarmStore.setFactoryId(id)

    // 并行刷新各 Store 数据
    await Promise.allSettled([
      alarmStore.fetchSummary(),
      alarmStore.fetchTrend(),
      alarmStore.fetchAlarms(),
      useDeviceStore().refreshDeviceStatus(),
    ])
  }

  /** 选择车间 → 过滤报警数据 */
  function selectWorkshop(id: string | null) {
    selectedWorkshopId.value = id
  }

  /** 折叠/展开侧边栏 */
  function toggleSidebar() {
    sidebarCollapsed.value = !sidebarCollapsed.value
  }

  /** 获取工厂/车间树 */
  async function fetchFactoryTree() {
    factoryTreeLoading.value = true
    try {
      const { data } = await factoryApi.getFactoryTree()
      factoryTree.value = data
    } catch {
      // 非阻塞
    } finally {
      factoryTreeLoading.value = false
    }
  }

  /** 切换声音告警 */
  function toggleSound() {
    soundEnabled.value = !soundEnabled.value
    localStorage.setItem('alarm-sound-enabled', String(soundEnabled.value))
  }

  return {
    selectedFactoryId,
    selectedWorkshopId,
    sidebarCollapsed,
    factoryTree,
    factoryTreeLoading,
    soundEnabled,
    selectFactory,
    selectWorkshop,
    toggleSidebar,
    fetchFactoryTree,
    toggleSound,
  }
})
