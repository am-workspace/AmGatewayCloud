<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { Card, Form, FormItem, Input, InputPassword, Button, Select, SelectOption, Divider, Alert, Space } from 'ant-design-vue'
import { UserOutlined, LockOutlined, SafetyOutlined } from '@ant-design/icons-vue'
import { useAuthStore } from '@/stores/auth'

const router = useRouter()
const authStore = useAuthStore()

const loading = ref(false)
const error = ref('')

// 标准登录
const loginForm = ref({ username: '', password: '' })

// 开发模式：选择租户直接获取 Token
const devTenantId = ref('default')
const devUserId = ref('dev-user')

async function onLogin() {
  error.value = ''
  loading.value = true
  try {
    await authStore.login(loginForm.value)
    router.push('/dashboard')
  } catch (e: any) {
    error.value = e?.response?.data?.message || e?.message || '登录失败'
  } finally {
    loading.value = false
  }
}

async function onDevLogin() {
  error.value = ''
  loading.value = true
  try {
    await authStore.loginWithDevToken(devTenantId.value, devUserId.value)
    router.push('/dashboard')
  } catch (e: any) {
    error.value = e?.response?.data?.message || e?.message || '获取开发Token失败'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="login-page">
    <Card class="login-card" :bordered="false">
      <div class="login-header">
        <SafetyOutlined class="login-icon" />
        <h1>AmGateway</h1>
        <p>工业物联网报警平台</p>
      </div>

      <Alert v-if="error" :message="error" type="error" show-icon closable @close="error = ''" />

      <!-- 标准登录 -->
      <Form layout="vertical" @finish="onLogin">
        <FormItem label="用户名" name="username" :rules="[{ required: true, message: '请输入用户名' }]">
          <Input v-model:value="loginForm.username" placeholder="请输入用户名" size="large">
            <template #prefix><UserOutlined /></template>
          </Input>
        </FormItem>
        <FormItem label="密码" name="password" :rules="[{ required: true, message: '请输入密码' }]">
          <InputPassword v-model:value="loginForm.password" placeholder="请输入密码" size="large">
            <template #prefix><LockOutlined /></template>
          </InputPassword>
        </FormItem>
        <FormItem>
          <Button type="primary" html-type="submit" :loading="loading" block size="large">
            登录
          </Button>
        </FormItem>
      </Form>

      <Divider>开发模式</Divider>

      <!-- 开发模式：选择租户直接登录 -->
      <Form layout="vertical" @finish="onDevLogin">
        <FormItem label="租户 ID">
          <Select v-model:value="devTenantId" size="large">
            <SelectOption value="default">默认租户 (default)</SelectOption>
            <SelectOption value="tenant-a">租户 A</SelectOption>
            <SelectOption value="tenant-b">租户 B</SelectOption>
          </Select>
        </FormItem>
        <FormItem label="用户 ID">
          <Input v-model:value="devUserId" placeholder="dev-user" size="large" />
        </FormItem>
        <FormItem>
          <Button type="dashed" html-type="submit" :loading="loading" block size="large">
            开发模式登录（跳过密码）
          </Button>
        </FormItem>
      </Form>
    </Card>
  </div>
</template>

<style scoped>
.login-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: linear-gradient(135deg, #001529 0%, #003a70 100%);
}

.login-card {
  width: 420px;
  border-radius: 12px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.2);
}

.login-header {
  text-align: center;
  margin-bottom: 24px;
}

.login-icon {
  font-size: 36px;
  color: #1890ff;
}

.login-header h1 {
  margin: 8px 0 4px;
  font-size: 24px;
  color: #001529;
}

.login-header p {
  color: #666;
  margin: 0;
}
</style>
