# AmGatewayCloud.Web — 前端看板（阶段 5）

## 1. 定位

Vue 3 单页应用，作为工业物联网平台的操作入口：

- 实时报警看板：SignalR 订阅 → 新报警弹窗 + 列表实时刷新
- 报警管理：分页查询、确认、抑制、关闭
- 规则管理：规则 CRUD、启停切换
- 设备状态看板：在线/离线概览、ECharts 可视化
- 工厂/车间树形导航：按工厂过滤数据和 SignalR 分组

**不包含**：认证/多租户（阶段 8）、工单系统（阶段 6）、DDD 重构（阶段 6）。

---

## 2. 架构

```
浏览器
┌─────────────────────────────────────────────────────┐
│  Vue 3 SPA (AmGatewayCloud.Web)                     │
│                                                      │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐          │
│  │ 报警看板  │  │ 规则管理  │  │ 设备状态  │          │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘          │
│       │              │              │                 │
│  ┌────▼──────────────▼──────────────▼─────┐          │
│  │         Pinia Stores                    │          │
│  │  alarmStore | ruleStore | deviceStore   │          │
│  └────┬──────────────┬──────────────┬─────┘          │
│       │              │              │                 │
│  ┌────▼─────┐  ┌────▼─────┐  ┌────▼─────┐          │
│  │ axios    │  │ SignalR  │  │ echarts   │          │
│  │ REST API │  │ Hub      │  │ 图表      │          │
│  └────┬─────┘  └────┬─────┘  └──────────┘          │
└───────┼─────────────┼──────────────────────────────┘
        │             │
        ▼             ▼
┌───────────────────────────────┐
│  WebApi (BFF)                 │
│  ├── YARP → AlarmService      │
│  └── SignalR Hub              │
│      └── factory-{id} 分组    │
└───────────────────────────────┘
```

**数据流**：
- **REST**：Vue → axios → WebApi:8080 → YARP → AlarmService:5001
- **实时**：AlarmService → RabbitMQ → WebApi → SignalR → Vue
- **路由**：Vue Router 管理页面导航，工厂上下文通过 URL 参数传递

---

## 3. 页面设计

### 3.1 全局布局

```
┌──────────────────────────────────────────────────────┐
│  顶部导航栏                                           │
│  Logo | 报警看板 | 规则管理 | 设备状态 |  工厂选择器 ▼  │
├──────────┬───────────────────────────────────────────┤
│          │                                           │
│  侧边栏   │           主内容区                        │
│  (可折叠)  │                                           │
│          │                                           │
│ ▸ 工厂 A  │                                           │
│   车间1   │                                           │
│   车间2   │                                           │
│ ▸ 工厂 B  │                                           │
│   车间1   │                                           │
│          │                                           │
├──────────┴───────────────────────────────────────────┤
│  报警通知栏（底部弹出，自动消失）                       │
└──────────────────────────────────────────────────────┘
```

- **工厂选择器**：全局下拉，切换后刷新所有 Store 数据 + 重新 Join SignalR 分组
- **侧边栏**：树形展示工厂→车间结构（数据来自 `GET /api/factories/tree`），点击车间过滤数据
- **报警通知栏**：SignalR 收到新报警时从右下角弹出，5 秒后自动消失

### 3.2 路由设计

| 路径 | 页面 | 说明 |
|------|------|------|
| `/` | 重定向到 `/dashboard` | — |
| `/dashboard` | 报警看板 | 实时报警列表 + 统计卡片 |
| `/dashboard?factoryId=x&workshopId=y` | 报警看板（过滤） | 按工厂/车间过滤 |
| `/alarms` | 报警管理 | 分页列表 + 操作 |
| `/alarms?status=Active&level=Critical` | 报警管理（过滤） | 按状态/级别过滤 |
| `/alarms/:id` | 报警详情 | 单条报警完整信息 |
| `/rules` | 规则管理 | 规则列表 + CRUD |
| `/rules/:id` | 规则编辑 | 编辑/创建规则表单 |
| `/devices` | 设备状态 | 设备在线/离线概览 + 图表 |

### 3.3 页面详细设计

#### 3.3.1 报警看板 `/dashboard`

