<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import {
  Card,
  Table,
  Tag,
  Button,
  Space,
  Select,
  SelectOption,
  Row,
  Col,
  FormItem,
  Tooltip,
  Popconfirm,
  Empty,
} from 'ant-design-vue'
import {
  SearchOutlined,
  UndoOutlined,
  LinkOutlined,
  WifiOutlined,
} from '@ant-design/icons-vue'
import { useAlarmStore } from '@/stores/alarm'
import { useAppStore } from '@/stores/app'
import AlarmActionModal from '@/components/AlarmActionModal.vue'
import { formatRelativeTime, formatDateTime, formatNumber } from '@/utils/format'
import {
  ALARM_LEVEL_TAG_COLOR,
  ALARM_STATUS_TAG_COLOR,
  ALARM_LEVEL_OPTIONS,
  ALARM_STATUS_OPTIONS,
} from '@/utils/constants'
import type { AlarmEvent, AlarmLevel, AlarmStatus } from '@/types'

const router = useRouter()
const alarmStore = useAlarmStore()
const appStore = useAppStore()

// ── 本地过滤表单 ──
const filterForm = reactive({
  factoryId: null as string | null,
  status: null as AlarmStatus | null,
  level: null as AlarmLevel | null,
  isStale: null as boolean | null,
})

// ── 工厂选项 ──
const factoryOptions = computed(() =>
  appStore.factoryTree.map((f) => ({ label: f.name, value: f.id }))
)

// ── 查询 ──
function handleSearch() {
  alarmStore.filters.factoryId = filterForm.factoryId
  alarmStore.filters.status = filterForm.status
  alarmStore.filters.level = filterForm.level
  alarmStore.filters.isStale = filterForm.isStale
  alarmStore.currentPage = 1
  alarmStore.fetchAlarms()
}

function handleReset() {
  filterForm.factoryId = null
  filterForm.status = null
  filterForm.level = null
  filterForm.isStale = null
  alarmStore.resetFilters()
  alarmStore.currentPage = 1
  alarmStore.fetchAlarms()
}

// ── 分页 ──
function handleTableChange(pagination: { current: number; pageSize: number }) {
  alarmStore.currentPage = pagination.current
  alarmStore.pageSize = pagination.pageSize
  alarmStore.fetchAlarms()
}

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
  } finally {
    actionLoading.value = false
  }
}

// 手动关闭
async function handleClear(alarm: AlarmEvent) {
  try {
    await alarmStore.clear(alarm.id)
  } catch {
    // 错误已在 axios 拦截器处理
  }
}

// 查看详情
function goToDetail(alarm: AlarmEvent) {
  router.push(`/alarms/${alarm.id}`)
}

// ── 列定义 ──
const columns = [
  { title: '级别', dataIndex: 'level', key: 'level', width: 90 },
  { title: '规则名称', dataIndex: 'ruleName', key: 'ruleName', ellipsis: true },
  { title: '设备', dataIndex: 'deviceId', key: 'deviceId', width: 120, ellipsis: true },
  { title: '触发值', key: 'triggerValue', width: 100 },
  { title: '条件', key: 'condition', width: 130 },
  { title: '状态', dataIndex: 'status', key: 'status', width: 90 },
  { title: '触发时间', dataIndex: 'triggeredAt', key: 'triggeredAt', width: 160 },
  { title: '操作', key: 'action', width: 200, fixed: 'right' as const },
]

onMounted(() => {
  alarmStore.fetchAlarms()
})
</script>

