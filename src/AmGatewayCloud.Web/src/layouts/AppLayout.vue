<script setup lang="ts">
import { computed, onMounted, onUnmounted } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import {
  Layout,
  LayoutSider,
  LayoutHeader,
  LayoutContent,
  Menu,
  MenuItem,
  Select,
  SelectOption,
  Button,
  Badge,
  Tree,
  Alert,
} from 'ant-design-vue'
import {
  DashboardOutlined,
  AlertOutlined,
  ToolOutlined,
  DesktopOutlined,
  SoundOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
} from '@ant-design/icons-vue'
import { useAppStore } from '@/stores/app'
import { useAlarmStore } from '@/stores/alarm'
import { useAlarmSignalR, startSignalR, stopSignalR } from '@/composables/useAlarmSignalR'
import { initSoundOnInteraction } from '@/composables/useAlarmSound'
import AlarmNotification from '@/components/AlarmNotification.vue'

const router = useRouter()
const route = useRoute()
const appStore = useAppStore()
const alarmStore = useAlarmStore()
const { connectionState } = useAlarmSignalR()

const collapsed = computed({
  get: () => appStore.sidebarCollapsed,
  set: (v) => { appStore.sidebarCollapsed = v },
})

// 菜单选中项
const selectedKeys = computed(() => {
  const path = route.path
  if (path.startsWith('/alarms')) return ['alarms']
  if (path.startsWith('/rules')) return ['rules']
  if (path.startsWith('/devices')) return ['devices']
  return ['dashboard']
})

// 工厂选择器选项
const factoryOptions = computed(() =>
  appStore.factoryTree.map((f) => ({ label: f.name, value: f.id }))
)

// 侧边栏树数据
const treeData = computed(() =>
  appStore.factoryTree.map((f) => ({
    key: f.id,
    title: f.name,
    children: f.workshops.map((w) => ({
      key: w.id,
      title: w.name,
    })),
  }))
)

// SignalR 连接状态提示
const showReconnectBanner = computed(() => connectionState.value === 'reconnecting')

function onMenuClick({ key }: { key: string }) {
  const map: Record<string, string> = {
    dashboard: '/dashboard',
    alarms: '/alarms',
    rules: '/rules',
    devices: '/devices',
  }
  router.push(map[key] || '/dashboard')
}

function onFactoryChange(value: string | undefined) {
  appStore.selectFactory(value ?? null)
}

function onTreeSelect(keys: string[]) {
  if (keys.length > 0) {
    appStore.selectWorkshop(keys[0])
  }
}

// 应用启动时初始化
onMounted(async () => {
  // 加载工厂树
  await appStore.fetchFactoryTree()

  // 建立 SignalR 连接
  await startSignalR()

  // 初始化声音（等待用户首次交互解锁 AudioContext）
  initSoundOnInteraction()
})

// 应用卸载时断开
onUnmounted(() => {
  stopSignalR()
})
</script>

<template>
  <Layout class="app-layout">
    <!-- SignalR 重连提示 -->
    <Alert
      v-if="showReconnectBanner"
      message="实时连接已断开，正在重连..."
      type="warning"
      banner
      closable
    />

    <!-- 侧边栏 -->
    <LayoutSider
      v-model:collapsed="collapsed"
      collapsible
      :width="240"
      :trigger="null"
      class="app-sider"
    >
      <div class="sider-logo">
        <AlertOutlined class="logo-icon" />
        <span v-if="!collapsed" class="logo-text">AmGateway</span>
      </div>

      <!-- 工厂/车间树 -->
      <div v-if="!collapsed" class="sider-tree">
        <Tree
          v-if="treeData.length > 0"
          :tree-data="treeData"
          :default-expand-all="true"
          :selectable="true"
          @select="onTreeSelect"
        />
        <div v-else class="sider-tree-empty">暂无工厂数据</div>
      </div>

      <!-- 导航菜单 -->
      <Menu
        :selected-keys="selectedKeys"
        mode="inline"
        theme="dark"
        @click="onMenuClick"
      >
        <MenuItem key="dashboard">
          <DashboardOutlined />
          <span>报警看板</span>
        </MenuItem>
        <MenuItem key="alarms">
          <AlertOutlined />
          <span>报警管理</span>
        </MenuItem>
        <MenuItem key="rules">
          <ToolOutlined />
          <span>规则管理</span>
        </MenuItem>
        <MenuItem key="devices">
          <DesktopOutlined />
          <span>设备状态</span>
        </MenuItem>
      </Menu>
    </LayoutSider>

    <!-- 右侧主体 -->
    <Layout>
      <!-- 顶部栏 -->
      <LayoutHeader class="app-header">
        <div class="header-left">
          <Button type="text" @click="collapsed = !collapsed">
            <MenuUnfoldOutlined v-if="collapsed" />
            <MenuFoldOutlined v-else />
          </Button>
        </div>

        <div class="header-center">
          <Select
            :value="appStore.selectedFactoryId"
            placeholder="选择工厂"
            style="width: 200px"
            allow-clear
            :loading="appStore.factoryTreeLoading"
            @change="onFactoryChange"
          >
            <SelectOption v-for="f in factoryOptions" :key="f.value" :value="f.value">
              {{ f.label }}
            </SelectOption>
          </Select>
        </div>

        <div class="header-right">
          <Badge :count="alarmStore.unreadCount" :overflow-count="99">
            <AlertOutlined style="font-size: 18px; cursor: pointer" @click="alarmStore.markAllRead()" />
          </Badge>
          <Button type="text" @click="appStore.toggleSound()" :title="appStore.soundEnabled ? '关闭声音' : '开启声音'">
            <SoundOutlined v-if="appStore.soundEnabled" />
            <span v-else style="position: relative">
              <SoundOutlined />
              <span style="position: absolute; top: -2px; right: -2px; width: 8px; height: 8px; background: #ff4d4f; border-radius: 50%" />
            </span>
          </Button>
        </div>
      </LayoutHeader>

      <!-- 内容区 -->
      <LayoutContent class="app-content">
        <AlarmNotification>
          <router-view />
        </AlarmNotification>
      </LayoutContent>
    </Layout>
  </Layout>
</template>

<style scoped>
.app-layout {
  min-height: 100vh;
}

.app-sider {
  background: #001529;
}

.sider-logo {
  height: 48px;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
}

.logo-icon {
  font-size: 20px;
  color: #1890ff;
}

.logo-text {
  color: #fff;
  font-size: 16px;
  font-weight: 600;
}

.sider-tree {
  padding: 8px 12px;
  max-height: 300px;
  overflow-y: auto;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
}

.sider-tree-empty {
  color: rgba(255, 255, 255, 0.4);
  text-align: center;
  padding: 12px;
  font-size: 12px;
}

.app-header {
  background: #fff;
  display: flex;
  align-items: center;
  padding: 0 16px;
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.08);
  height: 48px;
  line-height: 48px;
}

.header-left {
  flex: 0 0 auto;
}

.header-center {
  flex: 1;
  display: flex;
  justify-content: center;
}

.header-right {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  gap: 12px;
}

.app-content {
  margin: 16px;
  padding: 24px;
  background: #fff;
  border-radius: 8px;
  min-height: calc(100vh - 48px - 32px);
}
</style>
