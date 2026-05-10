# AmGatewayCloud.AlarmService + WebApi — 实现状态总览

> 对照 `alarm-service.md` 逐项核对。

**生成时间**：2026-05-10
**最后更新**：2026-05-10（BFF 重构 + 审核修复后）

---

## 1. 完成度概览

```
AlarmService (评估+API微服务)              ████████████████████  12/12  100%
WebApi (纯 BFF: YARP+SignalR)             ████████████████████  10/10  100%
Shared (共享契约库)                         ████████████████████   4/4  100%
部署配置 (Docker + SQL)                    ████████████████████   5/5  100%
整体完成度                                  ████████████████████  31/31  100%
```

---

## 2. 逐章核对（alarm-service.md）

| 章节 | 内容 | 状态 | 实际实现说明 |
|------|------|------|-------------|
| §1 定位 | AlarmService=评估+API，WebApi=纯BFF | ✅ | AlarmService: WebApplication + 评估引擎 + HTTP API；WebApi: YARP 反向代理 + SignalR 推送，零数据库访问 |
| §2 架构 | 双服务 + Shared 共享契约 | ✅ | AlarmService(5001) + WebApi(8080) + Shared 类库，RabbitMQ `/business` vhost 通信 |
| §3 数据流转 | 评估→持久化→MQ→SignalR + YARP转发 | ✅ | TimescaleDB → RuleEvaluator → PostgreSQL → RabbitMQ → WebApi Subscriber → SignalR；前端 REST → WebApi YARP → AlarmService |
| §4.1 AlarmRule | 规则模型 + ThresholdString + 作用域 | ✅ | `AlarmRule.cs` 含 tenant/factory/device 三级作用域 + `ThresholdString` 字符串阈值 |
| §4.2 AlarmEvent | 事件模型 + 唯一约束 | ✅ | `AlarmEvent.cs` + `idx_alarm_events_active_unique` 唯一索引 |
| §4.3 DataPointReadModel | DISTINCT ON 最新数据点 | ✅ | `DataPointReadModel.cs`，TimescaleDbReader 实现 |
| §4.4 AlarmStatus | 四状态枚举 | ✅ | `AlarmStatus` enum |
| §4.5 AlarmEventMessage | RabbitMQ 消息契约 | ✅ | 位于 `Shared/Messages/AlarmEventMessage.cs`，PascalCase 序列化 |
| §5 项目结构 | 三项目目录布局 | ✅ | 完全匹配 spec §5.1/5.2/5.3 |
| §6.1 AlarmEvaluationHostedService | 评估主循环 | ✅ | 327行，含冷启动、规则缓存刷新、StaleCheck、自动恢复、唯一约束冲突捕获 |
| §6.2 RuleEvaluator | 规则评估 + Deadband | ✅ | 数值比较 + 字符串比较（优先使用 `ThresholdString`，兼容 `Threshold.ToString()`） |
| §6.3 CooldownManager | 冷却期管理 | ✅ | ruleId:deviceKey，1h 清理，ResetCooldown 支持 |
| §6.4 TimescaleDbReader | 最新数据点查询 | ✅ | DISTINCT ON + 离线检测 |
| §6.5 AlarmEventPublisher | RabbitMQ 发布 | ✅ | 规则名称缓存（`ConcurrentDictionary`）+ JSON 序列化 + routingKey |
| §6.6 AlarmEventSubscriber | WebApi 侧订阅 → SignalR | ✅ | 按工厂分组推送，无 FactoryId 时广播 All；手动 Ack + Nack(requeue) + 重连 |
| §6.7 AlarmHub | SignalR Hub | ✅ | JoinFactory/LeaveFactory 分组 |
| §7.4 AlarmRuleService | 规则 CRUD + 校验 | ✅ | Operator/Level/ClearThreshold 白名单校验 |
| §7.5 AlarmQueryService | 报警查询/确认/抑制/关闭 | ✅ | 分页 + 过滤 + 状态流转 |
| AlarmsController | 报警 REST API | ✅ | 位于 AlarmService，GET/POST 四端点 |
| AlarmRulesController | 规则 REST API | ✅ | 位于 AlarmService，CRUD 四端点 |
| §8 RabbitMQ 拓扑 | /business vhost + Exchange + Queue | ✅ | docker-compose rabbitmq-init 创建 vhost，WebApi RabbitMqConnectionManager 声明 Exchange+Queue+Binding |
| §9 种子数据 | 16条规则 | ✅ | `quality-bad` 使用 `ThresholdString="Bad"`，字符串比较正确工作 |
| §10 部署 | Dockerfile + docker-compose | ✅ | 统一 `ASPNETCORE_ENVIRONMENT`，WebApi depends_on alarm-service |
| §11 错误处理 | 11种场景 | ✅ | 含 UpdateRuleAsync 白名单校验防 SQL 注入 |
| Shared 契约 | DTOs + Constants + Messages + Config | ✅ | `AlarmConstants.cs`（ValidOperators/ValidLevels/Exchange/Queue）、DTOs 四文件、AlarmEventMessage、DatabaseConfigs |