```
┌─────────────────────────────────────────────────────┐
│ 统计卡片区                                           │
│ ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐        │
│ │ Active │ │ Acked  │ │Suppressed│ │Cleared│        │
│ │   12   │ │   5    │ │    3     │ │  28   │        │
│ │  ⚠ 红色 │ │  🟡黄色│ │  🔵蓝色  │ │ 🟢绿色 │        │
│ └────────┘ └────────┘ └─────────┘ └────────┘        │
├─────────────────────────────────────────────────────┤
│ 实时报警列表                    [按级别排序▼] [刷新]   │
│ ┌───────────────────────────────────────────────┐   │
│ │ 🔴 Critical | 高温严重 | device-001 | 35.2°C  │   │
│ │    temperature > 35  | 2 分钟前  [确认] [抑制] │   │
│ ├───────────────────────────────────────────────┤   │
│ │ 🟡 Warning  | 高温警告 | device-002 | 29.1°C  │   │
│ │    temperature > 28  | 5 分钟前  [确认] [抑制] │   │
│ ├───────────────────────────────────────────────┤   │
│ │ 🔴 Critical | 液位严重 | device-003 | 96.2%   │   │
│ │    level > 95       | 8 分钟前  [确认] [抑制] │   │
│ └───────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────┘
```

- **统计卡片**：调用 `GET /api/alarms/summary` 获取各状态计数，一次请求拿到 Active/Acked/Suppressed/Cleared 数量
- **实时报警列表**：只显示 Active / Acked 状态，SignalR 新增报警插入列表顶部
- **操作按钮**：确认（Active→Acked）、抑制（→Suppressed），点击后调 API + 更新本地状态

#### 3.3.2 报警管理 `/alarms`

```
┌─────────────────────────────────────────────────────┐
│ 过滤条件栏                                           │
│ 工厂:[全部▼] 状态:[Active▼] 级别:[全部▼] 离线:[全部▼] │
│                                          [查询] [重置]│
├─────────────────────────────────────────────────────┤
│ 报警事件表格                     分页: 1/5  每页 20  │
│ ┌─────┬──────┬──────┬──────┬─────┬──────┬─────┐   │
│ │级别  │规则   │设备   │触发值 │状态  │触发时间│操作 │   │
│ ├─────┼──────┼──────┼──────┼─────┼──────┼─────┤   │
│ │Crit │高温严重│dev-01│35.2  │Active│10:30 │详情 │   │
│ │Warn │高温警告│dev-02│29.1  │Acked │10:25 │详情 │   │
│ │Warn │高压警告│dev-03│118.5 │Suppr │10:20 │详情 │   │
│ └─────┴──────┴──────┴──────┴─────┴──────┴─────┘   │
└─────────────────────────────────────────────────────┘
```

- **过滤条件**：factoryId / status / level / isStale，对应 API 查询参数
- **分页**：Ant Design Vue Table 内置分页，映射 API 的 page / pageSize
- **级别颜色**：Critical/Fatal=红、Warning=橙、Info=蓝
- **离线标记**：isStale=true 的行显示离线图标

#### 3.3.3 报警详情 `/alarms/:id`

```
┌─────────────────────────────────────────────────────┐
│ 报警详情                              [返回列表]     │
├─────────────────────────────────────────────────────┤
│ 基本信息                                             │
│ 规则: high-temp-critical / 高温严重                   │
│ 设备: device-001  |  工厂: factory-a  |  车间: ws-1  │
│ 测点: temperature  |  运算符: > 35                    │
│ 触发值: 35.2°C  |  触发时间: 2026-05-10 10:30:00     │
│ 当前状态: Active                                      │
│                                                      │
│ 状态流转                                             │
│ ● Active (10:30) ──○ Acked ──○ Cleared              │
│                                                      │
│ 操作                                                 │
│ [确认报警]  [手动抑制]  [手动关闭]                     │
│                                                      │
│ 恢复信息                                             │
│ (条件恢复后自动填充)                                   │
│ 恢复值: —  |  恢复时间: —                             │
│                                                      │
│ 离线标记: 否                                         │
└─────────────────────────────────────────────────────┘
```

- 确认弹出 Modal：输入 AcknowledgedBy
- 抑制弹出 Modal：输入 SuppressedBy + Reason
- 关闭需二次确认

#### 3.3.4 规则管理 `/rules`

