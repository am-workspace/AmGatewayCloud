# AmGatewayCloud.Collector.Modbus — 完整方案

## 1. 定位

独立微服务，负责通过 Modbus TCP 从设备采集数据，输出为统一的 `DataPoint` 格式。

阶段1输出到控制台，阶段3a替换为 RabbitMQ（Collector 本身不变）。

## 2. 架构

```
AmVirtualSlave (:5020, SlaveId=1)         Collector.Modbus
┌───────────────────────────┐           ┌──────────────────────────────────────┐
│                           │           │                                      │
│  Holding 0-9  (传感器)    │  FC03 ──► │                                      │
│  Holding 10-19 (参数)     │  FC03 ──► │  ModbusConnection                    │
│  Holding 20-29 (统计)     │  FC03 ──► │    ├── 连接管理 + 自动重连             │
│  Holding 100-106 (报警)   │  FC03 ──► │    └── 读写隔离（读不干扰从站）         │
│  Discrete 0-6 (开关入)    │  FC02 ──► │         │                             │
│  Coil 0-6    (开关出)     │  FC01 ──► │         ▼                             │
│                           │           │  ModbusCollectorService               │
│                           │           │    ├── BackgroundService 托管          │
│                           │           │    ├── 按配置轮询寄存器组              │
│                           │           │    ├── 原始值 → DataPoint 转换          │
│                           │           │    └── 调用 IDataOutput 输出           │
│                           │           │         │                             │
│                           │           │         ▼                             │
│                           │           │  IDataOutput                         │
│                           │           │    ├── ConsoleDataOutput  (阶段1)     │
│                           │           │    └── RabbitMqOutput    (阶段3a)     │
└───────────────────────────┘           └──────────────────────────────────────┘
```

## 3. 数据模型

### 3.1 DataPoint — 统一输出模型

所有 Collector（Modbus / OPC UA / ...）输出相同格式，下游服务无需关心数据来源。

```csharp
namespace AmGatewayCloud.Collector.Modbus.Models;

public class DataPoint
{
    /// <summary>设备标识，配置中指定</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>数据标签名，如 "temperature", "pressure"</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>采集值（动态类型：int/float/bool/string）</summary>
    public object? Value { get; set; }

    /// <summary>采集时间（UTC）</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>多租户伏笔，阶段6启用</summary>
    public string? TenantId { get; set; }

    /// <summary>扩展属性（如寄存器地址、原始值、质量戳等）</summary>
    public Dictionary<string, object>? Properties { get; set; }
}
```

### 3.2 寄存器值转换规则

| Modbus 寄存器类型 | .NET 类型 | 转换方式 |
|------------------|----------|---------|
| Holding Register | `float` | 原始值 ÷ ScaleFactor（默认 10.0，即 853 → 85.3） |
| Input Register | `float` | 同 Holding |
| Discrete Input | `bool` | 直接映射 |
| Coil | `bool` | 直接映射 |

ScaleFactor 可按寄存器组配置，工业传感器常用 10/100 缩放。

## 4. 配置模型

### 4.1 appsettings.json

```json
{
  "Collector": {
    "DeviceId": "simulator-001",
    "TenantId": "default",
    "PollIntervalMs": 2000,

    "Modbus": {
      "Host": "localhost",
      "Port": 5020,
      "SlaveId": 1,
      "ReconnectIntervalMs": 5000,
      "ReadTimeoutMs": 3000
    },

    "RegisterGroups": [
      {
        "Name": "sensors",
        "Type": "Holding",
        "Start": 0,
        "Count": 10,
        "ScaleFactor": 10.0,
        "Tags": [
          "temperature", "pressure", "flow", "liquidLevel", "humidity",
          "rpm", "voltage", "current", "power", "frequency"
        ]
      },
      {
        "Name": "parameters",
        "Type": "Holding",
        "Start": 10,
        "Count": 10,
        "ScaleFactor": 1.0,
        "Tags": [
          "mode", "noise", "delay", "samplePeriod", "alarmHighLimit",
          "alarmLowLimit", "deviceAddress", "baudRate", "powerFactor", "reserved"
        ]
      },
      {
        "Name": "statistics",
        "Type": "Holding",
        "Start": 20,
        "Count": 10,
        "ScaleFactor": 10.0,
        "Tags": [
          "tempMax", "tempMin", "tempAvg", "pressureStat",
          "runHours", "startCount", "commCount", "errorCount",
          "statReserved1", "statReserved2"
        ]
      },
      {
        "Name": "alarms",
        "Type": "Holding",
        "Start": 100,
        "Count": 7,
        "ScaleFactor": 1.0,
        "Tags": [
          "faultInject", "alarmEnable", "alarmStatus",
          "faultCode1", "faultCode2", "faultCode3", "faultCode4"
        ]
      },
      {
        "Name": "discretes",
        "Type": "Discrete",
        "Start": 0,
        "Count": 7,
        "Tags": [
          "isRunning", "isAlarm", "isOverLimit",
          "isCommOk", "isLocal", "isRemote", "discreteReserved"
        ]
      },
      {
        "Name": "coils",
        "Type": "Coil",
        "Start": 0,
        "Count": 7,
        "Tags": [
          "runControl", "alarmAck", "reset",
          "enable1", "enable2", "enable3", "enable4"
        ]
      }
    ]
  }
}
```