---

## 3. 边界条件处理表

| 异常场景 | spec 要求 | 状态 | 说明 |
|---------|---------|------|------|
| TimescaleDB 查询失败 | 跳过本次周期，不更新 lastEvalTime | ✅ | try/catch 记录错误，ConsecutiveErrors++ |
| PostgreSQL 写入失败 | 记录错误，下次重试 | ✅ | AlarmEventRepository 完整异常处理 |
| 唯一约束冲突 | 捕获 23505 忽略 | ✅ | `NpgsqlException.SqlState == "23505"` |
| RabbitMQ 发布失败 | 仅记录错误 | ✅ | AlarmEventPublisher catch + log |
| 规则加载失败 | 使用上次缓存 | ✅ | `_ruleCache` + 刷新失败不覆盖 |
| 连续错误超阈值 | Critical 日志 + Unhealthy | ✅ | ConsecutiveErrors 计数 + `/health` 端点暴露 PostgreSQL/RabbitMQ 状态 |
| 冷启动 | 查最近 30s 数据 | ✅ | `EvaluationLookbackSeconds` 配置 |
| 数据窗口过大 | MaxQueryWindowHours 限制 | ✅ | 1小时默认，超期丢弃 |
| 设备离线 | is_stale 标记 | ✅ | `StaleCheckAsync` + `MarkStaleAsync` |
| 时钟偏移 | 使用数据自带时间戳 | ✅ | 所有时间用 DateTimeOffset |
| UpdateRuleAsync 非法列名 | 白名单校验 | ✅ | `AlarmRuleRepository.UpdateRuleAsync` 列名白名单，抛出 ArgumentException |
| 评估循环停滞 | lastEvalTime 监控 | ⚠️ | 字段存在但未暴露到 `/health` 详情（低优先级） |

---

## 4. 已知问题

### 4.1 高优先级（影响功能）— **全部已修复** ✅

| # | 问题 | 修复说明 |
|---|------|---------|
| 1 | ~~`quality-bad` 种子规则无效~~ | ✅ **已修复**：`AlarmRule` 增加 `ThresholdString` 字段，`RuleEvaluator` 优先使用 `ThresholdString` 做字符串比较 |
| 2 | ~~WebApi DI 解析 `PostgreSqlConfig` 失败~~ | ✅ **已修复**：`Program.cs` 中单独注册，`RabbitMqConnectionManager` 改为直接注入 `RabbitMqConfig` |

### 4.2 中优先级（影响健壮性）— **全部已修复** ✅

