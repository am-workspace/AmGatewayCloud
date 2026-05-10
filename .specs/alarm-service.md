# AmGatewayCloud.AlarmService — 报警服务 + BFF 架构

## 1. 定位

### AlarmService — 独立业务微服务

- 定时拉取 TimescaleDB 最新时序数据，评估报警规则
- 生成报警事件，写入 PostgreSQL（alarm_rules / alarm_events）
- 发布报警事件到 RabbitMQ（/business vhost），供 WebApi 订阅
- 报警防抖（Cooldown + Deadband），避免报警风暴
- 报警自动恢复：条件不再满足时自动 Cleared
- 提供 HTTP API：报警查询/确认/抑制/关闭、规则 CRUD

### WebApi — 纯 BFF（Backend for Frontend）

- YARP 反向代理：`/api/alarms/*`、`/api/alarmrules/*` → AlarmService
- SignalR Hub：订阅 RabbitMQ 报警事件，实时推送前端
- **无数据库访问**、无业务逻辑

### Shared — 共享契约库

- DTOs、常量、MQ 消息定义、配置模型
- AlarmService 和 WebApi 共同引用

---

## 2. 架构

```
数据管道层（Phase 1-3，已完成）                     业务应用层（Phase 4+）
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━                    ━━━━━━━━━━━━━━━━━━━━━━

采集器 → EdgeHub → RabbitMQ (/pipeline)             AlarmService (WebApplication)
                      │                                    │
                      ▼                                    │ read
                 CloudGateway                              ▼
                      │                            TimescaleDB (device_data)
                      ├──► TimescaleDB                    │
                      └──► PostgreSQL                     │ eval rules
                           (devices/factories)            │
                                                        ▼
                                                   PostgreSQL
                                                   (alarm_rules/alarm_events)
                                                        │
                                                        │ publish
                                                        ▼
                                                 RabbitMQ (/business)
                                                 amgateway.alarms
                                                        │
                          ┌─────────────────────────────┤
                          │ subscribe                    │ HTTP API
                          ▼                              │
                   WebApi (BFF) ◄────────────────────────┘
                   ├── SignalR Hub ──► Vue
                   └── YARP Proxy ──► AlarmService:5001
```

**架构原则**：
- 管道层和业务层**单向依赖**：业务层读管道层的数据，管道层不知道报警的存在
- RabbitMQ 用 **vhost 隔离**：`/pipeline` 给管道层，`/business` 给业务层
- WebApi 定位为 **纯 BFF**：YARP 转发 + SignalR 推送，**零业务逻辑、零数据库访问**
- AlarmService 是**完整业务服务**：包含 API、评估引擎、数据访问、消息发布

---

## 3. 数据流转

```
1. AlarmEvaluationHostedService（定时，默认每 5 秒）
   │
   ├── 查询 TimescaleDB：获取自 lastEvalTime 以来的最新数据（限制最大查询窗口）
   │   SELECT DISTINCT ON (factory_id, device_id, tag)
   │          time, tenant_id, factory_id, workshop_id, device_id,
   │          tag, quality, value_float, value_int, value_bool, value_string
   │   FROM device_data
   │   WHERE time > @since
   │   ORDER BY factory_id, device_id, tag, time DESC
   │
   ├── 加载 alarm_rules（enabled=true，30秒缓存刷新）
   │
   ├── 逐条数据匹配规则
   │   └── RuleEvaluator.Evaluate(dataPoint, rule) → bool
   │
   ├── CooldownManager 检查
   │   └── 同一 (ruleId, deviceId) 在 cooldown 内？→ 跳过
   │
   ├── Suppressed 检查
   │   └── 同一 (ruleId, deviceId) 有 Suppressed 报警？→ 跳过（不重复触发）
   │
   ├── 生成 AlarmEvent
   │   ├── 新报警：status = Active
   │   ├── 已有报警自动恢复：status = Cleared，重置冷却
   │   └── Suppressed 报警条件恢复：status = Cleared，重置冷却
   │
   ├── 写入 PostgreSQL alarm_events 表（唯一索引防并发）
   │
   ├── 发布到 RabbitMQ
   │   Exchange: amgateway.alarms (Topic, durable)
   │   RoutingKey: alarm.{tenantId}.{factoryId}.{level}
   │
   ├── 设备离线检测（每轮评估后）
   │   └── 超过阈值时间无数据的设备，其报警标记 is_stale = true
   │
   └── 更新 lastEvalTime
```

