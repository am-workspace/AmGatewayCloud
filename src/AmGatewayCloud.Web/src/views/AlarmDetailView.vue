<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  Card,
  Descriptions,
  DescriptionsItem,
  Tag,
  Button,
  Space,
  Steps,
  Step,
  Popconfirm,
  Spin,
  Result,
  Tooltip,
} from 'ant-design-vue'
import {
  ArrowLeftOutlined,
  CheckCircleOutlined,
  StopOutlined,
  CloseCircleOutlined,
  WifiOutlined,
} from '@ant-design/icons-vue'
import { useAlarmStore } from '@/stores/alarm'
import AlarmActionModal from '@/components/AlarmActionModal.vue'
import { formatDateTime, formatNumber } from '@/utils/format'
import { ALARM_LEVEL_TAG_COLOR, ALARM_STATUS_TAG_COLOR, ALARM_STATUS_COLOR } from '@/utils/constants'
import type { AlarmEvent, AlarmLevel, AlarmStatus } from '@/types'

const route = useRoute()
const router = useRouter()
const alarmStore = useAlarmStore()

const alarmId = computed(() => route.params.id as string)
const alarm = computed(() => alarmStore.currentAlarm)
const loading = computed(() => alarmStore.loading)

// ── 状态流转 Steps ──
const statusSteps: AlarmStatus[] = ['Active', 'Acked', 'Suppressed', 'Cleared']

const currentStep = computed(() => {
  if (!alarm.value) return 0
  const idx = statusSteps.indexOf(alarm.value.status)
  return idx >= 0 ? idx : 0
})

// 各步骤的时间描述
const stepDescriptions = computed(() => {
  const a = alarm.value
  if (!a) return ['', '', '', '']

  return [
    formatDateTime(a.triggeredAt),                         // Active
    a.acknowledgedAt ? formatDateTime(a.acknowledgedAt) : '', // Acked
    a.suppressedAt ? formatDateTime(a.suppressedAt) : '',     // Suppressed
    a.clearedAt ? formatDateTime(a.clearedAt) : '',          // Cleared
  ]
})

// ── 操作 Modal ──
const actionModalVisible = ref(false)
const actionType = ref<'acknowledge' | 'suppress'>('acknowledge')
const actionLoading = ref(false)
const actionModalRef = ref<InstanceType<typeof AlarmActionModal> | null>(null)

function openAcknowledge() {
  actionType.value = 'acknowledge'
  actionModalVisible.value = true
}

function openSuppress() {
  actionType.value = 'suppress'
  actionModalVisible.value = true
}

async function handleActionConfirm(payload: { by: string; reason?: string }) {
  if (!alarm.value) return
  actionLoading.value = true
  try {
    if (actionType.value === 'acknowledge') {
      await alarmStore.acknowledge(alarm.value.id, payload.by)
    } else {
      await alarmStore.suppress(alarm.value.id, payload.by, payload.reason)
    }
    actionModalVisible.value = false
    actionModalRef.value?.resetForm()
    // 重新加载详情
    await alarmStore.fetchAlarmById(alarm.value.id)
  } finally {
    actionLoading.value = false
  }
}

async function handleClear() {
  if (!alarm.value) return
  try {
    await alarmStore.clear(alarm.value.id)
    await alarmStore.fetchAlarmById(alarm.value.id)
  } catch {
    // 错误已在 axios 拦截器处理
  }
}

function goBack() {
  router.push('/alarms')
}

// ── 加载详情 ──
onMounted(() => {
  if (alarmId.value) {
    alarmStore.fetchAlarmById(alarmId.value)
  }
})
</script>