```
┌─────────────────────────────────────────────────────┐
│ 报警规则                          [+ 新建规则]       │
├─────────────────────────────────────────────────────┤
│ ┌──────┬──────┬─────┬──────┬──────┬─────┬─────┐   │
│ │ID    │名称   │测点 │运算符 │阈值   │级别  │状态 │   │
│ ├──────┼──────┼─────┼──────┼──────┼─────┼─────┤   │
│ │high- │高温   │temp │  >   │35    │Crit │ ✅  │   │
│ │temp- │严重   │     │      │      │     │     │   │
│ ├──────┼──────┼─────┼──────┼──────┼─────┼─────┤   │
│ │high- │高温   │temp │  >   │28    │Warn │ ✅  │   │
│ │temp- │警告   │     │      │      │     │     │   │
│ └──────┴──────┴─────┴──────┴──────┴─────┴─────┘   │
│  操作: [编辑] [删除] [启停切换]                       │
└─────────────────────────────────────────────────────┘
```

- 启停切换：`PUT /api/alarmrules/{id}` 设置 `enabled: true/false`
- 删除：二次确认，有 Active/Acked 报警时提示无法删除

#### 3.3.5 规则编辑 `/rules/:id`（新建/编辑共用）

```
┌─────────────────────────────────────────────────────┐
│ 新建报警规则 / 编辑报警规则           [保存] [取消]  │
├─────────────────────────────────────────────────────┤
│ 规则ID: [________]     (新建时可编辑，编辑时只读)     │
│ 规则名称: [________]                                 │
│                                                      │
│ 作用域                                               │
│ 工厂: [全部▼]  设备: [全部▼]                          │
│                                                      │
│ 触发条件                                             │
│ 测点Tag: [________]                                  │
│ 运算符: [> ▼]   阈值: [____]                         │
│ 字符串阈值: [____] (仅 == 运算符)                     │
│ 恢复阈值: [____] (Deadband)                           │
│                                                      │
│ 报警配置                                             │
│ 级别: [Warning ▼]   冷却时间: [5] 分钟               │
│ 描述: [________________________________]              │
│                                                      │
│ [✓] 启用                                             │
└─────────────────────────────────────────────────────┘
```

**表单校验**：
- 规则 ID：必填，仅新建时可编辑
- 名称：必填
- Tag：必填
- 运算符：必须为 `>`, `>=`, `<`, `<=`, `==`, `!=` 之一
- 级别：必须为 `Info`, `Warning`, `Critical`, `Fatal` 之一
- ClearThreshold：`>`/`>=` 时必须 < Threshold；`<`/`<=` 时必须 > Threshold

#### 3.3.6 设备状态看板 `/devices`

```
┌─────────────────────────────────────────────────────┐
│ 设备状态概览                                         │
├─────────────────────────────────────────────────────┤
│ ┌──────────────┐  ┌──────────────────────────────┐  │
│ │ 设备总览饼图  │  │ 报警趋势折线图（24h）        │  │
│ │              │  │                              │  │
│ │  🟢 在线 45  │  │    ╱╲    ╱╲                  │  │
│ │  🔴 离线  3  │  │   ╱  ╲  ╱  ╲                │  │
│ │  🟡 Stale 2  │  │  ╱    ╲╱    ╲               │  │
│ │              │  │                              │  │
│ └──────────────┘  └──────────────────────────────┘  │
├─────────────────────────────────────────────────────┤
│ 离线设备列表                                         │
│ ┌──────┬──────┬──────┬─────────┬──────────┐        │
│ │设备   │工厂  │车间  │最后数据  │离线时长   │        │
│ ├──────┼──────┼──────┼─────────┼──────────┤        │
│ │dev-03│fac-A │ws-2  │09:15    │1h 15m    │        │
│ │dev-07│fac-B │ws-1  │08:30    │2h 00m    │        │
│ └──────┴──────┴──────┴─────────┴──────────┘        │
└─────────────────────────────────────────────────────┘
```

- **设备总览饼图**：从报警 API 中统计 isStale 设备，在线数从设备总数推算
- **报警趋势折线图**：调用 `GET /api/alarms/trend?hours=24` 获取后端按小时聚合的趋势数据（TimescaleDB time_bucket）
- **离线设备列表**：筛选 isStale=true 的报警关联设备

> 注：当前后端没有独立的设备 API，设备状态信息从报警事件中提取。阶段 6 DDD 提炼后可引入专用设备接口。

#### 3.3.7 UI 状态规范

所有数据页面统一遵循以下 UI 状态规范：

