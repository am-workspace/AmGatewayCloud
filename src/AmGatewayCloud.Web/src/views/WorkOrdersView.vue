<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import {
  Table, Tag, Button, Space, Select, SelectOption, Input, Card,
  Row, Col, Statistic, Modal, Form, FormItem, InputTextArea, message,
} from 'ant-design-vue'
import {
  CheckOutlined, UserOutlined, ClockCircleOutlined,
  ExclamationCircleOutlined, CarryOutOutlined,
} from '@ant-design/icons-vue'
import { useWorkOrderStore } from '@/stores/workorder'
import { useAppStore } from '@/stores/app'
import type { WorkOrder, WorkOrderStatus } from '@/types'

const store = useWorkOrderStore()
const appStore = useAppStore()

// 操作弹窗
const actionModalVisible = ref(false)
const actionType = ref<'assign' | 'complete'>('assign')
const selectedWorkOrder = ref<WorkOrder | null>(null)
const actionForm = ref({ assignee: '', completedBy: '', completionNote: '' })
const actionLoading = ref(false)

// 状态过滤选项
const statusOptions = [
  { label: '全部', value: '' },
  { label: '待分配', value: 'Pending' },
  { label: '处理中', value: 'InProgress' },
  { label: '已完成', value: 'Completed' },
]

// 工厂选项
const factoryOptions = computed(() =>
  appStore.factoryTree.map((f) => ({ label: f.name, value: f.id }))
)

// 状态颜色映射
function getStatusColor(status: WorkOrderStatus) {
  switch (status) {
    case 'Pending': return 'orange'
    case 'InProgress': return 'blue'
    case 'Completed': return 'green'
    default: return 'default'
  }
}

function getStatusLabel(status: WorkOrderStatus) {
  switch (status) {
    case 'Pending': return '待分配'
    case 'InProgress': return '处理中'
    case 'Completed': return '已完成'
    default: return status
  }
}

function getLevelColor(level: string) {
  switch (level) {
    case 'Critical': return 'red'
    case 'Fatal': return 'magenta'
    case 'Warning': return 'orange'
    case 'Info': return 'blue'
    default: return 'default'
  }
}

// 表格列
const columns = [
  { title: '标题', dataIndex: 'title', key: 'title', ellipsis: true },
  { title: '设备', dataIndex: 'deviceId', key: 'deviceId', width: 120 },
  { title: '级别', dataIndex: 'level', key: 'level', width: 90 },
  { title: '状态', dataIndex: 'status', key: 'status', width: 100 },
  { title: '负责人', dataIndex: 'assignee', key: 'assignee', width: 100 },
  { title: '创建时间', dataIndex: 'createdAt', key: 'createdAt', width: 170 },
  { title: '操作', key: 'action', width: 160, fixed: 'right' as const },
]

function formatTime(ts: string | null) {
  if (!ts) return '-'
  return new Date(ts).toLocaleString('zh-CN')
}

// 分页
function onPageChange(page: number, pageSize: number) {
  store.currentPage = page
  store.pageSize = pageSize
  store.fetchWorkOrders()
}

// 过滤
function onStatusChange(val: string) {
  store.filters.status = (val || null) as WorkOrderStatus | null
  store.currentPage = 1
  store.fetchWorkOrders()
}

function onFactoryChange(val: string | undefined) {
  store.filters.factoryId = val ?? null
  store.currentPage = 1
  store.fetchWorkOrders()
}

// 操作
function openAssignModal(record: WorkOrder) {
  actionType.value = 'assign'
  selectedWorkOrder.value = record
  actionForm.value = { assignee: '', completedBy: '', completionNote: '' }
  actionModalVisible.value = true
}

function openCompleteModal(record: WorkOrder) {
  actionType.value = 'complete'
  selectedWorkOrder.value = record
  actionForm.value = { assignee: '', completedBy: '', completionNote: '' }
  actionModalVisible.value = true
}

async function handleAction() {
  if (!selectedWorkOrder.value) return
  actionLoading.value = true
  try {
    if (actionType.value === 'assign') {
      if (!actionForm.value.assignee.trim()) {
        message.warning('请输入负责人')
        return
      }
      await store.assign(selectedWorkOrder.value.id, actionForm.value.assignee.trim())
      message.success('工单已分配')
    } else {
      if (!actionForm.value.completedBy.trim()) {
        message.warning('请输入完成人')
        return
      }
      await store.complete(
        selectedWorkOrder.value.id,
        actionForm.value.completedBy.trim(),
        actionForm.value.completionNote?.trim() || undefined,
      )
      message.success('工单已完成')
    }
    actionModalVisible.value = false
  } catch {
    // 错误已由 axios 拦截器处理
  } finally {
    actionLoading.value = false
  }
}

// 初始化
onMounted(async () => {
  store.setFactoryId(appStore.selectedFactoryId)
  await Promise.all([store.fetchWorkOrders(), store.fetchSummary()])
})
</script>