### 4.2 配置映射类

```csharp
namespace AmGatewayCloud.Collector.Modbus.Configuration;

public class CollectorConfig
{
    public string DeviceId { get; set; } = "device-001";
    public string? TenantId { get; set; }
    public int PollIntervalMs { get; set; } = 2000;
    public ModbusConfig Modbus { get; set; } = new();
    public List<RegisterGroupConfig> RegisterGroups { get; set; } = [];
}

public class ModbusConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5020;
    public byte SlaveId { get; set; } = 1;
    public int ReconnectIntervalMs { get; set; } = 5000;
    public int ReadTimeoutMs { get; set; } = 3000;
}

public enum RegisterType
{
    Holding,   // FC03 读
    Input,     // FC04 读
    Discrete,  // FC02 读
    Coil       // FC01 读
}

public class RegisterGroupConfig
{
    public string Name { get; set; } = string.Empty;
    public RegisterType Type { get; set; }
    public ushort Start { get; set; }
    public int Count { get; set; }
    public double ScaleFactor { get; set; } = 1.0;
    public List<string> Tags { get; set; } = [];
}
```

## 5. 组件设计

### 5.1 ModbusConnection — 连接管理

**职责**：建立/维护 Modbus TCP 连接，自动重连。

```
状态机：

  Disconnected ──Connect()──► Connected ──ReadFails──► Reconnecting
       ▲                         │  │                      │
       │                         │  │                      │
       └─────────────────────────┘  └────────── retry ────┘
                                  读取成功，保持 Connected
```

**关键行为**：
- 首次启动自动连接
- 读取失败自动进入重连循环（间隔 ReconnectIntervalMs）
- 重连成功后自动恢复采集
- 线程安全：读写操作加锁，防止并发访问 TcpClient
- 连接/断开/重连均输出结构化日志

```csharp
namespace AmGatewayCloud.Collector.Modbus;

public class ModbusConnection : IDisposable
{
    public bool IsConnected { get; }
    public Task ConnectAsync(CancellationToken ct);
    public Task<ushort[]> ReadHoldingRegistersAsync(ushort start, ushort count, CancellationToken ct);
    public Task<ushort[]> ReadInputRegistersAsync(ushort start, ushort count, CancellationToken ct);
    public Task<bool[]> ReadCoilsAsync(ushort start, ushort count, CancellationToken ct);
    public Task<bool[]> ReadDiscreteInputsAsync(ushort start, ushort count, CancellationToken ct);
}
```

### 5.2 ModbusCollectorService — 采集主循环

**职责**：BackgroundService，按配置轮询所有寄存器组，转换为 DataPoint 输出。

```
主循环：

  ┌─────────────────────────────────────────┐
  │ while (!ct.IsCancellationRequested)      │
  │ {                                        │
  │   foreach (group in RegisterGroups)      │
  │   {                                      │
  │     raw = connection.Read(group)         │
  │     points = ConvertToDataPoints(raw)    │
  │     output.WriteBatchAsync(points)       │
  │   }                                      │
  │   await Task.Delay(PollIntervalMs)       │
  │ }                                        │
  └─────────────────────────────────────────┘
```

**关键行为**：
- 启动时等连接就绪再开始轮询
- 单个寄存器组读取失败不影响其他组（catch + 日志）
- 所有组轮询完后统一 Delay（不是每组各 Delay）
- 优雅关机：CancellationToken 触发后停止轮询，等待当前读取完成

```csharp
namespace AmGatewayCloud.Collector.Modbus;

public class ModbusCollectorService : BackgroundService
{
    public ModbusCollectorService(
        ModbusConnection connection,
        IOptions<CollectorConfig> config,
        IEnumerable<IDataOutput> outputs,
        ILogger<ModbusCollectorService> logger);

    protected override Task ExecuteAsync(CancellationToken ct);
}
```

### 5.3 IDataOutput — 输出抽象

```csharp
namespace AmGatewayCloud.Collector.Modbus.Output;

public interface IDataOutput
{
    /// <summary>输出单条数据</summary>
    Task WriteAsync(DataPoint point, CancellationToken ct);

    /// <summary>批量输出（同一次轮询的所有数据点）</summary>
    Task WriteBatchAsync(IEnumerable<DataPoint> points, CancellationToken ct);
}
```

### 5.4 ConsoleDataOutput — 阶段1实现