```
2. WebApi 订阅 RabbitMQ
   │
   └── 收到报警事件 → 按工厂分组推送到 SignalR Hub
       ├── factory-{factoryId} 分组推送
       └── 无工厂信息时广播 All

3. 前端请求流程
   │
   ├── Vue → WebApi (YARP) → AlarmService HTTP API
   │   ├── GET  /api/alarms              查询报警列表（分页 + 过滤）
   │   ├── GET  /api/alarms/{id}         查询单条报警
   │   ├── GET  /api/alarms/summary      报警状态汇总（统计卡片用）
   │   ├── GET  /api/alarms/trend        报警趋势数据（按小时聚合，趋势图用）
   │   ├── POST /api/alarms/{id}/ack     确认报警
   │   ├── POST /api/alarms/{id}/suppress 手动抑制
   │   ├── POST /api/alarms/{id}/clear   关闭报警
   │   ├── GET  /api/alarmrules          查询规则列表
   │   ├── POST /api/alarmrules          创建规则
   │   ├── PUT  /api/alarmrules/{id}     更新规则
   │   ├── DELETE /api/alarmrules/{id}   删除规则
   │   └── GET  /api/factories/tree     工厂/车间树形结构（侧边栏用）
   │
   └── Vue ← SignalR Hub ← AlarmEventSubscriber（实时推送）
```

---

## 4. 数据模型

### 4.1 AlarmRule — 报警规则

```sql
CREATE TABLE alarm_rules (
    id              TEXT PRIMARY KEY,             -- 规则ID，如 "high-temp-critical"
    name            TEXT NOT NULL,                -- 规则名称: "高温严重报警"
    tenant_id       TEXT NOT NULL DEFAULT 'default',
    factory_id      TEXT,                         -- NULL = 全局（所有工厂）
    device_id       TEXT,                         -- NULL = 同工厂所有设备
    tag             TEXT NOT NULL,                -- 测点: "temperature"
    operator        TEXT NOT NULL,                -- 运算符: >, >=, <, <=, ==, !=
    threshold       DOUBLE PRECISION NOT NULL,    -- 触发阈值: 35.0
    threshold_string TEXT,                        -- 字符串阈值: "Bad"（用于 == 比较）
    clear_threshold DOUBLE PRECISION,             -- 恢复阈值(Deadband): 30.0
    level           TEXT NOT NULL DEFAULT 'Warning',  -- Info/Warning/Critical/Fatal
    cooldown_minutes INT NOT NULL DEFAULT 5,      -- 冷却时间
    delay_seconds   INT NOT NULL DEFAULT 0,       -- 延迟确认（预留）
    enabled         BOOLEAN NOT NULL DEFAULT TRUE,
    description     TEXT,                         -- 报警描述模板
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    updated_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_alarm_rules_tag ON alarm_rules (tag, enabled) WHERE enabled = TRUE;
CREATE INDEX idx_alarm_rules_scope ON alarm_rules (tenant_id, factory_id, device_id);
```

**规则作用域优先级**：`device_id` 指定 > 仅 `factory_id` 指定 > 全局（两者都 NULL）

**运算符与值类型映射**：

| 运算符 | 适用值列 | 说明 |
|--------|---------|------|
| `>`, `>=`, `<`, `<=` | value_float, value_int | 数值比较 |
| `==`, `!=` | value_float, value_int, value_bool | 相等/不等 |
| `==` | value_string + threshold_string | 字符串匹配（如 quality == "Bad"） |

**Deadband 机制**：
- 规则 `temperature > 35, clear_threshold = 30`
- 温度 35.1 → 触发报警（Active）
- 温度回落到 33 → 仍在报警（30 < 33 < 35）
- 温度回落到 29 → 自动恢复（Cleared，低于 clear_threshold）

**ClearThreshold 校验规则**：
- `>`/`>=` 运算符：`clear_threshold < threshold`（恢复阈值必须小于触发阈值）
- `<`/`<=` 运算符：`clear_threshold > threshold`（恢复阈值必须大于触发阈值）
- `==`/`!=` 运算符：ClearThreshold 不适用数值比较，字符串比较自动恢复逻辑为"当前值不再满足触发条件"
- 校验在 AlarmRuleService 中执行（AlarmService 内部）

### 4.2 AlarmEvent — 报警事件