<template>
  <div class="alarms-view">
    <!-- 过滤条件栏 -->
    <Card :bordered="false" size="small" class="filter-card">
      <Row :gutter="16" align="middle">
        <Col :xs="24" :sm="12" :md="6" :lg="5">
          <FormItem label="工厂" style="margin-bottom: 0">
            <Select
              v-model:value="filterForm.factoryId"
              placeholder="全部工厂"
              allow-clear
              style="width: 100%"
            >
              <SelectOption v-for="f in factoryOptions" :key="f.value" :value="f.value">
                {{ f.label }}
              </SelectOption>
            </Select>
          </FormItem>
        </Col>
        <Col :xs="24" :sm="12" :md="4" :lg="4">
          <FormItem label="状态" style="margin-bottom: 0">
            <Select
              v-model:value="filterForm.status"
              placeholder="全部状态"
              allow-clear
              style="width: 100%"
            >
              <SelectOption v-for="s in ALARM_STATUS_OPTIONS" :key="s.value" :value="s.value">
                {{ s.label }}
              </SelectOption>
            </Select>
          </FormItem>
        </Col>
        <Col :xs="24" :sm="12" :md="4" :lg="4">
          <FormItem label="级别" style="margin-bottom: 0">
            <Select
              v-model:value="filterForm.level"
              placeholder="全部级别"
              allow-clear
              style="width: 100%"
            >
              <SelectOption v-for="l in ALARM_LEVEL_OPTIONS" :key="l.value" :value="l.value">
                {{ l.label }}
              </SelectOption>
            </Select>
          </FormItem>
        </Col>
        <Col :xs="24" :sm="12" :md="4" :lg="4">
          <FormItem label="离线" style="margin-bottom: 0">
            <Select
              v-model:value="filterForm.isStale"
              placeholder="全部"
              allow-clear
              style="width: 100%"
            >
              <SelectOption :value="false">正常</SelectOption>
              <SelectOption :value="true">离线</SelectOption>
            </Select>
          </FormItem>
        </Col>
        <Col :xs="24" :sm="24" :md="6" :lg="7">
          <Space style="margin-top: 22px">
            <Button type="primary" @click="handleSearch">
              <SearchOutlined /> 查询
            </Button>
            <Button @click="handleReset">
              <UndoOutlined /> 重置
            </Button>
          </Space>
        </Col>
      </Row>
    </Card>

    <!-- 报警列表 -->
    <Card :bordered="false" size="small" class="alarm-table-card">
      <template #title>
        <span>报警事件</span>
        <span style="font-size: 12px; color: #8c8c8c; margin-left: 8px">
          共 {{ alarmStore.totalCount }} 条
        </span>
      </template>

      <Table
        :columns="columns"
        :data-source="alarmStore.alarms"
        :loading="alarmStore.loading"
        :pagination="{
          current: alarmStore.currentPage,
          pageSize: alarmStore.pageSize,
          total: alarmStore.totalCount,
          showSizeChanger: true,
          showQuickJumper: true,
          pageSizeOptions: ['10', '20', '50'],
          showTotal: (total: number) => `共 ${total} 条`,
          size: 'small',
        }"
        :scroll="{ x: 1000 }"
        row-key="id"
        size="small"
        :row-class-name="(record: AlarmEvent) => `alarm-row-level-${record.level}`"
        @change="handleTableChange"
      >
        <template #bodyCell="{ column, record }">
          <!-- 级别 -->
          <template v-if="column.key === 'level'">
            <Tag :color="ALARM_LEVEL_TAG_COLOR[record.level as AlarmLevel] || 'default'">
              {{ record.level }}
            </Tag>
          </template>

          <!-- 设备 + 离线标记 -->
          <template v-else-if="column.key === 'deviceId'">
            <span>{{ record.deviceId }}</span>
            <Tooltip v-if="record.isStale" title="设备离线">
              <WifiOutlined style="color: #ff4d4f; margin-left: 4px" />
            </Tooltip>
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
            <Tag :color="ALARM_STATUS_TAG_COLOR[record.status as AlarmStatus] || 'default'">
              {{ record.status }}
            </Tag>
          </template>

          <!-- 触发时间 -->
          <template v-else-if="column.key === 'triggeredAt'">
            <Tooltip :title="formatDateTime(record.triggeredAt)">
              {{ formatRelativeTime(record.triggeredAt) }}
            </Tooltip>
          </template>

          <!-- 操作 -->
          <template v-else-if="column.key === 'action'">
            <Space size="small">
              <Button type="link" size="small" @click="goToDetail(record)">
                <LinkOutlined /> 详情
              </Button>
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
              <Popconfirm
                v-if="record.status !== 'Cleared'"
                title="确定要手动关闭此报警吗？"
                ok-text="确定"
                cancel-text="取消"
                @confirm="handleClear(record)"
              >
                <Button type="link" size="small" danger>关闭</Button>
              </Popconfirm>
            </Space>
          </template>
        </template>

        <template #emptyText>
          <Empty description="暂无报警数据" />
        </template>
      </Table>
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
.alarms-view {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.filter-card :deep(.ant-card-body) {
  padding: 16px;
}

.alarm-table-card :deep(.ant-card-head) {
  min-height: 36px;
}

.alarm-table-card :deep(.ant-card-head-title) {
  font-size: 14px;
  padding: 8px 0;
}

.condition-text {
  font-family: 'SFMono-Regular', Consolas, monospace;
  font-size: 12px;
  color: #595959;
}

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
