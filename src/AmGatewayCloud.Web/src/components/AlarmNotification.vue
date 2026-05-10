<script setup lang="ts">
import { ref, watch, h } from 'vue'
import { notification, Tag } from 'ant-design-vue'
import { useAlarmStore } from '@/stores/alarm'
import { useRouter } from 'vue-router'
import type { AlarmLevel } from '@/types'
import { playAlarmSound } from '@/composables/useAlarmSound'

const alarmStore = useAlarmStore()
const router = useRouter()

/** 报警级别 → Tag 颜色 */
const levelTagColor: Record<AlarmLevel, string> = {
  Fatal: 'red',
  Critical: 'orange',
  Warning: 'gold',
  Info: 'blue',
}

/** 报警级别 → 左边框颜色 */
const levelBorderColor: Record<AlarmLevel, string> = {
  Fatal: '#cf1322',
  Critical: '#fa541c',
  Warning: '#faad14',
  Info: '#1890ff',
}

/** 已显示通知的 ID 集合（防止重复弹窗） */
const notifiedIds = ref(new Set<string>())
const MAX_NOTIFIED = 200

// 追踪上一次 unreadCount，只在增加时触发
let prevUnreadCount = 0

/** 监听 unreadCount 变化 → 弹出通知 */
watch(
  () => alarmStore.unreadCount,
  (newVal) => {
    // unreadCount 增加说明有新报警
    if (newVal <= prevUnreadCount) {
      prevUnreadCount = newVal
      return
    }
    prevUnreadCount = newVal

    // 找到最新的未通知报警
    const latest = alarmStore.alarms.find(
      (a) => !notifiedIds.value.has(a.id)
    )
    if (!latest) return

    // 标记为已通知
    notifiedIds.value.add(latest.id)
    if (notifiedIds.value.size > MAX_NOTIFIED) {
      const arr = Array.from(notifiedIds.value)
      notifiedIds.value = new Set(arr.slice(-100))
    }

    const level = latest.level as AlarmLevel

    // 播放声音
    playAlarmSound(level)

    // 弹出通知
    notification.open({
      key: latest.id,
      message: h('span', { style: 'display: flex; align-items: center; gap: 8px' }, [
        h(Tag, { color: levelTagColor[level] || 'blue' }, () => level),
        h('span', null, latest.ruleName),
      ]),
      description: latest.message,
      duration: level === 'Fatal' || level === 'Critical' ? 0 : 6,
      style: {
        borderLeft: `4px solid ${levelBorderColor[level] || '#1890ff'}`,
        cursor: 'pointer',
      },
      onClick: () => {
        notification.close(latest.id)
        router.push(`/alarms/${latest.id}`)
      },
    })
  }
)

/** 当 unreadCount 清零时，关闭所有通知 */
watch(
  () => alarmStore.unreadCount,
  (val) => {
    if (val === 0) {
      notification.destroy()
      notifiedIds.value.clear()
      prevUnreadCount = 0
    }
  }
)
</script>

<template>
  <!-- 纯逻辑组件，无 UI 渲染 -->
  <slot />
</template>
