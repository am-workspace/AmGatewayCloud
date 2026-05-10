-- ============================================================
-- Migration: 在 amgateway_business 数据库中创建 alarm 表
-- 由 TimescaleDB 容器初始化时执行 (via init-db.sql)
-- ============================================================

-- 1. 报警规则表
CREATE TABLE IF NOT EXISTS alarm_rules (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    tenant_id       TEXT NOT NULL DEFAULT 'default',
    factory_id      TEXT,
    device_id       TEXT,
    tag             TEXT NOT NULL,
    operator        TEXT NOT NULL,
    threshold       DOUBLE PRECISION NOT NULL,
    threshold_string TEXT,
    clear_threshold DOUBLE PRECISION,
    level           TEXT NOT NULL DEFAULT 'Warning',
    cooldown_minutes INT NOT NULL DEFAULT 5,
    delay_seconds   INT NOT NULL DEFAULT 0,
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
    status          TEXT NOT NULL DEFAULT 'Active',
    is_stale        BOOLEAN NOT NULL DEFAULT FALSE,
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
