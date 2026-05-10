<template>
  <div class="rule-edit-view">
    <a-page-header
      :title="isNew ? '新建报警规则' : '编辑报警规则'"
      @back="router.push('/rules')"
    />

    <a-spin :spinning="pageLoading">
      <a-form
        ref="formRef"
        :model="form"
        :rules="rules"
        :label-col="{ span: 6 }"
        :wrapper-col="{ span: 16 }"
        class="rule-form"
      >
        <!-- 规则 ID -->
        <a-form-item label="规则ID" name="id">
          <a-input v-model:value="form.id" :disabled="!isNew" placeholder="如 high-temp" />
        </a-form-item>

        <!-- 规则名称 -->
        <a-form-item label="规则名称" name="name">
          <a-input v-model:value="form.name" placeholder="如 高温报警" />
        </a-form-item>

        <a-divider orientation="left">作用域</a-divider>

        <!-- 工厂 -->
        <a-form-item label="工厂" name="factoryId">
          <a-select v-model:value="form.factoryId" placeholder="全部工厂" allow-clear>
            <a-select-option v-for="f in factoryTree" :key="f.id" :value="f.id">
              {{ f.name }}
            </a-select-option>
          </a-select>
        </a-form-item>

        <!-- 设备 -->
        <a-form-item label="设备" name="deviceId">
          <a-input v-model:value="form.deviceId" placeholder="留空表示全部设备" allow-clear />
        </a-form-item>

        <a-divider orientation="left">触发条件</a-divider>

        <!-- 测点 Tag -->
        <a-form-item label="测点Tag" name="tag">
          <a-input v-model:value="form.tag" placeholder="如 temperature" />
        </a-form-item>

        <!-- 运算符 + 阈值 -->
        <a-form-item label="运算符" name="operator" required>
          <a-input-group compact>
            <a-select v-model:value="form.operator" style="width: 100px" :options="OPERATOR_OPTIONS" />
            <a-form-item name="threshold" :no-style="form.operator !== '=='">
              <a-input-number
                v-if="form.operator !== '=='"
                v-model:value="form.threshold"
                style="width: 160px"
                placeholder="阈值"
              />
            </a-form-item>
          </a-input-group>
        </a-form-item>

        <!-- 字符串阈值 -->
        <a-form-item v-if="form.operator === '=='" label="字符串阈值" name="thresholdString">
          <a-input v-model:value="form.thresholdString" placeholder="字符串匹配值" />
        </a-form-item>

        <!-- 恢复阈值 -->
        <a-form-item label="恢复阈值" name="clearThreshold">
          <a-input-number v-model:value="form.clearThreshold" style="width: 160px" placeholder="Deadband" />
          <span class="form-hint">触发恢复的阈值，留空表示不自动恢复</span>
        </a-form-item>

        <a-divider orientation="left">报警配置</a-divider>

        <!-- 级别 + 冷却时间 -->
        <a-form-item label="报警级别" name="level" required>
          <a-input-group compact>
            <a-select v-model:value="form.level" style="width: 140px" :options="ALARM_LEVEL_OPTIONS" />
            <a-input-number
              v-model:value="form.cooldownMinutes"
              style="width: 160px"
              :min="0"
              addon-after="分钟"
              placeholder="冷却时间"
            />
          </a-input-group>
        </a-form-item>

        <!-- 延迟 -->
        <a-form-item label="延迟(秒)" name="delaySeconds">
          <a-input-number v-model:value="form.delaySeconds" :min="0" style="width: 160px" />
          <span class="form-hint">持续触发多久后才报警</span>
        </a-form-item>

        <!-- 描述 -->
        <a-form-item label="描述" name="description">
          <a-textarea v-model:value="form.description" :rows="3" placeholder="规则描述（可选）" />
        </a-form-item>

        <!-- 启用 -->
        <a-form-item label="启用" name="enabled">
          <a-switch v-model:checked="form.enabled" checked-children="启用" un-checked-children="停用" />
        </a-form-item>

        <!-- 操作按钮 -->
        <a-form-item :wrapper-col="{ offset: 6, span: 16 }">
          <a-space>
            <a-button type="primary" :loading="submitting" @click="handleSubmit">保存</a-button>
            <a-button @click="router.push('/rules')">取消</a-button>
          </a-space>
        </a-form-item>
      </a-form>
    </a-spin>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { message } from 'ant-design-vue'