```csharp
namespace AmGatewayCloud.Collector.Modbus.Output;

public class ConsoleDataOutput : IDataOutput
{
    // WriteAsync: 格式化单条输出
    // WriteBatchAsync: 一行汇总输出，如：
    //   [20:35:03 INF] [simulator-001] temperature=85.3 pressure=2.2 flow=120.5 ...
}
```

**输出格式设计**：
- 每个寄存器组一条汇总日志（不是每个数据点一行）
- 数值型保留1位小数
- 布尔型显示 true/false
- 前缀带设备ID和时间

## 6. 依赖注入 (Program.cs)

```csharp
var builder = Host.CreateApplicationBuilder(args);

// 配置
builder.Services.Configure<CollectorConfig>(
    builder.Configuration.GetSection("Collector"));

// 核心服务
builder.Services.AddSingleton<ModbusConnection>();
builder.Services.AddSingleton<IDataOutput, ConsoleDataOutput>();

// BackgroundService
builder.Services.AddHostedService<ModbusCollectorService>();

// 日志
builder.Services.AddSerilog();

var host = builder.Build();
await host.RunAsync();
```

## 7. 错误处理策略

| 场景 | 处理 |
|------|------|
| 首次连接失败 | 循环重试，每次间隔 ReconnectIntervalMs |
| 运行中连接断开 | 标记 IsConnected=false，自动重连 |
| 单个寄存器组读取失败 | catch + Warn 日志，跳过该组，继续下一组 |
| 所有寄存器组都失败 | 触发重连 |
| CancellationToken 取消 | 停止轮询，等待当前操作完成，Disconnect |
| Tags 数量与 Count 不匹配 | 启动时校验，不匹配则抛出异常阻止启动 |

## 8. 项目文件

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NModbus" Version="3.0.*" />
    <PackageReference Include="Serilog.AspNetCore" Version="8.*" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
  </ItemGroup>
</Project>
```

## 9. 项目结构

```
src/AmGatewayCloud.Collector.Modbus/
├── AmGatewayCloud.Collector.Modbus.csproj
├── Program.cs                              # 入口 + DI
├── appsettings.json                        # 默认配置
├── ModbusConnection.cs                     # 连接管理 + 自动重连
├── ModbusCollectorService.cs               # BackgroundService 采集主循环
├── Configuration/
│   ├── CollectorConfig.cs                  # 配置映射类
│   └── RegisterGroupConfig.cs              # 寄存器组配置 + RegisterType 枚举
├── Models/
│   └── DataPoint.cs                        # 统一数据模型
└── Output/
    ├── IDataOutput.cs                      # 输出抽象接口
    └── ConsoleDataOutput.cs                # 阶段1：控制台输出
```

## 10. 运行验证

### 前置条件

- AmVirtualSlave 运行在 localhost:5020

### 启动

```powershell
cd src/AmGatewayCloud.Collector.Modbus
dotnet run
```

### 预期输出

```
[20:35:01 INF] Collector starting - Device: simulator-001, Target: localhost:5020/1
[20:35:01 INF] Connected to Modbus slave localhost:5020
[20:35:01 INF] Polling 6 register groups every 2000ms
[20:35:03 INF] [simulator-001] sensors: temperature=85.3 pressure=2.2 flow=120.5 liquidLevel=50.1 humidity=62.8 rpm=1485.0 voltage=380.2 current=5.3 power=2.0 frequency=50.0
[20:35:03 INF] [simulator-001] parameters: mode=1 noise=1.0 delay=0 samplePeriod=2000 ...
[20:35:03 INF] [simulator-001] statistics: tempMax=92.1 tempMin=78.5 tempAvg=85.3 ...
[20:35:03 INF] [simulator-001] alarms: faultInject=0 alarmEnable=1 alarmStatus=0 ...
[20:35:03 INF] [simulator-001] discretes: isRunning=true isAlarm=false isOverLimit=false ...
[20:35:03 INF] [simulator-001] coils: runControl=true alarmAck=false reset=false ...
[20:35:05 INF] [simulator-001] sensors: temperature=86.1 pressure=2.1 flow=119.8 ...
...
```

### 断线恢复验证

1. 停掉 AmVirtualSlave
2. 预期看到 `[WRN] Failed to read register group "sensors": connection lost`
3. 预期看到 `[INF] Reconnecting in 5000ms...`
4. 重启 AmVirtualSlave
5. 预期看到 `[INF] Reconnected to Modbus slave localhost:5020`
6. 数据恢复输出

## 11. 后续演进

| 阶段 | 变化 |
|------|------|
| 阶段3a | 新增 `RabbitMqOutput : IDataOutput`，Collector 不改 |
| 阶段5 | 容器化，Serilog → Seq，加入 OpenTelemetry |
| 阶段6 | 配置中心下发 TenantId，Collector 动态加载 |
| 通用化 | 抽取 `AmGatewayCloud.Collector.Abstractions`（DataPoint + IDataOutput），OPC UA Collector 共享 |
