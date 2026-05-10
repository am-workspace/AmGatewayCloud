<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import {
  Row,
  Col,
  Card,
  Statistic,
  Table,
  Tag,
  Button,
  Space,
  Tooltip,
  Select,
  SelectOption,
  Empty,
  Spin,
} from 'ant-design-vue'
import {
  ReloadOutlined,
  WarningOutlined,
  CheckCircleOutlined,
  StopOutlined,
  SafetyOutlined,
  ExclamationCircleOutlined,
} from '@ant-design/icons-vue'
import { useAlarmStore } from '@/stores/alarm'
import { useAppStore } from '@/stores/app'
import TrendChart from '@/components/TrendChart.vue'
import AlarmActionModal from '@/components/AlarmActionModal.vue'
import { formatRelativeTime, formatNumber } from '@/utils/format'
import { ALARM_LEVEL_TAG_COLOR, ALARM_STATUS_TAG_COLOR } from '@/utils/constants'
import type { AlarmEvent, AlarmLevel } from '@/types'

const alarmStore = useAlarmStore()
const appStore = useAppStore()

// ── 看板只显示 Active / Acked 报警 ──
const dashboardAlarms = computed(() =>
  alarmStore.alarms.filter((a) => a.status === 'Active' || a.status === 'Acked')
)

// ── 统计卡片数据 ──
const summaryCards = computed(() => {
  const s = alarmStore.summary
  return [
    {
      key: 'active',
      title: '活跃',
      value: s?.active ?? 0,
      color: '#ff4d4f',
      icon: WarningOutlined,
      bg: '#fff1f0',
    },
    {
      key: 'acked',
      title: '已确认',
      value: s?.acked ?? 0,
      color: '#faad14',
      icon: CheckCircleOutlined,
      bg: '#fffbe6',
    },
    {
      key: 'suppressed',
      title: '已抑制',
      value: s?.suppressed ?? 0,
      color: '#1890ff',
      icon: StopOutlined,
      bg: '#e6f7ff',
    },
    {
      key: 'cleared',
      title: '已恢复',
      value: s?.cleared ?? 0,
      color: '#52c41a',
      icon: SafetyOutlined,
      bg: '#f6ffed',
    },
  ]
})

// ── 级别排序 ──
const levelOrder: Record<AlarmLevel, number> = { Fatal: 0, Critical: 1, Warning: 2, Info: 3 }
const sortBy = ref<'level' | 'time'>('level')

const sortedAlarms = computed(() => {
  const list = [...dashboardAlarms.value]
  if (sortBy.value === 'level') {
    list.sort((a, b) => (levelOrder[a.level as AlarmLevel] ?? 9) - (levelOrder[b.level as AlarmLevel] ?? 9))
  }
  // time: 已按 SignalR 推送顺序（最新在前），无需额外排序
  return list
})

// ── 操作 Modal ──
const actionModalVisible = ref(false)
const actionType = ref<'acknowledge' | 'suppress'>('acknowledge')
const actionTarget = ref<AlarmEvent | null>(null)
const actionLoading = ref(false)
const actionModalRef = ref<InstanceType<typeof AlarmActionModal> | null>(null)

function openAcknowledge(alarm: AlarmEvent) {
  actionType.value = 'acknowledge'
  actionTarget.value = alarm
  actionModalVisible.value = true
}

function openSuppress(alarm: AlarmEvent) {
  actionType.value = 'suppress'
  actionTarget.value = alarm
  actionModalVisible.value = true
}

async function handleActionConfirm(payload: { by: string; reason?: string }) {
  if (!actionTarget.value) return
  actionLoading.value = true
  try {
    if (actionType.value === 'acknowledge') {
      await alarmStore.acknowledge(actionTarget.value.id, payload.by)
    } else {
      await alarmStore.suppress(actionTarget.value.id, payload.by, payload.reason)
    }
    actionModalVisible.value = false
    actionModalRef.value?.resetForm()
  } catch {
    // 错误已在 axios 拦截器处理
  } finally {
    actionLoading.value = false
  }
}

// ── 列定义 ──
const columns = [
  {
    title: '级别',
    dataIndex: 'level',
    key: 'level',
    width: 90,
  },
  {
    title: '报警名称',
    dataIndex: 'ruleName',
    key: 'ruleName',
    ellipsis: true,
  },
  {
    title: '设备',
    dataIndex: 'deviceId',
    key: 'deviceId',
    width: 120,
    ellipsis: true,
  },
  {
    title: '触发值',
    key: 'triggerValue',
    width: 100,
  },
  {
    title: '条件',
    key: 'condition',
    width: 130,
  },
  {
    title: '状态',
    dataIndex: 'status',
    key: 'status',
    width: 90,
  },
  {
    title: '时间',
    dataIndex: 'triggeredAt',
    key: 'triggeredAt',
    width: 110,
  },
  {
    title: '操作',
    key: 'action',
    width: 150,
    fixed: 'right' as const,
  },
]

// ── 刷新 ──
const refreshing = ref(false)
async function refresh() {
  refreshing.value = true
  try {
    await Promise.allSettled([
      alarmStore.fetchSummary(),
      alarmStore.fetchTrend(),
      alarmStore.fetchAlarms(),
    ])
  } finally {
    refreshing.value = false
  }
}

// ── 定时刷新汇总（30 秒） ──
let summaryTimer: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  // 初次加载数据
  alarmStore.fetchSummary()
  alarmStore.fetchTrend()
  alarmStore.fetchAlarms()

  // markAllRead（用户进入看板时清零）
  alarmStore.markAllRead()

  // 定时刷新汇总
  summaryTimer = setInterval(() => {
    alarmStore.fetchSummary()
    alarmStore.fetchTrend()
  }, 30000)
})