| 状态 | 表现 | 交互 |
|------|------|------|
| **Loading** | Ant Design Spin 组件居中覆盖内容区，首次加载用全屏 spin，分页/过滤用表格内 spin | 不可操作 |
| **Empty** | Ant Design Empty 组件 + 描述文案（如"暂无报警数据"） | 可操作筛选/刷新 |
| **Error** | Ant Design Result `status="error"` + 错误描述 + 重试按钮 | 点击重试重新请求 |

规范要点：
- 列表页 Loading：首次加载显示骨架屏（Skeleton），翻页/过滤显示表格 loading 遮罩
- 表单页 Loading：提交时按钮 loading 态，防止重复提交
- SignalR 断连：顶部显示黄色提示条"实时连接已断开，正在重连…"

---

## 4. API 对接

### 4.1 现有 API 清单

前端通过 WebApi BFF（`http://localhost:8080`）访问，YARP 透传到 AlarmService（`http://localhost:5001`）。

| 方法 | 路径 | 说明 | 请求体 | 响应 |
|------|------|------|--------|------|
| GET | `/api/alarms` | 报警列表（分页+过滤） | Query: factoryId, deviceId, status, level, isStale, page, pageSize | `PagedResult<AlarmEventDto>` |
| GET | `/api/alarms/summary` | 报警状态汇总 | Query: factoryId | `AlarmSummaryDto` |
| GET | `/api/alarms/trend` | 报警趋势（按小时聚合） | Query: hours (默认24), factoryId | `List<AlarmTrendPoint>` |
| GET | `/api/alarms/{id}` | 报警详情 | — | `AlarmEventDto` |
| POST | `/api/alarms/{id}/ack` | 确认报警 | `AckRequest` | `AlarmEventDto` |
| POST | `/api/alarms/{id}/suppress` | 抑制报警 | `SuppressRequest` | `AlarmEventDto` |
| POST | `/api/alarms/{id}/clear` | 手动关闭 | — | `AlarmEventDto` |
| GET | `/api/alarmrules` | 规则列表 | Query: factoryId, tag | `List<AlarmRuleDto>` |
| GET | `/api/alarmrules/{id}` | 规则详情 | — | `AlarmRuleDto` |
| POST | `/api/alarmrules` | 创建规则 | `CreateAlarmRuleRequest` | `AlarmRuleDto` (201) |
| PUT | `/api/alarmrules/{id}` | 更新规则 | `UpdateAlarmRuleRequest` | `AlarmRuleDto` |
| DELETE | `/api/alarmrules/{id}` | 删除规则 | — | 204 |
| GET | `/api/factories/tree` | 工厂/车间树 | — | `List<FactoryNode>` |

### 4.2 DTO 结构（TypeScript 映射）

```typescript
// 报警事件
interface AlarmEvent {
  id: string
  ruleId: string
  ruleName: string
  tenantId: string
  factoryId: string
  workshopId: string | null
  deviceId: string
  tag: string
  triggerValue: number | null
  level: 'Info' | 'Warning' | 'Critical' | 'Fatal'
  status: 'Active' | 'Acked' | 'Suppressed' | 'Cleared'
  isStale: boolean
  staleAt: string | null
  message: string | null
  triggeredAt: string
  acknowledgedAt: string | null
  acknowledgedBy: string | null
  suppressedAt: string | null
  suppressedBy: string | null
  suppressedReason: string | null
  clearedAt: string | null
  clearValue: number | null
  createdAt: string
}

// 报警规则
interface AlarmRule {
  id: string
  name: string
  tenantId: string
  factoryId: string | null
  deviceId: string | null
  tag: string
  operator: string
  threshold: number
  thresholdString: string | null
  clearThreshold: number | null
  level: 'Info' | 'Warning' | 'Critical' | 'Fatal'
  cooldownMinutes: number
  delaySeconds: number
  enabled: boolean
  description: string | null
  createdAt: string
  updatedAt: string
}

// 分页结果
interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
  totalPages: number
  hasNextPage: boolean
  hasPrevPage: boolean
}

// 请求体
interface CreateAlarmRuleRequest {
  id: string
  name: string
  tenantId?: string
  factoryId?: string | null
  deviceId?: string | null
  tag: string
  operator: string
  threshold: number
  thresholdString?: string | null
  clearThreshold?: number | null
  level: string
  cooldownMinutes?: number
  delaySeconds?: number
  enabled?: boolean
  description?: string | null
}

interface UpdateAlarmRuleRequest {
  name?: string | null
  factoryId?: string | null
  deviceId?: string | null
  tag?: string | null
  operator?: string | null
  threshold?: number | null
  thresholdString?: string | null
  clearThreshold?: number | null
  level?: string | null
  cooldownMinutes?: number | null
  delaySeconds?: number | null
  enabled?: boolean | null
  description?: string | null
}

interface AckRequest {
  acknowledgedBy: string
}

interface SuppressRequest {
  suppressedBy: string
  reason?: string | null
}

// 报警状态汇总（统计卡片用）
interface AlarmSummary {
  active: number
  acked: number
  suppressed: number
  cleared: number
}

// 报警趋势数据点（趋势图用）
interface AlarmTrendPoint {
  hour: string              // ISO 8601 时间桶起点
  total: number
  critical: number          // Critical + Fatal 合计
  warning: number
  info: number
}

// 工厂/车间树（侧边栏用）
interface FactoryNode {
  id: string
  name: string
  workshops: WorkshopNode[]
}

interface WorkshopNode {
  id: string
  name: string
}
```

