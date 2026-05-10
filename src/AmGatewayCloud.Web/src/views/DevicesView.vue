<template>
  <div class="devices-view">
    <div class="devices-header">
      <h2>设备状态概览</h2>
      <a-button @click="handleRefresh" :loading="deviceStore.loading">
        <template #icon><ReloadOutlined /></template>
        刷新
      </a-button>
    </div>

    <a-spin :spinning="deviceStore.loading">
      <!-- 上半部分：饼图 + 趋势图 -->
      <div class="charts-row">
        <a-card class="chart-card" title="设备总览">
          <VChart
            v-if="hasDeviceData"
            :option="pieOption"
            :autoresize="true"
            style="height: 240px"
          />
          <a-empty v-else description="暂无设备数据" />
        </a-card>

        <a-card class="chart-card" title="报警趋势（24h）">
          <TrendChart :data="alarmStore.trendData" height="240px" />
        </a-card>
      </div>

      <!-- 下半部分：离线设备列表 -->
      <a-card title="离线设备列表" style="margin-top: 16px">
        <a-table
          v-if="deviceStore.staleDevices.length > 0"
          :columns="offlineColumns"
          :data-source="deviceStore.staleDevices"
          :pagination="false"
          row-key="deviceId"
          size="middle"
        >
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'factory'">
              {{ findFactoryName(record.factoryId) }}
            </template>
            <template v-else-if="column.key === 'workshop'">
              {{ findWorkshopName(record.factoryId, record.workshopId) }}
            </template>
            <template v-else-if="column.key === 'lastData'">
              <a-tooltip :title="record.lastDataAt">
                {{ formatTime(record.lastDataAt) }}
              </a-tooltip>
            </template>
            <template v-else-if="column.key === 'duration'">
              <span class="offline-duration">
                {{ deviceStore.formatOfflineDuration(record.lastDataAt) }}
              </span>
            </template>
          </template>
        </a-table>
        <a-empty v-else description="所有设备在线" />
      </a-card>
    </a-spin>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted } from 'vue'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { PieChart } from 'echarts/charts'
import {
  TitleComponent,
  TooltipComponent,
  LegendComponent,
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { ReloadOutlined } from '@ant-design/icons-vue'
import { useDeviceStore } from '@/stores/device'
import { useAlarmStore } from '@/stores/alarm'
import { useAppStore } from '@/stores/app'
import TrendChart from '@/components/TrendChart.vue'

use([PieChart, TitleComponent, TooltipComponent, LegendComponent, CanvasRenderer])

const deviceStore = useDeviceStore()
const alarmStore = useAlarmStore()
const appStore = useAppStore()

const hasDeviceData = computed(() => deviceStore.onlineCount + deviceStore.offlineCount > 0)

// 饼图配置
const pieOption = computed(() => ({
  tooltip: {
    trigger: 'item',
    formatter: '{b}: {c} ({d}%)',
  },
  legend: {
    bottom: 0,
    textStyle: { fontSize: 12 },
  },
  series: [
    {
      type: 'pie',
      radius: ['40%', '70%'],
      center: ['50%', '45%'],
      avoidLabelOverlap: false,
      itemStyle: {
        borderRadius: 6,
        borderColor: '#fff',
        borderWidth: 2,
      },
      label: {
        show: true,
        formatter: '{b}\n{c}',
        fontSize: 12,
      },
      data: [
        { value: deviceStore.onlineCount, name: '在线', itemStyle: { color: '#52c41a' } },
        { value: deviceStore.offlineCount, name: '离线', itemStyle: { color: '#ff4d4f' } },
      ].filter((d) => d.value > 0),
    },
  ],
}))

// 离线设备表格列
const offlineColumns = [
  { title: '设备', dataIndex: 'deviceId', key: 'deviceId', width: 140 },
  { title: '工厂', key: 'factory', width: 120 },
  { title: '车间', key: 'workshop', width: 120 },
  { title: '最后数据', key: 'lastData', width: 160 },
  { title: '离线时长', key: 'duration', width: 120 },
]

// 从工厂树中查找名称
function findFactoryName(factoryId: string): string {
  const f = appStore.factoryTree.find((n) => n.id === factoryId)
  return f?.name ?? factoryId
}

function findWorkshopName(factoryId: string, workshopId: string): string {
  const f = appStore.factoryTree.find((n) => n.id === factoryId)
  const w = f?.workshops.find((ws) => ws.id === workshopId)
  return w?.name ?? workshopId
}

function formatTime(val: string | null): string {
  if (!val) return '—'
  const d = new Date(val)
  return `${d.getHours().toString().padStart(2, '0')}:${d.getMinutes().toString().padStart(2, '0')}`
}

async function handleRefresh() {
  await Promise.allSettled([
    deviceStore.refreshDeviceStatus(),
    alarmStore.fetchTrend(),
  ])
}

// 30 秒自动刷新
let timer: ReturnType<typeof setInterval> | null = null

onMounted(async () => {
  await handleRefresh()
  timer = setInterval(handleRefresh, 30000)
})

onUnmounted(() => {
  if (timer) {
    clearInterval(timer)
    timer = null
  }
})
</script>

<style scoped>
.devices-view {
  padding: 24px;
}

.devices-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
}

.devices-header h2 {
  margin: 0;
  font-size: 20px;
  font-weight: 600;
}

.charts-row {
  display: grid;
  grid-template-columns: 1fr 2fr;
  gap: 16px;
}

@media (max-width: 768px) {
  .charts-row {
    grid-template-columns: 1fr;
  }
}

.chart-card {
  min-height: 300px;
}

.offline-duration {
  color: #ff4d4f;
  font-weight: 500;
}
</style>
