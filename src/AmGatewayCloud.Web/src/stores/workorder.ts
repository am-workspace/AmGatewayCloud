import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { WorkOrder, WorkOrderSummary, WorkOrderStatus } from '@/types'
import * as workorderApi from '@/api/workorders'

export const useWorkOrderStore = defineStore('workorder', () => {
  const workOrders = ref<WorkOrder[]>([])
  const totalCount = ref(0)
  const currentPage = ref(1)
  const pageSize = ref(20)
  const loading = ref(false)
  const filters = ref({
    factoryId: null as string | null,
    status: null as WorkOrderStatus | null,
    assignee: null as string | null,
  })

  const summary = ref<WorkOrderSummary | null>(null)

  let _factoryId: string | null = null

  function setFactoryId(id: string | null) {
    _factoryId = id
  }

  /** 查询工单列表 */
  async function fetchWorkOrders() {
    loading.value = true
    try {
      const { data } = await workorderApi.getWorkOrders({
        factoryId: filters.value.factoryId ?? undefined,
        status: filters.value.status ?? undefined,
        assignee: filters.value.assignee ?? undefined,
        page: currentPage.value,
        pageSize: pageSize.value,
      })
      workOrders.value = data.items
      totalCount.value = data.totalCount
    } finally {
      loading.value = false
    }
  }

  /** 查询汇总 */
  async function fetchSummary() {
    try {
      const { data } = await workorderApi.getWorkOrderSummary(_factoryId ?? undefined)
      summary.value = data
    } catch {
      // 非阻塞
    }
  }

  /** 分配工单 */
  async function assign(id: string, assignee: string) {
    const { data } = await workorderApi.assignWorkOrder(id, { assignee })
    updateInList(data)
    fetchSummary()
    return data
  }

  /** 完成工单 */
  async function complete(id: string, completedBy: string, completionNote?: string) {
    const { data } = await workorderApi.completeWorkOrder(id, { completedBy, completionNote })
    updateInList(data)
    fetchSummary()
    return data
  }

  function updateInList(updated: WorkOrder) {
    const idx = workOrders.value.findIndex((w) => w.id === updated.id)
    if (idx !== -1) workOrders.value[idx] = updated
  }

  function resetFilters() {
    filters.value = { factoryId: null, status: null, assignee: null }
  }

  return {
    workOrders, totalCount, currentPage, pageSize, loading, filters, summary,
    setFactoryId, fetchWorkOrders, fetchSummary, assign, complete, resetFilters,
  }
})