### 4.3 axios 实例配置

```typescript
// src/api/client.ts
const apiClient = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || 'http://localhost:8080',
  timeout: 10000,
  headers: { 'Content-Type': 'application/json' }
})

// 响应拦截：统一错误处理
apiClient.interceptors.response.use(
  (res) => res,
  (error) => {
    const msg = error.response?.data || error.message
    message.error(`请求失败: ${msg}`)
    return Promise.reject(error)
  }
)
```

---

## 5. SignalR 对接

### 5.1 连接配置

```typescript
// src/composables/useAlarmSignalR.ts
const hubUrl = `${import.meta.env.VITE_API_BASE_URL || 'http://localhost:8080'}/hubs/alarm`

const connection = new HubConnectionBuilder()
  .withUrl(hubUrl)
  .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
  .build()
```

### 5.2 服务端方法（前端调用）

| 方法 | 参数 | 说明 |
|------|------|------|
| `JoinFactory` | `factoryId: string` | 加入工厂分组，只接收该工厂报警 |
| `LeaveFactory` | `factoryId: string` | 离开工厂分组 |

### 5.3 客户端监听（服务端推送）

采用**单一事件名**设计，前端通过 `msg.status` 区分报警状态，按需做不同 UI 处理。

| 事件名 | 载荷 | 说明 |
|--------|------|------|
| `AlarmReceived` | `AlarmEventMessage` | 所有报警事件统一推送，通过 `status` 字段区分 |

**前端分发逻辑**：

```typescript
connection.on("AlarmReceived", (msg: AlarmEventMessage) => {
  switch (msg.status) {
    case 'Active':     // 新报警触发 → 弹窗通知 + 播声音 + 列表顶部插入
    case 'Cleared':    // 报警自动恢复 → 静默更新列表项状态
    case 'Acked':      // 报警被确认 → 更新列表项状态
    case 'Suppressed': // 报警被抑制 → 更新列表项状态
  }
})
```

> **设计决策**：为什么用单一事件名而不是多个？
> - 后端只需一行 `SendAsync("AlarmReceived", message)`，零分支
> - 新增报警状态时后端不用改推送逻辑，前端加一个 `case` 即可
> - 前端逻辑集中在单个 handler 中，store 更新一致性好

### 5.4 SignalR 消息契约

```typescript
// 与 AlarmEventMessage 对应
interface AlarmEventMessage {
  id: string
  ruleId: string
  ruleName: string
  tenantId: string
  factoryId: string
  workshopId: string | null
  deviceId: string
  tag: string
  triggerValue: number | null
  level: string
  status: string
  isStale: boolean
  message: string | null
  triggeredAt: string
  suppressedAt: string | null
  suppressedBy: string | null
  clearedAt: string | null
  clearValue: number | null
}
```

### 5.5 连接生命周期

```
App 挂载
  ├── 建立 SignalR 连接
  ├── 监听 "AlarmReceived" 事件 → 按 status 分发更新 alarmStore
  ├── 启动后自动 JoinFactory（如有选中工厂）
  │
  ├── 工厂切换时
  │   ├── LeaveFactory(旧工厂)
  │   └── JoinFactory(新工厂)
  │
  ├── SignalR 重连时
  │   └── 重新 JoinFactory(当前选中工厂)  // 重连后分组订阅丢失，必须重新加入
  │
  └── App 卸载
      └── 停止 SignalR 连接
```

---

## 6. Pinia 状态管理

### 6.1 alarmStore