| # | 问题 | 修复说明 |
|---|------|---------|
| 3 | ~~AlarmRulesController 缺少 Operator/Level 校验~~ | ✅ **已修复**：`AlarmRuleService` 添加白名单校验 |
| 4 | ~~WebApi 缺少健康检查端点~~ | ✅ **已修复**：添加 `HealthChecks.cs`，注册到 `/health` |
| 5 | ~~`AlarmEventPublisher._ruleNameCache` 非线程安全~~ | ✅ **已修复**：改用 `ConcurrentDictionary` |
| 6 | ~~AlarmEventSubscriber 双重 SignalR 推送~~ | ✅ **已修复**：有 FactoryId 时只推分组，无 FactoryId 时才广播 All |
| 7 | ~~AlarmEventRepository 构造函数参数命名 `_logger`~~ | ✅ **已修复**：改为 `logger`，去掉 `this.` |
| 8 | ~~AlarmRuleRepository.UpdateRuleAsync 无列名校验~~ | ✅ **已修复**：加白名单校验，防止 SQL 注入 |
| 9 | ~~环境变量 `DOTNET_ENVIRONMENT` 不统一~~ | ✅ **已修复**：统一为 `ASPNETCORE_ENVIRONMENT`（兼容 `DOTNET_ENVIRONMENT` 回退） |

### 4.3 低优先级（改进建议）

| # | 问题 | 状态 | 建议 |
|---|------|------|------|
| 10 | ~~Program.cs 双重注册配置~~ | ✅ 已修复 | 移除未使用的 `Configure<>()` 注册 |
| 11 | AlarmEventMessage JSON 序列化使用默认 PascalCase | 待定 | 如需与 camelCase 前端对接，添加 `JsonSerializerOptions` |
| 12 | AlarmRulesController 中 `error.Contains("not found")` 硬编码字符串匹配 | 待定 | 改用自定义异常类型或错误码 |
| 13 | 数据库初始化超时硬编码 30 秒 | 待定 | 可配置化 |
| 14 | UpdateAlarmRuleRequest 的 `ClearThreshold: double?` 无法区分"未提供"和"显式设为 null" | 待定 | 如需 PATCH 语义，使用 JsonPatchDocument 或专门的 DTO |

---

## 5. 与方案差异说明

### 5.1 有意差异

| 差异 | 方案 | 实际 | 原因 |
|------|------|------|------|
| RabbitMQ.Client 版本 | 6.* | 6.8.1 | 具体版本锁定 |
| SignalR 包 | Microsoft.AspNetCore.SignalR 1.* | ASP.NET Core 内置 | .NET 10 的 `Microsoft.NET.Sdk.Web` 已包含 |
| Serilog 版本 | 8.* | 9.0.0 | NuGet 最新稳定版 |
| Npgsql 版本 | 9.* | 9.0.3 | 具体版本锁定 |
| Swagger | 未提及 | Swashbuckle.AspNetCore 10.1.7 | 开发环境便利功能 |
| RabbitMQ.Client API | `BasicPublish(exchange, routingKey, body)` | `BasicPublish(exchange, routingKey, mandatory, properties, body)` | 6.8.1 签名要求 `mandatory` 参数 |
| 环境变量 | `DOTNET_ENVIRONMENT` | `ASPNETCORE_ENVIRONMENT`（兼容 `DOTNET_ENVIRONMENT` 回退） | ASP.NET Core 标准 |

### 5.2 实现超规格项

| 项 | 说明 |
|----|------|
| Shared 共享契约库 | DTOs/Constants/Messages/Configuration 独立项目，双服务共享 |
| AlarmConstants | `ValidOperators`、`ValidLevels`、Exchange/Queue 常量集中管理 |
| AlarmDbInitializer | 建表 + 种子数据合并在一个类中 |
| AlarmEvaluationHostedService | 327 行，含冷启动恢复、规则缓存刷新、StaleCheck、自动恢复、唯一约束冲突捕获 |
| AlarmEventSubscriber | 手动 Ack + Nack(requeue) + 重连循环 + 按工厂分组推送 |
| RabbitMQ /business vhost 初始化 | docker-compose 中独立 `rabbitmq-init` 容器，幂等创建 |
| HealthChecks | PostgreSQL + RabbitMQ 健康检查，`/health` 端点 |
| ThresholdString 字段 | `AlarmRule.ThresholdString` 支持字符串阈值 |
| UpdateRuleAsync 白名单 | `AlarmRuleRepository` 列名校验，防止 SQL 注入 |
| WebApi 纯 BFF 架构 | YARP 反向代理 + SignalR，零数据库访问 |