```sql
CREATE TABLE alarm_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rule_id         TEXT NOT NULL REFERENCES alarm_rules(id),
    tenant_id       TEXT NOT NULL,
    factory_id      TEXT NOT NULL,
    workshop_id     TEXT,
    device_id       TEXT NOT NULL,
    tag             TEXT NOT NULL,
    trigger_value   DOUBLE PRECISION,             -- 触发时的值
    level           TEXT NOT NULL,                 -- Warning/Critical/...
    status          TEXT NOT NULL DEFAULT 'Active', -- Active/Acked/Suppressed/Cleared
    is_stale        BOOLEAN NOT NULL DEFAULT FALSE, -- 设备离线标记
    stale_at        TIMESTAMPTZ,                   -- 设备离线时间
    message         TEXT,                          -- 生成时的描述
    triggered_at    TIMESTAMPTZ NOT NULL,
    acknowledged_at TIMESTAMPTZ,
    acknowledged_by TEXT,
    suppressed_at   TIMESTAMPTZ,                   -- 手动抑制时间
    suppressed_by   TEXT,                          -- 抑制操作人
    suppressed_reason TEXT,                        -- 抑制原因
    cleared_at      TIMESTAMPTZ,
    clear_value     DOUBLE PRECISION,              -- 恢复时的值
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_alarm_events_lookup
    ON alarm_events (tenant_id, factory_id, device_id, triggered_at DESC);
CREATE INDEX idx_alarm_events_status
    ON alarm_events (status, triggered_at DESC) WHERE status IN ('Active', 'Acked', 'Suppressed');
CREATE INDEX idx_alarm_events_rule_device
    ON alarm_events (rule_id, device_id, status) WHERE status IN ('Active', 'Acked');
-- 多实例安全：同一 (rule_id, device_id) 只允许一条 Active/Acked 报警
CREATE UNIQUE INDEX idx_alarm_events_active_unique
    ON alarm_events (rule_id, device_id) WHERE status IN ('Active', 'Acked');
```

**报警生命周期**：

```
         触发                     确认                    恢复
  ┌──────────┐            ┌──────────┐            ┌──────────┐
  │ Active   │───────────►│ Acked    │───────────►│ Cleared  │
  └──────────┘            └──────────┘            └──────────┘
       │                       │                       ▲
       │                       │  手动抑制              │
       │                       └──────────┐            │
       │                                  ▼            │
       │            条件不再满足    ┌──────────┐        │
       ├─────────────────────────►│ Suppressed│───────┘
       │            自动恢复      └──────────┘ 条件恢复
       │                                  │
       │                                  ▼
       └────────────────────────────►┌──────────┐
                  自动恢复            │ Cleared  │
                                      └──────────┘
```

- **Active → Acked**：运维人员手动确认
- **Active → Cleared**：条件不再满足（值回到 clear_threshold 以内），自动更新
- **Acked → Cleared**：已确认的报警，条件恢复后自动关闭
- **Active/Acked → Suppressed**：运维人员手动抑制，表示"已处理，不要因为当前条件再重复报警"
- **Suppressed → Cleared**：条件恢复后自动关闭，释放抑制占位
- 同一 (rule_id, device_id) 同一时刻只允许一条 Active/Acked 报警（由唯一索引保证）
- Suppressed 状态下，AlarmService 不会为同一 (rule_id, device_id) 创建新报警
- `is_stale`：设备离线后标记为 true，报警仍保持 Active；设备恢复后自动置为 false

### 4.3 新增 API 数据模型（Phase 5 前端需求）

#### AlarmSummaryDto — 报警状态汇总

```csharp
public class AlarmSummaryDto
{
    public int Active { get; set; }
    public int Acked { get; set; }
    public int Suppressed { get; set; }
    public int Cleared { get; set; }
}
```

**SQL 实现**：

```sql
SELECT status, COUNT(*) as count
FROM alarm_events
WHERE (@factoryId IS NULL OR factory_id = @factoryId)
GROUP BY status
```

#### AlarmTrendPoint — 报警趋势数据点

```csharp
public class AlarmTrendPoint
{
    public DateTime Hour { get; set; }       // 时间桶起点
    public int Total { get; set; }           // 该小时总报警数
    public int Critical { get; set; }        // Critical + Fatal 合计
    public int Warning { get; set; }         // Warning 数
    public int Info { get; set; }            // Info 数
}
```

**SQL 实现（TimescaleDB time_bucket）**：

```sql
SELECT time_bucket('1 hour', triggered_at) AS hour,
       COUNT(*) AS total,
       COUNT(*) FILTER (WHERE level IN ('Critical', 'Fatal')) AS critical,
       COUNT(*) FILTER (WHERE level = 'Warning') AS warning,
       COUNT(*) FILTER (WHERE level = 'Info') AS info
FROM alarm_events
WHERE triggered_at > NOW() - INTERVAL '@hours hours'
  AND (@factoryId IS NULL OR factory_id = @factoryId)
GROUP BY hour
ORDER BY hour
```

#### FactoryNode / WorkshopNode — 工厂/车间树

```csharp
public class FactoryNode
{
    public string Id { get; set; }
    public string Name { get; set; }
    public List<WorkshopNode> Workshops { get; set; } = [];
}

public class WorkshopNode
{
    public string Id { get; set; }
    public string Name { get; set; }
}
```