```typescript
// src/stores/alarm.ts
interface AlarmState {
  // 列表查询
  alarms: AlarmEvent[]
  totalCount: number
  currentPage: number
  pageSize: number
  filters: {
    factoryId: string | null
    deviceId: string | null
    status: string | null
    level: string | null
    isStale: boolean | null
  }
  loading: boolean

  // 汇总（统计卡片）
  summary: AlarmSummary | null

  // 趋势（趋势图）
  trendData: AlarmTrendPoint[]
  trendHours: number  // 趋势查询范围，默认 24

  // 详情
  currentAlarm: AlarmEvent | null

  // 实时推送（SignalR）累积的未读计数
  unreadCount: number
}

// Actions
fetchAlarms()          // GET /api/alarms + filters
fetchAlarmById(id)     // GET /api/alarms/{id}
fetchSummary()         // GET /api/alarms/summary?factoryId=xxx → 更新 summary
fetchTrend(hours?)     // GET /api/alarms/trend?hours=24&factoryId=xxx → 更新 trendData
acknowledge(id, by)    // POST /api/alarms/{id}/ack
suppress(id, by, reason) // POST /api/alarms/{id}/suppress
clear(id)              // POST /api/alarms/{id}/clear
handleSignalREvent(msg) // 处理 SignalR 推送，更新列表/未读/汇总
resetFilters()         // 重置过滤条件
```

### 6.2 ruleStore

```typescript
// src/stores/rule.ts
interface RuleState {
  rules: AlarmRule[]
  currentRule: AlarmRule | null
  loading: boolean
  filters: {
    factoryId: string | null
    tag: string | null
  }
}

// Actions
fetchRules()           // GET /api/alarmrules
fetchRuleById(id)      // GET /api/alarmrules/{id}
createRule(data)       // POST /api/alarmrules
updateRule(id, data)   // PUT /api/alarmrules/{id}
deleteRule(id)         // DELETE /api/alarmrules/{id}
toggleEnabled(id, enabled) // 便捷方法：PUT 启停切换
```

### 6.3 appStore

```typescript
// src/stores/app.ts
interface AppState {
  selectedFactoryId: string | null
  selectedWorkshopId: string | null
  sidebarCollapsed: boolean
  factoryTree: FactoryNode[]      // 工厂/车间树（来自 GET /api/factories/tree）
  factoryTreeLoading: boolean
}

// Actions
selectFactory(id)      // 切换工厂 → 更新 SignalR 分组 + 刷新各 Store
selectWorkshop(id)     // 切换车间 → 过滤数据
toggleSidebar()        // 折叠/展开侧边栏
fetchFactoryTree()     // GET /api/factories/tree → 更新 factoryTree
```

### 6.4 deviceStore

```typescript
// src/stores/device.ts
interface DeviceState {
  // 从报警数据中提取的设备状态（无独立设备 API）
  staleDevices: AlarmEvent[]  // isStale=true 的最新报警
  onlineCount: number         // 从规则/历史推算
  staleCount: number
  offlineCount: number
}

// Actions
refreshDeviceStatus()  // 查询 isStale 报警，聚合设备状态
```

---

## 7. 项目结构