---

## 6. 文件清单

### 6.1 AmGatewayCloud.Shared — 共享契约库

```
src/AmGatewayCloud.Shared/
├── AmGatewayCloud.Shared.csproj          # net10.0 类库，无外部依赖
├── Configuration/
│   └── DatabaseConfigs.cs                # PostgreSqlConfig + TimescaleDbConfig + RabbitMqConfig
├── Constants/
│   └── AlarmConstants.cs                 # ValidOperators、ValidLevels、Exchange/Queue 常量
├── DTOs/
│   ├── AlarmEventDto.cs                  # 报警事件 DTO
│   ├── AlarmRuleDto.cs                   # 报警规则 DTO + ThresholdString
│   ├── AlarmRuleRequests.cs             # Create/Update/Ack/Suppress 请求 DTO
│   └── PagedResult.cs                    # 分页结果泛型
└── Messages/
    └── AlarmEventMessage.cs              # RabbitMQ 报警事件消息契约
```

### 6.2 AmGatewayCloud.AlarmService — 报警业务微服务

```
src/AmGatewayCloud.AlarmService/
├── AmGatewayCloud.AlarmService.csproj    # Web SDK + Npgsql + Dapper + RabbitMQ + Swagger
├── Program.cs                            # WebApplication 入口 + DI + 数据库初始化
├── appsettings.json                      # 默认配置
├── Configuration/
│   └── AlarmServiceConfig.cs             # 评估参数 + 数据库/MQ 配置
├── Controllers/
│   ├── AlarmsController.cs              # 报警查询/确认/抑制/关闭
│   └── AlarmRulesController.cs          # 规则 CRUD
├── Models/
│   ├── AlarmRule.cs                      # 报警规则模型 + ThresholdString
│   ├── AlarmEvent.cs                     # 报警事件模型 + AlarmStatus 枚举
│   └── DataPointReadModel.cs             # 时序数据读取模型
├── Services/
│   ├── AlarmEvaluationHostedService.cs   # 报警评估主循环 (327行)
│   ├── AlarmRuleService.cs               # 规则管理 + Operator/Level/ClearThreshold 校验
│   ├── AlarmQueryService.cs              # 报警查询 + 确认 + 抑制 + 关闭
│   ├── RuleEvaluator.cs                  # 规则评估 + Deadband + ThresholdString
│   ├── CooldownManager.cs               # 冷却管理
│   ├── AlarmEventRepository.cs          # 报警事件仓储 (Dapper + Npgsql)
│   ├── AlarmRuleRepository.cs           # 报警规则仓储 + 白名单校验
│   ├── TimescaleDbReader.cs             # 时序数据读取
│   ├── AlarmEventPublisher.cs            # RabbitMQ 事件发布 + ConcurrentDictionary
│   └── HealthChecks.cs                   # PostgreSQL + RabbitMQ 健康检查
└── Infrastructure/
    ├── AlarmDbInitializer.cs             # 数据库建表 + 种子规则
    └── RabbitMqConnectionManager.cs     # RabbitMQ 连接管理
```

### 6.3 AmGatewayCloud.WebApi — 纯 BFF

```
src/AmGatewayCloud.WebApi/
├── AmGatewayCloud.WebApi.csproj          # Web SDK + YARP + RabbitMQ + SignalR（无数据库依赖）
├── Program.cs                            # YARP + SignalR + RabbitMQ + CORS
├── appsettings.json                      # YARP 路由 + RabbitMQ + CORS 配置
├── Configuration/
│   └── WebApiConfig.cs                   # CorsOrigins + RabbitMq（无 PostgreSql）
├── Hubs/
│   └── AlarmHub.cs                       # SignalR Hub（JoinFactory / LeaveFactory）
├── Services/
│   ├── AlarmEventSubscriber.cs           # RabbitMQ 订阅 → SignalR 推送（按工厂分组）
│   └── HealthChecks.cs                   # RabbitMQ 健康检查
└── Infrastructure/
    └── RabbitMqConnectionManager.cs     # RabbitMQ 连接管理（含 Queue 声明 + Binding）
```

