import { ref, readonly } from 'vue'
import * as signalR from '@microsoft/signalr'
import type { AlarmEventMessage } from '@/types'
import { useAlarmStore } from '@/stores/alarm'
import { useAppStore } from '@/stores/app'

/** SignalR 连接状态 */
export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting'

const connectionState = ref<ConnectionState>('disconnected')
let connection: signalR.HubConnection | null = null

/** 建立 SignalR 连接 */
export async function startSignalR() {
  if (connection?.state === signalR.HubConnectionState.Connected) return

  const baseUrl = import.meta.env.VITE_API_BASE_URL || ''
  const token = localStorage.getItem('amgateway_token')

  connection = new signalR.HubConnectionBuilder()
    .withUrl(`${baseUrl}/hubs/alarm`, {
      accessTokenFactory: () => token ?? '',
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build()

  // 监听报警事件
  connection.on('AlarmReceived', (msg: AlarmEventMessage) => {
    const alarmStore = useAlarmStore()
    const appStore = useAppStore()

    // 如果有工厂过滤，只处理当前工厂的推送
    if (appStore.selectedFactoryId && msg.factoryId !== appStore.selectedFactoryId) {
      return
    }

    alarmStore.handleSignalREvent(msg)
  })

  // 连接状态变化
  connection.onreconnecting(() => {
    connectionState.value = 'reconnecting'
  })

  connection.onreconnected(async () => {
    connectionState.value = 'connected'
    // 重连后重新加入工厂分组
    const appStore = useAppStore()
    if (appStore.selectedFactoryId) {
      await joinFactory(appStore.selectedFactoryId)
    }
  })

  connection.onclose(() => {
    connectionState.value = 'disconnected'
  })

  try {
    connectionState.value = 'connecting'
    await connection.start()
    connectionState.value = 'connected'

    // 连接成功后加入当前工厂分组
    const appStore = useAppStore()
    if (appStore.selectedFactoryId) {
      await joinFactory(appStore.selectedFactoryId)
    }
  } catch (err) {
    connectionState.value = 'disconnected'
    console.error('SignalR connection failed:', err)
  }
}

/** 停止 SignalR 连接 */
export async function stopSignalR() {
  if (connection) {
    await connection.stop()
    connection = null
    connectionState.value = 'disconnected'
  }
}

/** 加入工厂分组 */
export async function joinFactory(factoryId: string) {
  if (connection?.state === signalR.HubConnectionState.Connected) {
    await connection.invoke('JoinFactory', factoryId)
  }
}

/** 离开工厂分组 */
export async function leaveFactory(factoryId: string) {
  if (connection?.state === signalR.HubConnectionState.Connected) {
    await connection.invoke('LeaveFactory', factoryId)
  }
}

/** composable: 在组件中使用 SignalR 状态 */
export function useAlarmSignalR() {
  return {
    connectionState: readonly(connectionState),
    start: startSignalR,
    stop: stopSignalR,
    joinFactory,
    leaveFactory,
  }
}