**SQL 实现**：

```sql
-- 1. 查询所有工厂
SELECT id, name FROM factories ORDER BY name;

-- 2. 查询所有车间
SELECT id, name, factory_id FROM workshops ORDER BY factory_id, name;

-- 3. 内存中组装树形结构
```

---

## 5. 项目结构

### 5.1 AmGatewayCloud.Shared — 共享契约库

```
src/AmGatewayCloud.Shared/
├── AmGatewayCloud.Shared.csproj        # net10.0 类库，无外部依赖
├── Configuration/
│   └── DatabaseConfigs.cs              # PostgreSqlConfig + TimescaleDbConfig + RabbitMqConfig
├── Constants/
│   └── AlarmConstants.cs               # ValidOperators、ValidLevels、Exchange/Queue 常量
├── DTOs/
│   ├── AlarmEventDto.cs                # 报警事件 DTO（API 返回）
│   ├── AlarmRuleDto.cs                 # 报警规则 DTO（API 返回）
│   ├── AlarmRuleRequests.cs            # CreateAlarmRuleRequest / UpdateAlarmRuleRequest / AckRequest / SuppressRequest
│   ├── AlarmSummaryDto.cs              # AlarmSummaryDto — 报警状态汇总（Phase 5 前端统计卡片）
│   ├── AlarmTrendDto.cs                # AlarmTrendPoint — 报警趋势数据点（Phase 5 前端趋势图）
│   ├── FactoryTreeDto.cs               # FactoryNode / WorkshopNode — 工厂/车间树（Phase 5 前端侧边栏）
│   └── PagedResult.cs                  # 分页查询结果泛型
└── Messages/
    └── AlarmEventMessage.cs            # RabbitMQ 报警事件消息契约
```

### 5.2 AmGatewayCloud.AlarmService — 报警业务服务

```
src/AmGatewayCloud.AlarmService/
├── AmGatewayCloud.AlarmService.csproj  # Web SDK + Npgsql + Dapper + RabbitMQ + Swagger
├── Program.cs                          # WebApplication 入口 + DI + 数据库初始化
├── appsettings.json                    # 默认配置
├── Configuration/
│   └── AlarmServiceConfig.cs           # 评估参数 + 数据库/MQ 配置
├── Controllers/
│   ├── AlarmsController.cs             # 报警查询/确认/抑制/关闭
│   ├── AlarmRulesController.cs        # 规则 CRUD
│   └── FactoriesController.cs         # 工厂/车间树查询（Phase 5 前端侧边栏）
├── Models/
│   ├── AlarmRule.cs                    # 报警规则模型
│   ├── AlarmEvent.cs                   # 报警事件模型 + AlarmStatus 枚举
│   └── DataPointReadModel.cs           # 时序数据读取模型
├── Services/
│   ├── AlarmEvaluationHostedService.cs # 报警评估主循环（BackgroundService）
│   ├── AlarmRuleService.cs             # 规则管理业务（CRUD + ClearThreshold 校验）
│   ├── AlarmQueryService.cs            # 报警查询业务（分页 + 汇总 + 趋势 + 确认 + 抑制 + 关闭）
│   ├── RuleEvaluator.cs                # 规则评估器（阈值判断 + Deadband）
│   ├── CooldownManager.cs             # 冷却管理器（内存字典）
│   ├── AlarmEventRepository.cs        # 报警事件仓储（Dapper + Npgsql）
│   ├── AlarmRuleRepository.cs         # 报警规则仓储（Dapper + Npgsql，含白名单校验）
│   ├── FactoryService.cs              # 工厂/车间树查询（Dapper + Npgsql，读 factories/workshops 表）
│   ├── TimescaleDbReader.cs           # 时序数据读取器
│   ├── AlarmEventPublisher.cs         # RabbitMQ 报警事件发布
│   └── HealthChecks.cs                # PostgreSQL + RabbitMQ 健康检查
└── Infrastructure/
    ├── AlarmDbInitializer.cs           # 数据库建表 + 种子规则
    └── RabbitMqConnectionManager.cs   # RabbitMQ 连接管理
```

### 5.3 AmGatewayCloud.WebApi — BFF

