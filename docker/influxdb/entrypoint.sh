#!/bin/sh
# Start influxd, wait for it, run one-time setup if needed, then keep running.

/usr/local/bin/influxd &
INFLUXD_PID=$!

# Wait for influxd to be ready
for i in $(seq 1 30); do
    if /usr/local/bin/influx ping 2>/dev/null; then
        break
    fi
    sleep 1
done

# Setup (no-op if already initialized)
/usr/local/bin/influx setup \
    --force \
    --username admin \
    --password admin123456 \
    --org amgateway \
    --bucket edge-data \
    --token dev-token-amgateway-edge \
    --retention 0 \
    2>/dev/null

# Bring influxd to foreground
wait $INFLUXD_PID
