#!/bin/bash
# Publish a test DataBatch message to a CloudGateway queue via RabbitMQ Management API.
# Usage: ./publish-test-message.sh [queue-name] [batch-count]
#
# Examples:
#   ./publish-test-message.sh                           # 1 message to factory-a
#   ./publish-test-message.sh amgateway.factory-a 5      # 5 messages to factory-a
#   ./publish-test-message.sh amgateway.factory-b 100    # 100 messages to factory-b

QUEUE="${1:-amgateway.factory-a}"
COUNT="${2:-1}"
API="http://localhost:15672/api"

FACTORY_ID="${QUEUE#amgateway.}"  # strip prefix to get factory-a / factory-b

for ((i=1; i<=COUNT; i++)); do
  BATCH_ID="$(date +%s%N | sha256sum | head -c 32 | python3 -c 'import sys; s=sys.stdin.read().strip(); print(f"{s[0:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:32]}")' 2>/dev/null || echo "$(date +%s)-$RANDOM-$i")"
  TS="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

  curl -s -u guest:guest \
    -H "Content-Type: application/json" \
    -X POST "$API/exchanges/%2F/amq.default/publish" \
    -d "{
      \"properties\": {\"content_type\": \"application/json\"},
      \"routing_key\": \"$QUEUE\",
      \"payload\": \"{\\\"BatchId\\\":\\\"$BATCH_ID\\\",\\\"TenantId\\\":\\\"default\\\",\\\"FactoryId\\\":\\\"$FACTORY_ID\\\",\\\"WorkshopId\\\":\\\"ws-001\\\",\\\"DeviceId\\\":\\\"dev-001\\\",\\\"Source\\\":\\\"192.168.1.100\\\",\\\"CollectedAt\\\":\\\"$TS\\\",\\\"Protocol\\\":\\\"opcua\\\",\\\"DeviceTimestamp\\\":\\\"$TS\\\",\\\"DataPoints\\\":[{\\\"Tag\\\":\\\"Temperature\\\",\\\"Value\\\":25.5,\\\"Quality\\\":\\\"Good\\\",\\\"Timestamp\\\":\\\"$TS\\\"},{\\\"Tag\\\":\\\"Pressure\\\",\\\"Value\\\":101.3,\\\"Quality\\\":\\\"Good\\\",\\\"Timestamp\\\":\\\"$TS\\\"}]}\",
      \"payload_encoding\": \"string\"
    }" > /dev/null

  echo "[$i/$COUNT] Published batch $BATCH_ID to $QUEUE"
done

echo "Done. Check: http://localhost:15672/#/queues  or query TimescaleDB."