<template>
  <div class="workorders-view">
    <!-- 统计卡片 -->
    <Row :gutter="16" style="margin-bottom: 16px">
      <Col :span="8">
        <Card>
          <Statistic title="待分配" :value="store.summary?.pending ?? 0" :value-style="{ color: '#fa8c16' }">
            <template #prefix><ExclamationCircleOutlined /></template>
          </Statistic>
        </Card>
      </Col>
      <Col :span="8">
        <Card>
          <Statistic title="处理中" :value="store.summary?.inProgress ?? 0" :value-style="{ color: '#1890ff' }">
            <template #prefix><ClockCircleOutlined /></template>
          </Statistic>
        </Card>
      </Col>
      <Col :span="8">
        <Card>
          <Statistic title="已完成" :value="store.summary?.completed ?? 0" :value-style="{ color: '#52c41a' }">
            <template #prefix><CarryOutOutlined /></template>
          </Statistic>
        </Card>
      </Col>
    </Row>

    <!-- 过滤栏 -->
    <div class="filter-bar">
      <Space>
        <Select
          :value="store.filters.factoryId ?? undefined"
          placeholder="选择工厂"
          style="width: 180px"
          allow-clear
          :options="factoryOptions"
          @change="onFactoryChange"
        />
        <Select
          :value="store.filters.status ?? ''"
          style="width: 120px"
          :options="statusOptions"
          @change="onStatusChange"
        />
      </Space>
    </div>

    <!-- 工单表格 -->
    <Table
      :columns="columns"
      :data-source="store.workOrders"
      :loading="store.loading"
      :pagination="{
        current: store.currentPage,
        pageSize: store.pageSize,
        total: store.totalCount,
        showSizeChanger: true,
        showTotal: (total: number) => `共 ${total} 条`,
      }"
      :scroll="{ x: 900 }"
      row-key="id"
      size="middle"
      @change="(pag: any) => onPageChange(pag.current, pag.pageSize)"
    >
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'level'">
          <Tag :color="getLevelColor(record.level)">{{ record.level }}</Tag>
        </template>
        <template v-else-if="column.key === 'status'">
          <Tag :color="getStatusColor(record.status)">{{ getStatusLabel(record.status) }}</Tag>
        </template>
        <template v-else-if="column.key === 'assignee'">
          <span v-if="record.assignee">
            <UserOutlined /> {{ record.assignee }}
          </span>
          <span v-else style="color: #999">未分配</span>
        </template>
        <template v-else-if="column.key === 'createdAt'">
          {{ formatTime(record.createdAt) }}
        </template>
        <template v-else-if="column.key === 'action'">
          <Space>
            <Button
              v-if="record.status === 'Pending'"
              type="primary"
              size="small"
              @click="openAssignModal(record)"
            >
              分配
            </Button>
            <Button
              v-if="record.status === 'InProgress'"
              type="primary"
              size="small"
              ghost
              @click="openCompleteModal(record)"
            >
              完成
            </Button>
            <span v-if="record.status === 'Completed'" style="color: #52c41a">
              <CheckOutlined /> 已完成
            </span>
          </Space>
        </template>
      </template>
    </Table>

    <!-- 操作弹窗 -->
    <Modal
      v-model:open="actionModalVisible"
      :title="actionType === 'assign' ? '分配工单' : '完成工单'"
      :confirm-loading="actionLoading"
      @ok="handleAction"
    >
      <Form layout="vertical">
        <FormItem v-if="actionType === 'assign'" label="负责人" required>
          <Input v-model:value="actionForm.assignee" placeholder="请输入负责人姓名">
            <template #prefix><UserOutlined /></template>
          </Input>
        </FormItem>
        <template v-if="actionType === 'complete'">
          <FormItem label="完成人" required>
            <Input v-model:value="actionForm.completedBy" placeholder="请输入完成人姓名">
              <template #prefix><UserOutlined /></template>
            </Input>
          </FormItem>
          <FormItem label="完成备注">
            <InputTextArea v-model:value="actionForm.completionNote" :rows="3" placeholder="请输入完成备注（可选）" />
          </FormItem>
        </template>
      </Form>
      <div v-if="selectedWorkOrder" style="margin-top: 8px; padding: 12px; background: #f5f5f5; border-radius: 6px">
        <p style="margin: 0 0 4px; font-weight: 500">{{ selectedWorkOrder.title }}</p>
        <p v-if="selectedWorkOrder.description" style="margin: 0; color: #666; font-size: 12px; white-space: pre-line">
          {{ selectedWorkOrder.description }}
        </p>
      </div>
    </Modal>
  </div>
</template>

<style scoped>
.workorders-view {
  padding: 0;
}

.filter-bar {
  margin-bottom: 16px;
  display: flex;
  justify-content: space-between;
  align-items: center;
}
</style>