<template>
  <div class="alarm-detail-view">
    <!-- 顶部操作栏 -->
    <div class="detail-header">
      <Button type="text" @click="goBack">
        <ArrowLeftOutlined /> 返回列表
      </Button>
    </div>

    <Spin :spinning="loading">
      <template v-if="alarm">
        <!-- 基本信息 -->
        <Card title="基本信息" :bordered="false" size="small" class="detail-card">
          <Descriptions :column="{ xs: 1, sm: 2, md: 3 }" size="small">
            <DescriptionsItem label="规则">
              <span class="mono-text">{{ alarm.ruleId }}</span>
              <span style="margin-left: 8px">{{ alarm.ruleName }}</span>
            </DescriptionsItem>
            <DescriptionsItem label="设备">
              {{ alarm.deviceId }}
              <Tooltip v-if="alarm.isStale" title="设备离线">
                <WifiOutlined style="color: #ff4d4f; margin-left: 4px" />
              </Tooltip>
            </DescriptionsItem>
            <DescriptionsItem label="工厂">{{ alarm.factoryId }}</DescriptionsItem>
            <DescriptionsItem label="车间">{{ alarm.workshopId }}</DescriptionsItem>
            <DescriptionsItem label="测点">
              <span class="mono-text">{{ alarm.tag }}</span>
            </DescriptionsItem>
            <DescriptionsItem label="运算符">
              <span class="mono-text">{{ alarm.tag }} {{ alarm.operator }} {{ alarm.threshold }}</span>
            </DescriptionsItem>
            <DescriptionsItem label="触发值">
              <span style="font-weight: 600; color: #ff4d4f; font-size: 16px">
                {{ formatNumber(alarm.triggerValue) }}
              </span>
            </DescriptionsItem>
            <DescriptionsItem label="触发时间">
              {{ formatDateTime(alarm.triggeredAt) }}
            </DescriptionsItem>
            <DescriptionsItem label="当前状态">
              <Tag
                :color="ALARM_STATUS_TAG_COLOR[alarm.status as AlarmStatus] || 'default'"
                style="font-size: 13px; padding: 2px 12px"
              >
                {{ alarm.status }}
              </Tag>
              <Tag
                :color="ALARM_LEVEL_TAG_COLOR[alarm.level as AlarmLevel] || 'default'"
                style="margin-left: 8px"
              >
                {{ alarm.level }}
              </Tag>
            </DescriptionsItem>
          </Descriptions>
        </Card>

        <!-- 状态流转 -->
        <Card title="状态流转" :bordered="false" size="small" class="detail-card">
          <Steps :current="currentStep" size="small">
            <Step
              v-for="(step, idx) in statusSteps"
              :key="step"
              :title="step"
              :description="stepDescriptions[idx]"
            />
          </Steps>

          <!-- 确认/抑制信息 -->
          <Descriptions v-if="alarm.acknowledgedBy || alarm.suppressedBy" :column="{ xs: 1, sm: 2 }" size="small" style="margin-top: 16px">
            <DescriptionsItem v-if="alarm.acknowledgedBy" label="确认人">
              {{ alarm.acknowledgedBy }}
            </DescriptionsItem>
            <DescriptionsItem v-if="alarm.acknowledgedAt" label="确认时间">
              {{ formatDateTime(alarm.acknowledgedAt) }}
            </DescriptionsItem>
            <DescriptionsItem v-if="alarm.suppressedBy" label="抑制人">
              {{ alarm.suppressedBy }}
            </DescriptionsItem>
            <DescriptionsItem v-if="alarm.suppressedReason" label="抑制原因">
              {{ alarm.suppressedReason }}
            </DescriptionsItem>
            <DescriptionsItem v-if="alarm.suppressedAt" label="抑制时间">
              {{ formatDateTime(alarm.suppressedAt) }}
            </DescriptionsItem>
          </Descriptions>
        </Card>

        <!-- 操作区 -->
        <Card title="操作" :bordered="false" size="small" class="detail-card">
          <Space>
            <Button
              v-if="alarm.status === 'Active'"
              type="primary"
              @click="openAcknowledge"
            >
              <CheckCircleOutlined /> 确认报警
            </Button>
            <Button
              v-if="alarm.status === 'Active' || alarm.status === 'Acked'"
              danger
              @click="openSuppress"
            >
              <StopOutlined /> 手动抑制
            </Button>
            <Popconfirm
              v-if="alarm.status !== 'Cleared'"
              title="确定要手动关闭此报警吗？关闭后不可恢复。"
              ok-text="确定关闭"
              cancel-text="取消"
              @confirm="handleClear"
            >
              <Button danger>
                <CloseCircleOutlined /> 手动关闭
              </Button>
            </Popconfirm>
          </Space>
        </Card>

        <!-- 恢复信息 -->
        <Card title="恢复信息" :bordered="false" size="small" class="detail-card">
          <Descriptions :column="{ xs: 1, sm: 2 }" size="small">
            <DescriptionsItem label="恢复值">
              <span v-if="alarm.clearValue != null" class="mono-text">
                {{ formatNumber(alarm.clearValue) }}
              </span>
              <span v-else-if="alarm.clearedAt" style="color: #bfbfbf">—</span>
              <span v-else style="color: #bfbfbf">条件恢复后自动填充</span>
            </DescriptionsItem>
            <DescriptionsItem label="恢复时间">
              {{ alarm.clearedAt ? formatDateTime(alarm.clearedAt) : '—' }}
            </DescriptionsItem>
          </Descriptions>
          <Descriptions :column="1" size="small" style="margin-top: 8px">
            <DescriptionsItem label="离线标记">
              <Tag :color="alarm.isStale ? 'error' : 'success'">
                {{ alarm.isStale ? '离线' : '正常' }}
              </Tag>
            </DescriptionsItem>
          </Descriptions>
        </Card>

        <!-- 报警消息 -->
        <Card title="报警消息" :bordered="false" size="small" class="detail-card">
          <div class="alarm-message">{{ alarm.message }}</div>
        </Card>
      </template>

      <!-- 无数据 -->
      <Result v-else-if="!loading" status="404" title="报警不存在" sub-title="该报警事件可能已被清理">
        <template #extra>
          <Button type="primary" @click="goBack">返回列表</Button>
        </template>
      </Result>
    </Spin>

    <!-- 操作 Modal -->
    <AlarmActionModal
      ref="actionModalRef"
      :visible="actionModalVisible"
      :action="actionType"
      :alarm-name="alarm?.ruleName ?? ''"
      :loading="actionLoading"
      @update:visible="actionModalVisible = $event"
      @confirm="handleActionConfirm"
    />
  </div>
</template>

<style scoped>
.alarm-detail-view {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.detail-header {
  display: flex;
  align-items: center;
}

.detail-card :deep(.ant-card-head) {
  min-height: 36px;
}

.detail-card :deep(.ant-card-head-title) {
  font-size: 14px;
  padding: 8px 0;
}

.mono-text {
  font-family: 'SFMono-Regular', Consolas, monospace;
  font-size: 13px;
}

.alarm-message {
  padding: 12px 16px;
  background: #fafafa;
  border-radius: 4px;
  border: 1px solid #f0f0f0;
  color: #595959;
  line-height: 1.6;
}
</style>