```
src/AmGatewayCloud.Web/
├── index.html
├── package.json
├── vite.config.ts
├── tsconfig.json
├── env.d.ts
├── .env                           # VITE_API_BASE_URL=http://localhost:8080
├── .env.production                # VITE_API_BASE_URL=（相对路径，通过 nginx 代理）
├── public/
│   └── favicon.ico
└── src/
    ├── main.ts                    # App 创建 + 插件注册
    ├── App.vue                    # 根组件（全局布局）
    ├── router/
    │   └── index.ts               # Vue Router 路由定义
    ├── api/
    │   ├── client.ts              # axios 实例 + 拦截器
    │   ├── alarms.ts              # 报警 API 封装（列表 + 汇总 + 趋势 + 操作）
    │   ├── rules.ts               # 规则 API 封装
    │   └── factories.ts          # 工厂/车间树 API 封装
    ├── types/
    │   └── index.ts               # TypeScript 接口定义（DTO 映射）
    ├── stores/
    │   ├── alarm.ts               # alarmStore
    │   ├── rule.ts                # ruleStore
    │   ├── device.ts              # deviceStore
    │   └── app.ts                 # appStore
    ├── composables/
    │   ├── useAlarmSignalR.ts     # SignalR 连接 + 事件监听
    │   └── useAlarmNotification.ts # 报警弹窗通知逻辑
    ├── views/
    │   ├── DashboardView.vue      # 报警看板
    │   ├── AlarmsView.vue         # 报警管理列表
    │   ├── AlarmDetailView.vue    # 报警详情
    │   ├── RulesView.vue          # 规则管理列表
    │   ├── RuleEditView.vue       # 规则新建/编辑表单
    │   └── DevicesView.vue        # 设备状态看板
    ├── components/
    │   ├── layout/
    │   │   ├── AppLayout.vue      # 全局布局壳
    │   │   ├── AppHeader.vue      # 顶部导航栏
    │   │   ├── AppSidebar.vue     # 侧边栏（工厂树）
    │   │   └── AlarmNotification.vue # 底部报警弹窗
    │   ├── alarm/
    │   │   ├── AlarmStatsCards.vue # 统计卡片
    │   │   ├── AlarmTable.vue     # 报警表格（管理页用）
    │   │   ├── AlarmLiveList.vue  # 实时报警列表（看板用）
    │   │   ├── AlarmDetail.vue    # 报警详情组件
    │   │   └── AlarmActions.vue   # 确认/抑制/关闭操作按钮
    │   ├── rule/
    │   │   ├── RuleTable.vue      # 规则表格
    │   │   └── RuleForm.vue       # 规则新建/编辑表单
    │   └── device/
    │       ├── DeviceOverviewChart.vue  # 设备总览饼图
    │       ├── AlarmTrendChart.vue      # 报警趋势折线图
    │       └── StaleDeviceTable.vue     # 离线设备列表
    └── utils/
        ├── constants.ts            # 常量（级别颜色映射、状态枚举等）
        └── format.ts              # 格式化工具（时间、数值）
```

---

## 8. 前端技术栈版本

| 依赖 | 版本 | 说明 |
|------|------|------|
| vue | ^3.5 | Composition API + `<script setup>` |
| vue-router | ^4.4 | 路由管理 |
| pinia | ^2.2 | 状态管理 |
| ant-design-vue | ^4.2 | UI 组件库 |
| @ant-design/icons-vue | ^7.0 | 图标 |
| echarts | ^5.5 | 图表核心 |
| vue-echarts | ^7.0 | ECharts Vue 封装 |
| @microsoft/signalr | ^8.0 | SignalR 客户端 |
| axios | ^1.7 | HTTP 客户端 |
| dayjs | ^1.11 | 时间处理（Ant Design Vue 内置依赖） |
| typescript | ^5.6 | 类型安全 |
| vite | ^6.0 | 构建工具 |
| @vitejs/plugin-vue | ^5.0 | Vite Vue 插件 |

---

## 9. Docker 部署

### 9.1 Dockerfile

```dockerfile
# 构建阶段
FROM node:22-alpine AS build
WORKDIR /app
COPY src/AmGatewayCloud.Web/package.json src/AmGatewayCloud.Web/package-lock.json* ./
RUN npm ci
COPY src/AmGatewayCloud.Web/ .
RUN npm run build

# 运行阶段
FROM nginx:alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY docker/web/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
```

### 9.2 nginx.conf

```nginx
server {
    listen 80;
    server_name _;
    root /usr/share/nginx/html;
    index index.html;

    # SPA 路由回退
    location / {
        try_files $uri $uri/ /index.html;
    }

    # API 代理 → WebApi BFF
    location /api/ {
        proxy_pass http://amgw-webapi:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    # SignalR WebSocket 代理
    location /hubs/ {
        proxy_pass http://amgw-webapi:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_cache off;
    }

    # 静态资源缓存
    location /assets/ {
        expires 30d;
        add_header Cache-Control "public, immutable";
    }
}
```

### 9.3 docker-compose 新增服务

```yaml
  # ── 前端看板 (Phase 5) ──
  web:
    build:
      context: .
      dockerfile: docker/web/Dockerfile
    container_name: amgw-web
    ports:
      - "80:80"
    depends_on:
      webapi:
        condition: service_started
    restart: unless-stopped
```

### 9.4 环境变量

```bash
# .env（开发环境）
VITE_API_BASE_URL=http://localhost:8080

# .env.production（生产环境，通过 nginx 代理，用相对路径）
VITE_API_BASE_URL=
```

生产环境下前端通过 nginx 反向代理访问 API，无需跨域配置。

---

## 10. 后端配置变更

### 10.1 WebApi CORS 更新