```
src/AmGatewayCloud.WebApi/
├── AmGatewayCloud.WebApi.csproj        # Web SDK + YARP + RabbitMQ + SignalR（无数据库依赖）
├── Program.cs                          # YARP + SignalR + RabbitMQ + CORS
├── appsettings.json                    # YARP 路由 + RabbitMQ + CORS 配置
├── Configuration/
│   └── WebApiConfig.cs                # CorsOrigins + RabbitMq（无 PostgreSql）
├── Hubs/
│   └── AlarmHub.cs                    # SignalR Hub（JoinFactory / LeaveFactory）
├── Services/
│   ├── AlarmEventSubscriber.cs         # RabbitMQ 订阅 → SignalR 推送（按工厂分组）
│   └── HealthChecks.cs                 # RabbitMQ 健康检查
└── Infrastructure/
    └── RabbitMqConnectionManager.cs   # RabbitMQ 连接管理（含 Queue 声明 + Binding）
```

---

## 6. 配置

### 6.1 AlarmService — appsettings.json

```json
{
  "AlarmService": {
    "TenantId": "default",
    "EvaluationIntervalSeconds": 5,
    "EvaluationLookbackSeconds": 30,
    "MaxConsecutiveErrors": 10,
    "RuleCacheRefreshSeconds": 30,
    "MaxQueryWindowHours": 1,
    "DeviceOfflineThresholdMinutes": 10,
    "TimescaleDb": {
      "Host": "localhost",
      "Port": 5432,
      "Database": "amgateway_timeseries",
      "Username": "postgres",
      "Password": "postgres",
      "SslMode": "Disable"
    },
    "PostgreSql": {
      "Host": "localhost",
      "Port": 5432,
      "Database": "amgateway_business",
      "Username": "sa",
      "Password": "sa",
      "SslMode": "Disable"
    },
    "RabbitMq": {
      "HostName": "localhost",
      "Port": 5672,
      "UseSsl": false,
      "VirtualHost": "/business",
      "Username": "guest",
      "Password": "guest",
      "Exchange": "amgateway.alarms",
      "ReconnectDelayMs": 5000,
      "MaxReconnectDelayMs": 60000
    }
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:5001" }
    }
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.AspNetCore": "Warning",
        "RabbitMQ": "Warning"
      }
    },
    "WriteTo": [{ "Name": "Console" }]
  }
}
```

### 6.2 WebApi — appsettings.json

```json
{
  "WebApi": {
    "CorsOrigins": ["http://localhost:5173"],
    "RabbitMq": {
      "HostName": "localhost",
      "Port": 5672,
      "UseSsl": false,
      "VirtualHost": "/business",
      "Username": "guest",
      "Password": "guest",
      "Exchange": "amgateway.alarms",
      "QueueName": "amgateway.alarm-notifications",
      "ReconnectDelayMs": 5000,
      "MaxReconnectDelayMs": 60000
    }
  },
  "ReverseProxy": {
    "Clusters": {
      "alarm-service": {
        "Destinations": {
          "default": { "Address": "http://localhost:5001" }
        }
      }
    },
    "Routes": {
      "alarm-rules-route": {
        "ClusterId": "alarm-service",
        "Match": { "Path": "/api/alarmrules/{**catch-all}" }
      },
      "alarms-route": {
        "ClusterId": "alarm-service",
        "Match": { "Path": "/api/alarms/{**catch-all}" }
      },
      "factories-route": {
        "ClusterId": "alarm-service",
        "Match": { "Path": "/api/factories/{**catch-all}" }
      }
    }
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.AspNetCore": "Warning",
        "RabbitMQ": "Warning",
        "Yarp": "Information"
      }
    },
    "WriteTo": [{ "Name": "Console" }]
  }
}
```

### 6.3 配置映射类

**AlarmServiceConfig**（AlarmService/Configuration/AlarmServiceConfig.cs）：

```csharp
namespace AmGatewayCloud.AlarmService.Configuration;

public class AlarmServiceConfig
{
    public string TenantId { get; set; } = "default";
    public int EvaluationIntervalSeconds { get; set; } = 5;
    public int EvaluationLookbackSeconds { get; set; } = 30;
    public int MaxConsecutiveErrors { get; set; } = 10;
    public int RuleCacheRefreshSeconds { get; set; } = 30;
    public int MaxQueryWindowHours { get; set; } = 1;
    public int DeviceOfflineThresholdMinutes { get; set; } = 10;
    public TimescaleDbConfig TimescaleDb { get; set; } = new();
    public PostgreSqlConfig PostgreSql { get; set; } = new();
    public RabbitMqConfig RabbitMq { get; set; } = new();
}
```

**WebApiConfig**（WebApi/Configuration/WebApiConfig.cs）：

```csharp
namespace AmGatewayCloud.WebApi.Configuration;

public class WebApiConfig
{
    public string[] CorsOrigins { get; set; } = ["http://localhost:5173"];
    public RabbitMqConfig RabbitMq { get; set; } = new();
}
```