import type { FormInstance, Rule } from 'ant-design-vue/es/form'
import * as rulesApi from '@/api/rules'
import { useAppStore } from '@/stores/app'
import { OPERATOR_OPTIONS, ALARM_LEVEL_OPTIONS } from '@/utils/constants'
import type { AlarmLevel, CreateAlarmRuleRequest } from '@/types'

const route = useRoute()
const router = useRouter()
const appStore = useAppStore()

const isNew = computed(() => route.params.id === 'new' || !route.params.id)
const ruleId = computed(() => route.params.id as string)

const formRef = ref<FormInstance>()
const pageLoading = ref(false)
const submitting = ref(false)

const factoryTree = computed(() => appStore.factoryTree)

const form = reactive<CreateAlarmRuleRequest & { deviceId: string | null }>({
  id: '',
  name: '',
  factoryId: '',
  deviceId: null,
  tag: '',
  operator: '>',
  threshold: 0,
  clearThreshold: null,
  thresholdString: null,
  level: 'Warning' as AlarmLevel,
  delaySeconds: 0,
  cooldownMinutes: 5,
  description: '',
  enabled: true,
})

// 校验规则
const rules: Record<string, Rule[]> = {
  id: [{ required: true, message: '请输入规则ID' }],
  name: [{ required: true, message: '请输入规则名称' }],
  tag: [{ required: true, message: '请输入测点Tag' }],
  operator: [{ required: true, message: '请选择运算符' }],
  threshold: [{ required: true, message: '请输入阈值' }],
  level: [{ required: true, message: '请选择报警级别' }],
}

// 清除恢复阈值的自定义校验（在 submit 时做）
function validateClearThreshold(): string | null {
  if (form.clearThreshold == null) return null
  const op = form.operator
  const ct = form.clearThreshold
  const th = form.threshold
  if ((op === '>' || op === '>=') && ct >= th) {
    return '恢复阈值必须小于触发阈值'
  }
  if ((op === '<' || op === '<=') && ct <= th) {
    return '恢复阈值必须大于触发阈值'
  }
  return null
}

async function loadRule() {
  if (isNew.value) return
  pageLoading.value = true
  try {
    const { data } = await rulesApi.getRuleById(ruleId.value)
    Object.assign(form, {
      id: data.id,
      name: data.name,
      factoryId: data.factoryId,
      deviceId: data.deviceId,
      tag: data.tag,
      operator: data.operator,
      threshold: data.threshold,
      clearThreshold: data.clearThreshold,
      thresholdString: data.thresholdString,
      level: data.level,
      delaySeconds: data.delaySeconds,
      cooldownMinutes: data.cooldownMinutes,
      description: data.description,
      enabled: data.enabled,
    })
  } catch {
    // 错误已由全局拦截器提示
  } finally {
    pageLoading.value = false
  }
}

async function handleSubmit() {
  try {
    await formRef.value?.validateFields()
  } catch {
    return
  }

  const err = validateClearThreshold()
  if (err) {
    message.error(err)
    return
  }

  submitting.value = true
  try {
    if (isNew.value) {
      await rulesApi.createRule(form)
      message.success('规则创建成功')
    } else {
      await rulesApi.updateRule(ruleId.value, {
        name: form.name,
        factoryId: form.factoryId,
        deviceId: form.deviceId,
        tag: form.tag,
        operator: form.operator,
        threshold: form.threshold,
        clearThreshold: form.clearThreshold,
        thresholdString: form.thresholdString,
        level: form.level,
        delaySeconds: form.delaySeconds,
        cooldownMinutes: form.cooldownMinutes,
        description: form.description,
        enabled: form.enabled,
      })
      message.success('规则更新成功')
    }
    router.push('/rules')
  } catch {
    // 错误已由全局拦截器提示
  } finally {
    submitting.value = false
  }
}

onMounted(loadRule)
</script>

<style scoped>
.rule-edit-view {
  padding: 24px;
  max-width: 800px;
}

.rule-form {
  margin-top: 16px;
}

.form-hint {
  margin-left: 8px;
  color: #999;
  font-size: 12px;
}
</style>
