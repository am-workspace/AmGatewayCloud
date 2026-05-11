import client from './client'

export interface LoginRequest {
  username: string
  password: string
}

export interface LoginResponse {
  token: string
  tenantId: string
  userId: string
  name: string
  role: string
}

/** 登录接口（开发阶段直接用 /api/auth/login，生产环境对接正式 IdP） */
export async function loginApi(data: LoginRequest): Promise<LoginResponse> {
  const res = await client.post<LoginResponse>('/api/auth/login', data)
  return res.data
}

/** 获取开发用 Token（直接指定租户） */
export async function devTokenApi(tenantId: string, userId: string = 'dev-user', role: string = 'Admin'): Promise<LoginResponse> {
  const res = await client.post<LoginResponse>('/api/auth/dev-token', { tenantId, userId, role })
  return res.data
}