**共享配置**（Shared/Configuration/DatabaseConfigs.cs）：

```csharp
namespace AmGatewayCloud.Shared.Configuration;

public class PostgreSqlConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Disable";
    public string ConnectionString =>
        $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password};SSL Mode={SslMode}";
}

public class TimescaleDbConfig { /* 同 PostgreSqlConfig 结构 */ }

public class RabbitMqConfig
{
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; } = 5672;
    public bool UseSsl { get; set; }
    public string VirtualHost { get; set; } = "/business";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Exchange { get; set; } = "amgateway.alarms";
    public string QueueName { get; set; } = "amgateway.alarm-notifications";
    public int ReconnectDelayMs { get; set; } = 5000;
    public int MaxReconnectDelayMs { get; set; } = 60000;
}
```

---

## 7. 组件设计

### 7.1 AlarmEvaluationHostedService — 报警评估主循环

**职责**：定时触发报警评估周期。

```csharp
public class AlarmEvaluationHostedService : BackgroundService
{
    // 冷启动：查 alarm_events 最后触发时间，或默认 now - lookback
    // 主循环：EvaluateAsync → StaleCheckAsync → Delay
    // 规则缓存：每 RuleCacheRefreshSeconds 秒刷新，加载失败使用上次缓存
    // 逐条数据匹配规则 → Evaluate → 去重 → 写入 → 发布
    // 设备离线检测：超过 DeviceOfflineThresholdMinutes 无数据 → is_stale = true
    // 恢复时重置冷却，允许条件再次满足时立即重新触发
}
```

### 7.2 RuleEvaluator — 规则评估器

**职责**：根据规则对数据点进行阈值判断。

- `Evaluate(point, rule)` → bool：判断数据点是否触发规则
  - 字符串比较（== / !=）：优先使用 `threshold_string`，其次 `threshold.ToString()`
  - 数值比较：支持 `>`, `>=`, `<`, `<=`, `==`（Epsilon）, `!=`
  - 值提取：优先 value_float → value_int → value_bool(0/1)

- `ShouldClear(point, rule)` → bool：判断报警是否应该自动恢复（Deadband 机制）
  - 无 ClearThreshold 时：字符串规则按"不再满足触发条件"恢复；数值规则不恢复
  - 有 ClearThreshold 时：`>` 类回落到 ClearThreshold 以下；`<` 类回升到 ClearThreshold 以上
  - 字符串规则：当前值不再满足触发条件时恢复

- `ExtractValue(point, rule)` → double?：提取数值

### 7.3 CooldownManager — 冷却管理器

**职责**：同一规则+设备在冷却时间内不重复触发。

- 内存字典实现：`ConcurrentDictionary<string, DateTimeOffset>`，key = `{ruleId}:{deviceId}`
- `IsInCooldown()`：检查是否仍在冷却期
- `RecordTrigger()`：记录触发时间
- `ResetCooldown()`：报警恢复时重置冷却，允许立即重新触发
- `Cleanup()`：定期清理过期记录，防止内存泄漏

### 7.4 AlarmRuleService — 规则管理业务

**职责**：规则 CRUD + ClearThreshold 校验。

- `GetRulesAsync()`：查询规则列表，内存过滤 factoryId / tag
- `CreateRuleAsync()`：创建规则，校验 Operator / Level / ClearThreshold 合法性
- `UpdateRuleAsync()`：动态更新规则字段，构建 updates 字典传入 Repository
- `DeleteRuleAsync()`：删除规则，有 Active/Acked 报警时拒绝删除

### 7.5 AlarmQueryService — 报警查询业务

**职责**：报警查询、汇总、趋势、确认、抑制、关闭。

- `GetAlarmsAsync()`：分页查询，支持 factoryId / deviceId / status / level / isStale 过滤
- `GetSummaryAsync(factoryId?)`：报警状态汇总，`SELECT status, COUNT(*) FROM alarm_events GROUP BY status`，支持按工厂过滤
- `GetTrendAsync(hours, factoryId?)`：报警趋势，TimescaleDB `time_bucket('1 hour', triggered_at)` 聚合，返回每小时的报警数量按级别分组
- `GetByIdAsync()`：根据 ID 获取报警事件（含规则名称）
- `AcknowledgeAsync()`：Active → Acked
- `SuppressAsync()`：Active/Acked → Suppressed
- `ClearAsync()`：手动关闭 → Cleared

### 7.6 FactoryService — 工厂/车间查询

**职责**：查询工厂/车间树形结构，供前端侧边栏和工厂选择器使用。

- `GetFactoryTreeAsync()`：查询 `factories` + `workshops` 表，组装树形结构
- 数据来源：CloudGateway 写入的 `factories` / `workshops` 表（同一 `amgateway_business` 数据库）