### 6.4 部署配置

```
docker/
├── alarm-service/Dockerfile               # AlarmService 多阶段构建 (ASPNETCORE_ENVIRONMENT)
├── webapi/Dockerfile                      # WebApi 多阶段构建
├── rabbitmq/
│   └── init-business-vhost.sh             # /business vhost 初始化
├── migrations/
│   └── 001_alarm_tables.sql               # SQL migration（含 threshold_string）
├── alarm-init.sql                         # 报警表 DDL（含 threshold_string）
└── init-db.sql                            # 初始化：含 alarm 表 + sa 权限

docker-compose.yml                         # 3 新服务，ASPNETCORE_ENVIRONMENT 统一
```

---

## 7. Spec 文件关系

```
alarm-service.md               ← 完整方案（架构/模型/评估/推送/部署）
alarm-service-status.md        ← 本文件（实现状态/审核/已知问题）
../docker/alarm-init.sql       ← 报警表 DDL（与 AlarmDbInitializer 对应）
```

---

## 8. 审核修复记录

| 日期 | 修复项 | 涉及文件 |
|------|--------|---------|
| 2026-05-10 | #1 `ThresholdString` 字段 — 修复 `quality-bad` 字符串规则 | `AlarmRule.cs`, `RuleEvaluator.cs`, `AlarmDbInitializer.cs`, `AlarmRuleRepository.cs`, `AlarmRuleDto.cs`, `AlarmRuleRequests.cs`, `AlarmRuleService.cs`, 3×SQL |
| 2026-05-10 | #2 WebApi DI 注册 `PostgreSqlConfig`/`RabbitMqConfig` | `Program.cs`, `RabbitMqConnectionManager.cs` |
| 2026-05-10 | #3 `ValidOperators`/`ValidLevels` 白名单校验 | `AlarmRuleService.cs` |
| 2026-05-10 | #4 `/health` 健康检查端点 | `HealthChecks.cs`(新建), `Program.cs` |
| 2026-05-10 | #5 `_ruleNameCache` → `ConcurrentDictionary` | `AlarmEventPublisher.cs` |
| 2026-05-10 | #6 移除双重 `Configure<>()` 注册 | `Program.cs` |
| 2026-05-10 | #7 SignalR 双重推送 → 按工厂分组推送 | `WebApi/Services/AlarmEventSubscriber.cs` |
| 2026-05-10 | #8 构造函数参数命名 `_logger` → `logger` | `AlarmService/Services/AlarmEventRepository.cs` |
| 2026-05-10 | #9 `UpdateRuleAsync` 白名单校验防 SQL 注入 | `AlarmService/Services/AlarmRuleRepository.cs` |
| 2026-05-10 | #10 环境变量统一 `ASPNETCORE_ENVIRONMENT` | `AlarmService/Program.cs`, `docker/alarm-service/Dockerfile`, `docker-compose.yml` |
| 2026-05-10 | #11 清理空目录 | 删除 `WebApi/Controllers/`, `WebApi/DTOs/`, `AlarmService/Contracts/` |

---

## 9. 后续演进路线图

| 阶段 | 需要做的 |
|------|---------|
| **验证** | 端到端集成测试：数据点→评估→报警→MQ→WebApi→SignalR |
| **Phase 5** | DDD 提炼：Alarm 聚合根、领域事件；WorkOrder 自动生成；AlarmRule 热更新 |
| **Phase 6** | OpenTelemetry 链路追踪；报警趋势分析（连续聚合）；多租户 JWT |
| **Phase 7** | 延迟确认（DelaySeconds 预留字段启用）；变化率报警 |
