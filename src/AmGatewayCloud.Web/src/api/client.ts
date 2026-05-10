import axios from 'axios'
import { message } from 'ant-design-vue'

const client = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || '',
  timeout: 15000,
  headers: {
    'Content-Type': 'application/json',
  },
})

// 请求拦截器
client.interceptors.request.use(
  (config) => config,
  (error) => Promise.reject(error)
)

// 响应拦截器
client.interceptors.response.use(
  (response) => response,
  (error) => {
    if (!error.response) {
      // 网络错误 / 超时
      message.error('网络连接失败，请检查网络')
    } else {
      const status = error.response.status
      if (status === 401) {
        message.error('登录已过期，请重新登录')
      } else if (status === 403) {
        message.error('没有操作权限')
      } else if (status === 404) {
        message.error('请求的资源不存在')
      } else if (status >= 500) {
        message.error('服务器异常，请稍后重试')
      } else {
        // 400 / 409 等业务错误，优先显示后端返回的消息
        const data = error.response.data
        const msg = typeof data === 'string' ? data : data?.message || data?.title || '请求失败'
        message.error(msg)
      }
    }
    return Promise.reject(error)
  }
)

export default client