> 注：AlarmService 已经连接 `amgateway_business` 数据库（用于 alarm_rules / alarm_events），
> 可以直接查询 `factories` / `workshops` 表，无需额外数据库连接。

### 7.7 AlarmEventSubscriber — WebApi 侧订阅

**职责**：WebApi 订阅 RabbitMQ 报警事件，推送到 SignalR Hub。

- 有 FactoryId 时：按工厂分组推送 `Clients.Group("factory-{factoryId}")`
- 无 FactoryId 时：广播 `Clients.All`
- 不做任何数据库写入

### 7.8 AlarmHub — SignalR Hub

**职责**：向前端实时推送报警事件。

- `JoinFactory(factoryId)`：加入工厂分组
- `LeaveFactory(factoryId)`：离开工厂分组

---

## 8. RabbitMQ 拓扑

### 8.1 /business vhost

```
Exchange: amgateway.alarms (Topic, durable)

Queue: amgateway.alarm-notifications (durable)
  Binding: alarm.#  (WebApi 订阅，接收所有报警)

RoutingKey 格式: alarm.{tenantId}.{factoryId}.{level}

示例:
  alarm.default.factory-a.warning    → Warning 级别报警
  alarm.default.factory-a.critical   → Critical 级别报警
```

### 8.2 与管道层隔离

```
RabbitMQ 实例
├── vhost: /pipeline                 ← 管道层（Phase 1-3 已有）
│   ├── Exchange: amgateway.topic
│   └── Queue: amgateway.factory-a / factory-b
│
└── vhost: /business                 ← 业务层（Phase 4 新增）
    ├── Exchange: amgateway.alarms
    └── Queue: amgateway.alarm-notifications
```

---

## 9. 默认报警规则种子数据

| ID | 名称 | Tag | Operator | Threshold | ClearThreshold | Level | Cooldown |
|----|------|-----|----------|-----------|----------------|-------|----------|
| high-temp-warning | 高温警告 | temperature | > | 28 | 26 | Warning | 5 |
| high-temp-critical | 高温严重 | temperature | > | 35 | 30 | Critical | 5 |
| low-temp-warning | 低温警告 | temperature | < | 18 | 20 | Warning | 5 |
| high-pressure-warning | 高压警告 | pressure | > | 115 | 110 | Warning | 5 |
| low-pressure-warning | 低压警告 | pressure | < | 85 | 90 | Warning | 5 |
| high-level-warning | 液位过高 | level | > | 90 | 85 | Warning | 5 |
| high-level-critical | 液位严重 | level | > | 95 | 90 | Critical | 5 |
| low-level-warning | 液位过低 | level | < | 10 | 15 | Warning | 5 |
| high-voltage-warning | 电压过高 | voltage | > | 395 | 390 | Warning | 5 |
| low-voltage-warning | 电压过低 | voltage | < | 360 | 365 | Warning | 5 |
| high-current-warning | 电流过大 | current | > | 18 | 15 | Warning | 5 |
| high-rpm-warning | 转速过高 | rpm | > | 1650 | 1600 | Warning | 5 |
| high-humidity-warning | 湿度过高 | humidity | > | 85 | 80 | Warning | 5 |
| freq-abnormal-warning | 频率异常 | frequency | > | 50.8 | 50.5 | Warning | 5 |
| device-alarm | 设备报警 | diAlarm | == | 1 | 0 | Warning | 2 |
| quality-bad | 数据质量差 | quality | == | 0 (ThresholdString="Bad") | — | Warning | 10 |

种子数据在 AlarmService 首次启动时通过 `AlarmDbInitializer` 自动插入（`ON CONFLICT DO NOTHING`）。

---

## 10. 部署

### 10.1 Dockerfile — AlarmService

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj ./AmGatewayCloud.Shared/
RUN dotnet restore ./AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj
COPY src/AmGatewayCloud.AlarmService/AmGatewayCloud.AlarmService.csproj ./AmGatewayCloud.AlarmService/
RUN dotnet restore ./AmGatewayCloud.AlarmService/AmGatewayCloud.AlarmService.csproj
COPY src/AmGatewayCloud.Shared/ ./AmGatewayCloud.Shared/
COPY src/AmGatewayCloud.AlarmService/ ./AmGatewayCloud.AlarmService/
RUN dotnet publish ./AmGatewayCloud.AlarmService/AmGatewayCloud.AlarmService.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5001
ENTRYPOINT ["dotnet", "AmGatewayCloud.AlarmService.dll"]
```

### 10.2 Dockerfile — WebApi

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj ./AmGatewayCloud.Shared/
RUN dotnet restore ./AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj
COPY src/AmGatewayCloud.WebApi/AmGatewayCloud.WebApi.csproj ./AmGatewayCloud.WebApi/
RUN dotnet restore ./AmGatewayCloud.WebApi/AmGatewayCloud.WebApi.csproj
COPY src/AmGatewayCloud.Shared/ ./AmGatewayCloud.Shared/
COPY src/AmGatewayCloud.WebApi/ ./AmGatewayCloud.WebApi/
RUN dotnet publish ./AmGatewayCloud.WebApi/AmGatewayCloud.WebApi.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "AmGatewayCloud.WebApi.dll"]
```

