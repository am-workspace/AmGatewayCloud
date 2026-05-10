<template>
  <div class="rules-view">
    <div class="rules-header">
      <h2>报警规则</h2>
      <a-button type="primary" @click="router.push('/rules/new')">
        <template #icon><PlusOutlined /></template>
        新建规则
      </a-button>
    </div>

    <!-- 过滤栏 -->
    <div class="rules-filter">
      <a-select
        v-model:value="filterFactoryId"
        placeholder="全部工厂"
        allow-clear
        style="width: 180px"
        @change="handleFilterChange"
      >
        <a-select-option v-for="f in appStore.factoryTree" :key="f.id" :value="f.id">
          {{ f.name }}
        </a-select-option>
      </a-select>
      <a-input
        v-model:value="filterTag"
        placeholder="按测点Tag过滤"
        allow-clear
        style="width: 180px"
        @change="handleFilterChange"
      />
    </div>

    <a-table
      :columns="columns"
      :data-source="rules"
      :loading="loading"
      :pagination="false"
      row-key="id"
      size="middle"
      :row-class-name="getRowClassName"
    >
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'level'">
          <a-tag :color="ALARM_LEVEL_TAG_COLOR[record.level]">{{ levelLabel(record.level) }}</a-tag>
        </template>
        <template v-else-if="column.key === 'enabled'">
          <a-switch
            :checked="record.enabled"
            @change="(val: boolean) => handleToggle(record, val)"
            :loading="toggleLoading === record.id"
            checked-children="启用"
            un-checked-children="停用"
          />
        </template>
        <template v-else-if="column.key === 'threshold'">
          <span class="threshold-cell">
            <span class="operator">{{ record.operator }}</span>
            <span>{{ record.thresholdString ?? record.threshold }}</span>
          </span>
        </template>
        <template v-else-if="column.key === 'factory'">
          {{ record.deviceId ? record.deviceId : '全部设备' }}
        </template>
        <template v-else-if="column.key === 'actions'">
          <a-space>
            <a-button type="link" size="small" @click="router.push(`/rules/${record.id}`)">
              编辑
            </a-button>
            <a-popconfirm
              title="确定删除此规则？"
              ok-text="确定"
              cancel-text="取消"
              @confirm="handleDelete(record)"
            >
              <a-button type="link" size="small" danger>删除</a-button>
            </a-popconfirm>
          </a-space>
        </template>
      </template>
    </a-table>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { message } from 'ant-design-vue'
import { PlusOutlined } from '@ant-design/icons-vue'
import * as rulesApi from '@/api/rules'
import { useAppStore } from '@/stores/app'
import { ALARM_LEVEL_TAG_COLOR } from '@/utils/constants'
import type { AlarmRule, AlarmLevel } from '@/types'

const router = useRouter()
const appStore = useAppStore()

const rules = ref<AlarmRule[]>([])
const loading = ref(false)
const toggleLoading = ref<string | null>(null)

// 过滤条件
const filterFactoryId = ref<string | undefined>(undefined)
const filterTag = ref<string | undefined>(undefined)

const columns = [
  { title: '规则ID', dataIndex: 'id', key: 'id', width: 160, ellipsis: true },
  { title: '名称', dataIndex: 'name', key: 'name', width: 160 },
  { title: '测点', dataIndex: 'tag', key: 'tag', width: 120 },
  { title: '条件', dataIndex: 'threshold', key: 'threshold', width: 120 },
  { title: '级别', dataIndex: 'level', key: 'level', width: 90 },
  { title: '冷却(分钟)', dataIndex: 'cooldownMinutes', key: 'cooldownMinutes', width: 100 },
  { title: '状态', dataIndex: 'enabled', key: 'enabled', width: 100 },
  { title: '操作', key: 'actions', width: 130, fixed: 'right' as const },
]

function levelLabel(level: AlarmLevel) {
  const map: Record<AlarmLevel, string> = { Fatal: '致命', Critical: '严重', Warning: '警告', Info: '信息' }
  return map[level] ?? level
}

function getRowClassName(record: AlarmRule) {
  if (!record.enabled) return 'rule-row-disabled'
  return ''
}

async function fetchRules() {
  loading.value = true
  try {
    const params: { factoryId?: string; tag?: string } = {}
    if (filterFactoryId.value) params.factoryId = filterFactoryId.value
    if (filterTag.value) params.tag = filterTag.value
    const { data } = await rulesApi.getRules(params)
    rules.value = data
  } catch {
    // 错误已由全局拦截器提示
  } finally {
    loading.value = false
  }
}

function handleFilterChange() {
  fetchRules()
}

async function handleToggle(record: AlarmRule, enabled: boolean) {
  toggleLoading.value = record.id
  try {
    const { data } = await rulesApi.updateRule(record.id, { enabled })
    Object.assign(record, data)
    message.success(enabled ? '规则已启用' : '规则已停用')
  } catch {
    // 错误已由全局拦截器提示
  } finally {
    toggleLoading.value = null
  }
}

async function handleDelete(record: AlarmRule) {
  try {
    await rulesApi.deleteRule(record.id)
    rules.value = rules.value.filter((r) => r.id !== record.id)
    message.success('规则已删除')
  } catch {
    // 错误已由全局拦截器提示
  }
}

onMounted(fetchRules)
</script>

<style scoped>
.rules-view {
  padding: 24px;
}

.rules-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
}

.rules-header h2 {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
}

.rules-filter {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
}

.threshold-cell {
  font-family: 'Consolas', 'Monaco', monospace;
}

.threshold-cell .operator {
  margin-right: 4px;
  color: #999;
}

::deep(.rule-row-disabled) {
  opacity: 0.55;
}
</style>
