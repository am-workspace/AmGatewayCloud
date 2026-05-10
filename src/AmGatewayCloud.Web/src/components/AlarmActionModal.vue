<script setup lang="ts">
import { ref, reactive, computed } from 'vue'
import { Modal, Form, Input, FormItem } from 'ant-design-vue'

const props = defineProps<{
  visible: boolean
  action: 'acknowledge' | 'suppress'
  alarmName: string
  loading: boolean
}>()

const emit = defineEmits<{
  (e: 'update:visible', val: boolean): void
  (e: 'confirm', payload: { by: string; reason?: string }): void
  (e: 'cancel'): void
}>()

const formState = reactive({
  by: import.meta.env.VITE_OPERATOR_NAME || '',
  reason: '',
})

const rules = {
  by: [{ required: true, message: '请输入操作人' }],
}

const formRef = ref()

const title = computed(() =>
  props.action === 'acknowledge' ? '确认报警' : '抑制报警'
)

const okText = computed(() =>
  props.action === 'acknowledge' ? '确认' : '抑制'
)

function handleOk() {
  formRef.value?.validate().then(() => {
    emit('confirm', {
      by: formState.by,
      reason: props.action === 'suppress' ? formState.reason : undefined,
    })
  })
}

function handleCancel() {
  formRef.value?.resetFields()
  formState.by = import.meta.env.VITE_OPERATOR_NAME || ''
  formState.reason = ''
  emit('update:visible', false)
  emit('cancel')
}

/** 外部调用：重置表单 */
function resetForm() {
  formRef.value?.resetFields()
  formState.by = import.meta.env.VITE_OPERATOR_NAME || ''
  formState.reason = ''
}

defineExpose({ resetForm })
</script>

<template>
  <Modal
    :open="visible"
    :title="title"
    :confirm-loading="loading"
    :ok-text="okText"
    cancel-text="取消"
    @ok="handleOk"
    @cancel="handleCancel"
  >
    <p style="margin-bottom: 16px; color: #595959">
      报警: <strong>{{ alarmName }}</strong>
    </p>
    <Form ref="formRef" :model="formState" :rules="rules" layout="vertical">
      <FormItem label="操作人" name="by">
        <Input v-model:value="formState.by" placeholder="请输入操作人姓名" />
      </FormItem>
      <FormItem v-if="action === 'suppress'" label="抑制原因" name="reason">
        <Input.TextArea
          v-model:value="formState.reason"
          placeholder="请输入抑制原因"
          :rows="3"
        />
      </FormItem>
    </Form>
  </Modal>
</template>