### 10.3 docker-compose（新增服务）

```yaml
  alarm-service:
    build:
      context: .
      dockerfile: docker/alarm-service/Dockerfile
    container_name: amgw-alarm-service
    ports:
      - "5001:5001"
    depends_on:
      rabbitmq-init:
        condition: service_completed_successfully
      timescaledb:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      AlarmService__TenantId: default
      AlarmService__EvaluationIntervalSeconds: "5"
      AlarmService__TimescaleDb__Host: timescaledb
      AlarmService__PostgreSql__Host: timescaledb
      AlarmService__RabbitMq__HostName: rabbitmq
    restart: unless-stopped

  webapi:
    build:
      context: .
      dockerfile: docker/webapi/Dockerfile
    container_name: amgw-webapi
    ports:
      - "8080:8080"
    depends_on:
      alarm-service:
        condition: service_started
      rabbitmq-init:
        condition: service_completed_successfully
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      WebApi__RabbitMq__HostName: rabbitmq
      ReverseProxy__Clusters__alarm-service__Destinations__default__Address: "http://alarm-service:5001"
    restart: unless-stopped
```

---

## 11. 错误处理策略

| 场景 | 处理 |
|------|------|
| TimescaleDB 查询失败 | 记录错误日志，跳过本次评估周期，不更新 lastEvalTime |
| PostgreSQL 写入失败 | 记录错误日志，下次评估时重试（报警事件不丢） |
| PostgreSQL 唯一约束冲突 | 并发插入重复 Active 报警时，捕获异常忽略，说明另一实例已创建 |
| RabbitMQ 发布失败 | 记录错误日志，报警数据已在 PostgreSQL，WebApi 可通过查询 API 获取 |
| 规则加载失败 | 使用上次缓存的规则继续评估 |
| 连续错误超过阈值 | 记录 Critical 日志，健康检查返回 Unhealthy |
| 冷启动（无 lastEvalTime） | 默认查最近 `EvaluationLookbackSeconds` 秒数据（默认 30s） |
| 数据窗口过大（宕机恢复后） | 查询时间限制在 `MaxQueryWindowHours`（默认1小时），超期数据丢弃 |
| 设备离线（无新数据） | Active/Acked/Suppressed 报警标记 `is_stale = true`，保持报警不自动恢复 |
| UpdateRuleAsync 非法列名 | 白名单校验，抛出 ArgumentException |
| 删除规则时有活跃报警 | 拒绝删除，返回错误信息 |

---

## 12. 与已有系统的衔接

| 依赖 | 来源 | 说明 |
|------|------|------|
| `device_data` 表 | CloudGateway → TimescaleDB | AlarmService 只读，利用已有索引 |
| `devices` 表 | CloudGateway → PostgreSQL | 报警中记录 device_id 关联 |
| `factories` 表 | CloudGateway → PostgreSQL | 报警事件中记录 factory_id |
| RabbitMQ `/pipeline` vhost | EdgeGateway/CloudGateway | AlarmService 不接触，完全隔离 |
| RabbitMQ `/business` vhost | Phase 4 新增 | AlarmService 发布，WebApi 订阅 |

---

## 13. 后续演进

| 阶段 | 变化 |
|------|------|
| Phase 5 | 前端看板：报警汇总 API / 趋势 API / 工厂树 API，Vue 3 前端对接 |
| Phase 5 | AlarmRule 热更新：WebApi 修改规则后通过 RabbitMQ 即时通知 AlarmService |
| Phase 6 | DDD 提炼：Alarm 聚合根、领域事件；WorkOrder 自动从报警生成 |
| Phase 7 | OpenTelemetry：报警评估链路追踪 |
| Phase 7 | 报警趋势增强：TimescaleDB 连续聚合报警频率 |
| Phase 8 | 多租户完善：JWT 中间件，API 按 TenantId 过滤 |
| Phase 8 | 延迟确认（DelaySeconds）评估逻辑实现 |
| Phase 8 | 变化率报警（1分钟内温度变化超过10°） |
