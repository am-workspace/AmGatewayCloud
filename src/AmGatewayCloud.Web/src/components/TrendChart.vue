<script setup lang="ts">
import { computed } from 'vue'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { LineChart } from 'echarts/charts'
import {
  TitleComponent,
  TooltipComponent,
  GridComponent,
  LegendComponent,
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import type { AlarmTrendPoint } from '@/types'

use([LineChart, TitleComponent, TooltipComponent, GridComponent, LegendComponent, CanvasRenderer])

const props = defineProps<{
  data: AlarmTrendPoint[]
  height?: string
}>()

const chartOption = computed(() => {
  const hours = props.data.map((d) => {
    // "2026-05-10T10:00:00" → "10:00"
    const parts = d.hour.split('T')
    return parts.length > 1 ? parts[1].substring(0, 5) : d.hour
  })

  return {
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'cross' },
    },
    legend: {
      data: ['总计', '严重', '警告', '信息'],
      bottom: 0,
      textStyle: { fontSize: 12 },
    },
    grid: {
      left: 40,
      right: 16,
      top: 16,
      bottom: 36,
    },
    xAxis: {
      type: 'category',
      data: hours,
      boundaryGap: false,
      axisLabel: { fontSize: 11 },
    },
    yAxis: {
      type: 'value',
      minInterval: 1,
      axisLabel: { fontSize: 11 },
    },
    series: [
      {
        name: '总计',
        type: 'line',
        data: props.data.map((d) => d.total),
        smooth: true,
        lineStyle: { width: 2 },
        itemStyle: { color: '#595959' },
        areaStyle: { color: 'rgba(89,89,89,0.06)' },
      },
      {
        name: '严重',
        type: 'line',
        data: props.data.map((d) => d.critical),
        smooth: true,
        lineStyle: { width: 2 },
        itemStyle: { color: '#ff4d4f' },
      },
      {
        name: '警告',
        type: 'line',
        data: props.data.map((d) => d.warning),
        smooth: true,
        lineStyle: { width: 2 },
        itemStyle: { color: '#faad14' },
      },
      {
        name: '信息',
        type: 'line',
        data: props.data.map((d) => d.info),
        smooth: true,
        lineStyle: { width: 1 },
        itemStyle: { color: '#1890ff' },
      },
    ],
  }
})
</script>

<template>
  <VChart
    v-if="data.length > 0"
    :option="chartOption"
    :autoresize="true"
    :style="{ height: height || '240px' }"
  />
  <div v-else class="trend-empty">暂无趋势数据</div>
</template>

<style scoped>
.trend-empty {
  height: 240px;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #bfbfbf;
  font-size: 14px;
}
</style>