onUnmounted(() => {
  if (summaryTimer) {
    clearInterval(summaryTimer)
    summaryTimer = null
  }
})
</script>

<template>
  <div class="dashboard-view">
    <!-- 统计卡片区 -->
    <Row :gutter="16" class="summary-row">
      <Col v-for="card in summaryCards" :key="card.key" :xs="12" :sm="12" :md="6">
        <Card class="summary-card" :style="{ borderTop: `3px solid ${card.color}` }">
          <Statistic
            :title="card.title"
            :value="card.value"
            :value-style="{ color: card.color, fontWeight: 600 }"
          >
            <template #prefix>
              <component :is="card.icon" />
            </template>
          </Statistic>
        </Card>
      </Col>
    </Row>

    <!-- 24小时趋势图 -->
    <Card class="trend-card" title="24小时报警趋势" :bordered="false" size="small">
      <template #extra>
        <Select v-model:value="alarmStore.trendHours" size="small" style="width: 100px" @change="alarmStore.fetchTrend()">
          <SelectOption :value="8">近 8 小时</SelectOption>
          <SelectOption :value="24">近 24 小时</SelectOption>
          <SelectOption :value="48">近 48 小时</SelectOption>
        </Select>
      </template>
      <TrendChart :data="alarmStore.trendData" />
    </Card>

    <!-- 实时报警列表 -->
    <Card
      class="alarm-list-card"
      title="实时报警"
      :bordered="false"
      size="small"
    >
      <template #extra>
        <Space>
          <Select v-model:value="sortBy" size="small" style="width: 120px">
            <SelectOption value="level">按级别排序</SelectOption>
            <SelectOption value="time">按时间排序</SelectOption>
          </Select>
          <Tooltip title="刷新">
            <Button type="text" size="small" :loading="refreshing" @click="refresh">
              <ReloadOutlined />
            </Button>
          </Tooltip>
        </Space>
      </template>

      <Spin :spinning="alarmStore.loading">
        <Table
          v-if="sortedAlarms.length > 0"
          :columns="columns"
          :data-source="sortedAlarms"
          :pagination="false"
          :scroll="{ x: 900 }"
          row-key="id"
          size="small"
          :row-class-name="(record: AlarmEvent) => `alarm-row-level-${record.level}`"
        >
          <!-- 级别 -->
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'level'">
              <Tag :color="ALARM_LEVEL_TAG_COLOR[record.level as AlarmLevel] || 'default'">
                {{ record.level }}
              </Tag>
            </template>

            <!-- 触发值 -->
            <template v-else-if="column.key === 'triggerValue'">
              <span style="font-weight: 600; color: #ff4d4f">
                {{ formatNumber(record.triggerValue) }}
              </span>
            </template>

            <!-- 条件 -->
            <template v-else-if="column.key === 'condition'">
              <span class="condition-text">
                {{ [record.tag, record.operator, record.thresholdString ?? record.threshold].filter(Boolean).join(' ') }}
              </span>
            </template>

            <!-- 状态 -->
            <template v-else-if="column.key === 'status'">
              <Tag :color="ALARM_STATUS_TAG_COLOR[record.status as keyof typeof ALARM_STATUS_TAG_COLOR] || 'default'">
                {{ record.status }}
              </Tag>
            </template>

            <!-- 时间 -->
            <template v-else-if="column.key === 'triggeredAt'">
              <Tooltip :title="record.triggeredAt">
                {{ formatRelativeTime(record.triggeredAt) }}
              </Tooltip>
            </template>

            <!-- 操作 -->
            <template v-else-if="column.key === 'action'">
              <Space size="small">
                <Button
                  v-if="record.status === 'Active'"
                  type="link"
                  size="small"
                  @click="openAcknowledge(record)"
                >
                  确认
                </Button>
                <Button
                  v-if="record.status === 'Active' || record.status === 'Acked'"
                  type="link"
                  size="small"
                  danger
                  @click="openSuppress(record)"
                >
                  抑制
                </Button>
              </Space>
            </template>
          </template>
        </Table>
        <Empty v-else description="暂无活跃报警" />
      </Spin>
    </Card>

    <!-- 操作 Modal -->
    <AlarmActionModal
      ref="actionModalRef"
      :visible="actionModalVisible"
      :action="actionType"
      :alarm-name="actionTarget?.ruleName ?? ''"
      :loading="actionLoading"
      @update:visible="actionModalVisible = $event"
      @confirm="handleActionConfirm"
    />
  </div>
</template>

<style scoped>
.dashboard-view {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.summary-row {
  margin-bottom: 0;
}

.summary-card {
  text-align: center;
}

.summary-card :deep(.ant-statistic-title) {
  font-size: 13px;
  color: #8c8c8c;
}

.summary-card :deep(.ant-statistic-content) {
  font-size: 28px;
}

.trend-card :deep(.ant-card-head) {
  min-height: 36px;
}

.trend-card :deep(.ant-card-head-title) {
  font-size: 14px;
  padding: 8px 0;
}

.alarm-list-card :deep(.ant-card-head) {
  min-height: 36px;
}

.alarm-list-card :deep(.ant-card-head-title) {
  font-size: 14px;
  padding: 8px 0;
}

.condition-text {
  font-family: 'SFMono-Regular', Consolas, monospace;
  font-size: 12px;
  color: #595959;
}

/* 级别行高亮 */
:deep(.alarm-row-level-Fatal) {
  background: #fff1f0 !important;
}

:deep(.alarm-row-level-Critical) {
  background: #fff2f0 !important;
}

:deep(.alarm-row-level-Warning) {
  background: #fffbe6 !important;
}
</style>
