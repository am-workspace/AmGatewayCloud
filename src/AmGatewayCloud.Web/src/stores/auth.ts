import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { loginApi, devTokenApi, type LoginRequest, type LoginResponse } from '@/api/auth'

const TOKEN_KEY = 'amgateway_token'
const USER_KEY = 'amgateway_user'

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string | null>(localStorage.getItem(TOKEN_KEY))
  const user = ref<LoginResponse | null>(loadUser())

  const isLoggedIn = computed(() => !!token.value)
  const tenantId = computed(() => user.value?.tenantId ?? 'default')
  const userName = computed(() => user.value?.name ?? '')
  const userRole = computed(() => user.value?.role ?? '')

  function loadUser(): LoginResponse | null {
    try {
      const raw = localStorage.getItem(USER_KEY)
      return raw ? JSON.parse(raw) : null
    } catch {
      return null
    }
  }

  function saveSession(data: LoginResponse) {
    token.value = data.token
    user.value = data
    localStorage.setItem(TOKEN_KEY, data.token)
    localStorage.setItem(USER_KEY, JSON.stringify(data))
  }

  async function login(credentials: LoginRequest) {
    const data = await loginApi(credentials)
    saveSession(data)
  }

  async function loginWithDevToken(tenantId: string, userId: string = 'dev-user', role: string = 'Admin') {
    const data = await devTokenApi(tenantId, userId, role)
    saveSession(data)
  }

  function logout() {
    token.value = null
    user.value = null
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(USER_KEY)
  }

  return {
    token,
    user,
    isLoggedIn,
    tenantId,
    userName,
    userRole,
    login,
    loginWithDevToken,
    logout,
  }
})
