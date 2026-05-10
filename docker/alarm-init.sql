-- ============================================================
-- AmGatewayCloud AlarmService — alarm_rules & alarm_events
-- 在 amgateway_business 数据库中执行
-- ============================================================

-- 1. 报警规则表
CREATE TABLE IF NOT EXISTS alarm_rules (
    id              TEXT PRIMARY KEY,             -- 规则ID，如 "high-temp-critical"
    name            TEXT NOT NULL,                -- 规则名称
    tenant_id       TEXT NOT NULL DEFAULT 'default',
    factory_id      TEXT,                         -- NULL = 全局（所有工厂）
    device_id       TEXT,                         -- NULL = 同工厂所有设备
    tag             TEXT NOT NULL,                -- 测点: "temperature"
    operator        TEXT NOT NULL,                -- 运算符: >, >=, <, <=, ==, !=
    threshold       DOUBLE PRECISION NOT NULL,    -- 触发阈值
    threshold_string TEXT,                        -- 字符串阈值（如 "Bad"，用于 == / != 字符串比较）
    clear_threshold DOUBLE PRECISION,             -- 恢复阈值(Deadband)
    level           TEXT NOT NULL DEFAULT 'Warning',  -- Info/Warning/Critical/Fatal
    cooldown_minutes INT NOT NULL DEFAULT 5,
    delay_seconds   INT NOT NULL DEFAULT 0,       -- 延迟确认(预留)
    enabled         BOOLEAN NOT NULL DEFAULT TRUE,
    description     TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    updated_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_alarm_rules_tag ON alarm_rules (tag, enabled) WHERE enabled = TRUE;
CREATE INDEX IF NOT EXISTS idx_alarm_rules_scope ON alarm_rules (tenant_id, factory_id, device_id);

-- 2. 报警事件表
CREATE TABLE IF NOT EXISTS alarm_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rule_id         TEXT NOT NULL REFERENCES alarm_rules(id),
    tenant_id       TEXT NOT NULL,
    factory_id      TEXT NOT NULL,
    workshop_id     TEXT,
    device_id       TEXT NOT NULL,
    tag             TEXT NOT NULL,
    trigger_value   DOUBLE PRECISION,
    level           TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'Active', -- Active/Acked/Suppressed/Cleared
    is_stale        BOOLEAN NOT NULL DEFAULT FALSE, -- 设备离线标记
    stale_at        TIMESTAMPTZ,
    message         TEXT,
    triggered_at    TIMESTAMPTZ NOT NULL,
    acknowledged_at TIMESTAMPTZ,
    acknowledged_by TEXT,
    suppressed_at   TIMESTAMPTZ,
    suppressed_by   TEXT,
    suppressed_reason TEXT,
    cleared_at      TIMESTAMPTZ,
    clear_value     DOUBLE PRECISION,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_alarm_events_lookup
    ON alarm_events (tenant_id, factory_id, device_id, triggered_at DESC);
CREATE INDEX IF NOT EXISTS idx_alarm_events_status
    ON alarm_events (status, triggered_at DESC) WHERE status IN ('Active', 'Acked', 'Suppressed');
CREATE INDEX IF NOT EXISTS idx_alarm_events_rule_device
    ON alarm_events (rule_id, device_id, status) WHERE status IN ('Active', 'Acked');

-- 多实例安全：同一 (rule_id, device_id) 只允许一条 Active/Acked 报警
CREATE UNIQUE INDEX IF NOT EXISTS idx_alarm_events_active_unique
    ON alarm_events (rule_id, device_id) WHERE status IN ('Active', 'Acked');
