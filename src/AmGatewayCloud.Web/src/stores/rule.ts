import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { AlarmRule, CreateAlarmRuleRequest, UpdateAlarmRuleRequest } from '@/types'
import * as ruleApi from '@/api/rules'

export const useRuleStore = defineStore('rule', () => {
  const rules = ref<AlarmRule[]>([])
  const loading = ref(false)
  const currentRule = ref<AlarmRule | null>(null)

  /** 查询规则列表 */
  async function fetchRules(params?: { factoryId?: string; tag?: string }) {
    loading.value = true
    try {
      const { data } = await ruleApi.getRules(params)
      rules.value = data
    } finally {
      loading.value = false
    }
  }

  /** 查询单条规则 */
  async function fetchRuleById(id: string) {
    loading.value = true
    try {
      const { data } = await ruleApi.getRuleById(id)
      currentRule.value = data
    } finally {
      loading.value = false
    }
  }

  /** 创建规则 */
  async function createRule(data: CreateAlarmRuleRequest) {
    const res = await ruleApi.createRule(data)
    rules.value.push(res.data)
    return res.data
  }

  /** 更新规则 */
  async function updateRule(id: string, data: UpdateAlarmRuleRequest) {
    const res = await ruleApi.updateRule(id, data)
    // 更新列表中的对应项
    const idx = rules.value.findIndex((r) => r.id === id)
    if (idx !== -1) {
      rules.value[idx] = res.data
    }
    if (currentRule.value?.id === id) {
      currentRule.value = res.data
    }
    return res.data
  }

  /** 删除规则 */
  async function deleteRule(id: string) {
    await ruleApi.deleteRule(id)
    rules.value = rules.value.filter((r) => r.id !== id)
  }

  /** 切换规则启停 */
  async function toggleEnabled(id: string, enabled: boolean) {
    const res = await ruleApi.updateRule(id, { enabled })
    const idx = rules.value.findIndex((r) => r.id === id)
    if (idx !== -1) {
      rules.value[idx] = res.data
    }
    if (currentRule.value?.id === id) {
      currentRule.value = res.data
    }
    return res.data
  }

  return {
    rules,
    loading,
    currentRule,
    fetchRules,
    fetchRuleById,
    createRule,
    updateRule,
    deleteRule,
    toggleEnabled,
  }
})
