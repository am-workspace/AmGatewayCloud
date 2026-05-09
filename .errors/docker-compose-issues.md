# Docker Compose 组合排错记录

## 1. rabbitmq:management 镜像无法拉取

**现象**：`docker compose up -d` 报错 `dialing registry-1.docker.io:443: connectex: No connection could be made`

**原因**：Docker Desktop 配置的代理 `http.docker.internal:3128` 不通，无法访问 Docker Hub。

**修复**：本地已有 `rabbitmq:latest`（391MB），改用本地镜像，启动时通过 command 启用 management 插件：
```yaml
image: rabbitmq:latest
command: ["sh", "-c", "rabbitmq-plugins enable --offline rabbitmq_management && exec rabbitmq-server"]
```

---

## 2. InfluxDB 容器无法启动（dasel 配置解析错误）

**现象**：`influxdb:latest`（v2.9.0, 2026-05-01 构建）启动后立即退出（exit code 1），日志报：
```
dasel: error: map key not found: "http-bind-address"
dasel: error: map key not found: "tls-cert"
```

**原因**：InfluxDB v2.9.0 的 Docker entrypoint 脚本使用 `dasel` 工具合并配置文件，但默认 base config 为空，dasel 查找预期 key 失败导致脚本退出。这是 v2.9.0 镜像的已知 bug（镜像 5 天前构建）。

**修复**：绕过官方 entrypoint，直接启动 `influxd`，通过自定义脚本完成初始化：
```yaml
influxdb:
  image: influxdb:latest
  entrypoint: ["/bin/sh", "/docker-entrypoint-initdb.d/entrypoint.sh"]
  volumes:
    - ./docker/influxdb/entrypoint.sh:/docker-entrypoint-initdb.d/entrypoint.sh:ro
```

自定义 entrypoint.sh：
```sh
#!/bin/sh
/usr/local/bin/influxd &
INFLUXD_PID=$!

# 等待就绪
for i in $(seq 1 30); do
    if /usr/local/bin/influx ping 2>/dev/null; then break; fi
    sleep 1
done

# 幂等初始化（已初始化则跳过）
/usr/local/bin/influx setup \
    --force --username admin --password admin123456 \
    --org amgateway --bucket edge-data \
    --token dev-token-amgateway-edge --retention 0 \
    2>/dev/null

wait $INFLUXD_PID
```

**注意**：InfluxDB v2.9.0 服务本身正常工作，API 与 v2.7 完全兼容。EdgeGateway 使用的 `InfluxDB.Client` SDK 不受影响。

---

## 3. Docker 网络限制（全局问题）

**现象**：Docker pull、git push 均失败，无法访问外网。

**原因**：Docker Desktop 配置的代理 `http.docker.internal:3128` 和 `hubproxy.docker.internal:5555` 不通。

**影响**：
- 无法拉取新镜像（需依赖本地已有镜像）
- 无法推送 Git 提交

**当前本地可用镜像**：
| 镜像 | 版本 | 大小 |
|------|------|------|
| timescale/timescaledb | latest-pg17 | 1.48GB |
| rabbitmq | latest | 391MB |
| influxdb | latest (v2.9.0) | 404MB |
| eclipse-mosquitto | latest | 35.9MB |
| redis | latest | 204MB |
| postgres | latest | 649MB |
| nginx | latest | 240MB |
| emqx | latest | 364MB |
| node | 24-slim | 325MB |
| caddy | latest | 87.9MB |

---

## 最终组合

```yaml
services:
  mosquitto:     # MQTT Broker (1883)         → 采集器入口
  influxdb:      # 本地时序库 (8086)           → EdgeGateway 缓存
  rabbitmq:      # 消息队列 (5672/15672)       → Edge→Cloud 传输
  timescaledb:   # 云端时序+业务库 (5432)       → CloudGateway 写入
```