`appsettings.json` 的 `CorsOrigins` 需要添加前端生产地址：

```json
{
  "WebApi": {
    "CorsOrigins": [
      "http://localhost:5173",
      "http://localhost:80",
      "http://127.0.0.1"
    ]
  }
}
```

Docker 部署时通过 nginx 代理，前端请求同源，不触发 CORS。开发模式下通过 Vite proxy 代理，也无需 CORS。

### 10.2 Vite 开发代理

开发时前端 dev server (5173) 通过 Vite proxy 代理 API 和 SignalR 请求到 WebApi (8080)，实现同源访问，彻底避免 CORS 问题：

```typescript
// vite.config.ts
export default defineConfig({
  server: {
    proxy: {
      '/api': 'http://localhost:8080',
      '/hubs': {
        target: 'http://localhost:8080',
        ws: true  // 启用 WebSocket 代理（SignalR 需要）
      }
    }
  }
})
```

> 注：使用 Vite proxy 后，`VITE_API_BASE_URL` 开发环境可留空（走 proxy），生产环境通过 nginx 代理也无需配置。

### 10.2 WebApi YARP 路由新增

`appsettings.json` 的 `ReverseProxy.Routes` 需要新增工厂路由（AlarmService Phase 5 新增的 `GET /api/factories/tree`）：

```json
{
  "factories-route": {
    "ClusterId": "alarm-service",
    "Match": { "Path": "/api/factories/{**catch-all}" }
  }
}
```

### 10.3 docker-compose WebApi CORS 环境变量

```yaml
  webapi:
    # ... 已有配置 ...
    environment:
      # ... 已有环境变量 ...
      WebApi__CorsOrigins__0: "http://localhost:5173"
      WebApi__CorsOrigins__1: "http://localhost"
      WebApi__CorsOrigins__2: "http://amgw-web"
```

---

## 11. 开发工作流

### 11.1 本地开发

```bash
# 终端 1：启动后端（docker compose）
docker compose up -d timescaledb rabbitmq alarm-service webapi

# 终端 2：启动前端 dev server（Vite proxy 已配置，无需 CORS）
cd src/AmGatewayCloud.Web
npm install
npm run dev    # http://localhost:5173
```

### 11.2 Docker 部署验证

```bash
docker compose up -d    # 启动所有服务
# 浏览器访问 http://localhost
```

---

## 12. 验证标准

| # | 验证项 | 方法 |
|---|--------|------|
| 1 | 报警看板加载 | 访问 `/dashboard`，看到统计卡片和报警列表 |
| 2 | 实时报警推送 | 数据超限触发报警 → 前端右下角弹出通知 + 列表顶部新增 |
| 3 | SignalR 工厂分组 | 切换工厂选择器 → 只看到该工厂的报警推送 |
| 4 | 报警确认 | 点击确认 → 输入操作人 → 状态变 Acked |
| 5 | 报警抑制 | 点击抑制 → 输入操作人和原因 → 状态变 Suppressed |
| 6 | 报警关闭 | 点击关闭 → 二次确认 → 状态变 Cleared |
| 7 | 报警过滤 | 按状态/级别/工厂过滤 → 列表更新 |
| 8 | 规则列表 | 访问 `/rules` → 看到所有规则 |
| 9 | 规则创建 | 新建规则 → 表单校验 → 保存成功 |
| 10 | 规则编辑 | 编辑规则 → 修改阈值 → 保存成功 |
| 11 | 规则启停 | 点击开关 → 规则 enabled 状态切换 |
| 12 | 规则删除 | 删除规则 → 二次确认 → 成功（或提示有活跃报警） |
| 13 | 设备看板 | 饼图显示设备在线/离线比例 |
| 14 | Docker 部署 | `docker compose up` → 浏览器访问 `http://localhost` 正常使用 |
| 15 | ClearThreshold 校验 | `>` 运算符设置 ClearThreshold >= Threshold → 表单报错 |

---

## 13. 后续演进

| 阶段 | 变化 |
|------|------|
| Phase 6 | 工单页：前端新增工单列表、工单详情、工单分配 |
| Phase 6 | 规则热更新反馈：修改规则后前端收到通知刷新列表 |
| Phase 7 | 图表增强：OpenTelemetry 链路追踪可视化 |
| Phase 8 | 登录页：JWT 认证，多租户切换 |
| Phase 8 | 用户管理页：租户管理员分配权限 |
