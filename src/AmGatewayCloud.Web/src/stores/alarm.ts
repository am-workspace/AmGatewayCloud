import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { AlarmEvent, AlarmSummary, AlarmTrendPoint, AlarmStatus, AlarmLevel, AlarmEventMessage } from '@/types'
import * as alarmApi from '@/api/alarms'

export const useAlarmStore = defineStore('alarm', () => {
  // ── 列表查询 ──
  const alarms = ref<AlarmEvent[]>([])
  const totalCount = ref(0)
  const currentPage = ref(1)
  const pageSize = ref(20)
  const loading = ref(false)
  const filters = ref({
    factoryId: null as string | null,
    deviceId: null as string | null,
    status: null as AlarmStatus | null,
    level: null as AlarmLevel | null,
    isStale: null as boolean | null,
  })

  // ── 汇总（统计卡片） ──
  const summary = ref<AlarmSummary | null>(null)

  // ── 趋势（趋势图） ──
  const trendData = ref<AlarmTrendPoint[]>([])
  const trendHours = ref(24)

  // ── 详情 ──
  const currentAlarm = ref<AlarmEvent | null>(null)

  // ── 实时推送未读计数 ──
  const unreadCount = ref(0)

  // ── 当前工厂 ID（由 appStore 同步） ──
  let _factoryId: string | null = null

  /** 设置工厂 ID（由 appStore.selectFactory 调用） */
  function setFactoryId(id: string | null) {
    _factoryId = id
  }

  /** 查询报警列表 */
  async function fetchAlarms() {
    loading.value = true
    try {
      const { data } = await alarmApi.getAlarms({
        factoryId: filters.value.factoryId ?? undefined,
        deviceId: filters.value.deviceId ?? undefined,
        status: filters.value.status ?? undefined,
        level: filters.value.level ?? undefined,
        isStale: filters.value.isStale ?? undefined,
        page: currentPage.value,
        pageSize: pageSize.value,
      })
      alarms.value = data.items
      totalCount.value = data.totalCount
    } finally {
      loading.value = false
    }
  }

  /** 查询单条报警 */
  async function fetchAlarmById(id: string) {
    loading.value = true
    try {
      const { data } = await alarmApi.getAlarmById(id)
      currentAlarm.value = data
    } finally {
      loading.value = false
    }
  }

  /** 查询报警状态汇总 */
  async function fetchSummary() {
    try {
      const { data } = await alarmApi.getAlarmSummary(_factoryId ?? undefined)
      summary.value = data
    } catch {
      // 静默失败，统计卡片不阻塞页面
    }
  }

  /** 查询报警趋势 */
  async function fetchTrend(hours?: number) {
    const h = hours ?? trendHours.value
    try {
      const { data } = await alarmApi.getAlarmTrend(h, _factoryId ?? undefined)
      trendData.value = data
    } catch {
      // 非阻塞
    }
  }

  /** 确认报警 Active → Acked */
  async function acknowledge(id: string, by: string) {
    const { data } = await alarmApi.acknowledgeAlarm(id, { acknowledgedBy: by })
    updateAlarmInList(data)
    if (currentAlarm.value?.id === id) currentAlarm.value = data
    // 汇总可能变化
    fetchSummary()
  }

  /** 抑制报警 Active/Acked → Suppressed */
  async function suppress(id: string, by: string, reason?: string) {
    const { data } = await alarmApi.suppressAlarm(id, { suppressedBy: by, reason })
    updateAlarmInList(data)
    if (currentAlarm.value?.id === id) currentAlarm.value = data
    fetchSummary()
  }

  /** 手动关闭报警 → Cleared */
  async function clear(id: string) {
    const { data } = await alarmApi.clearAlarm(id)
    updateAlarmInList(data)
    if (currentAlarm.value?.id === id) currentAlarm.value = data
    fetchSummary()
  }

  /** 处理 SignalR 推送的报警事件 — 按 status 分发 */
  function handleSignalREvent(msg: AlarmEventMessage) {
    const status = msg.status

    if (status === 'Active') {
      // 新报警：未读计数 +1，插入列表顶部
      unreadCount.value++

      const existing = alarms.value.find((a) => a.id === msg.id)
      if (!existing) {
        alarms.value.unshift(msg as unknown as AlarmEvent)
        totalCount.value++
      }
    } else {
      // Cleared / Acked / Suppressed：静默更新列表中的已有项
      const idx = alarms.value.findIndex((a) => a.id === msg.id)
      if (idx !== -1) {
        const old = alarms.value[idx]
        alarms.value[idx] = {
          ...old,
          status: msg.status,
          clearedAt: msg.clearedAt ?? old.clearedAt,
          clearValue: msg.clearValue ?? old.clearValue,
          suppressedAt: msg.suppressedAt ?? old.suppressedAt,
          suppressedBy: msg.suppressedBy ?? old.suppressedBy,
          suppressedReason: msg.suppressedReason ?? old.suppressedReason,
        } as AlarmEvent
      }
    }

    // 汇总可能变化，延迟刷新
    fetchSummary()
  }

  /** 在列表中更新单条报警 */
  function updateAlarmInList(updated: AlarmEvent) {
    const idx = alarms.value.findIndex((a) => a.id === updated.id)
    if (idx !== -1) {
      alarms.value[idx] = updated
    }
  }

  /** 清零未读计数 */
  function markAllRead() {
    unreadCount.value = 0
  }

  /** 重置过滤条件 */
  function resetFilters() {
    filters.value = {
      factoryId: null,
      deviceId: null,
      status: null,
      level: null,
      isStale: null,
    }
  }

  return {
    alarms,
    totalCount,
    currentPage,
    pageSize,
    loading,
    filters,
    summary,
    trendData,
    trendHours,
    currentAlarm,
    unreadCount,
    setFactoryId,
    fetchAlarms,
    fetchAlarmById,
    fetchSummary,
    fetchTrend,
    acknowledge,
    suppress,
    clear,
    handleSignalREvent,
    markAllRead,
    resetFilters,
  }
})
