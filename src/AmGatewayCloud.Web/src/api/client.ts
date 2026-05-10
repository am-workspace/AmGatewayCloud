import axios from 'axios'

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
    // TODO: 全局错误处理（网络错误、401、500 等）
    console.error('API Error:', error)
    return Promise.reject(error)
  }
)

export default client
