# Create queue topology and/or publish test DataBatch messages to a CloudGateway queue.
# Usage:
#   .\scripts\publish-test-message.ps1                           # setup topology + 1 message to factory-a
#   .\scripts\publish-test-message.ps1 amgateway.factory-a 10    # 10 messages to factory-a
#   .\scripts\publish-test-message.ps1 -SetupOnly                # just create topology, no messages
#   .\scripts\publish-test-message.ps1 -SkipSetup                # skip topology, just publish

param(
    [string]$Queue = "amgateway.factory-a",
    [int]$Count = 1,
    [switch]$SetupOnly,
    [switch]$SkipSetup,
    [switch]$Purge
)

$api = "http://localhost:15672/api"
$auth = "Basic " + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("guest:guest"))
$headers = @{ Authorization = $auth }

function Invoke-RabbitApi {
    param([string]$Method, [string]$Uri, $Body)
    try {
        $params = @{ Uri = $Uri; Method = $Method; Headers = $headers; TimeoutSec = 5 }
        if ($Body) { $params.Body = ($Body | ConvertTo-Json -Depth 5); $params.ContentType = "application/json" }
        $null = Invoke-RestMethod @params
        return $true
    } catch {
        if ($_.Exception.Response.StatusCode -eq 409) { return $true }  # already exists, ok
        Write-Host "  ERROR: $_" -ForegroundColor Red
        return $false
    }
}

# ── Setup topology (idempotent) ──
if (-not $SkipSetup) {
    $dlxName = "dlx.$Queue"
    $dlqName = "dlq.$Queue"
    $routingKey = "dlx.$Queue"

    Write-Host "=== Setting up topology for $Queue ==="

    # 1. DLX exchange (topic, durable)
    $ok = Invoke-RabbitApi Put "$api/exchanges/%2F/$dlxName" @{ type = "topic"; durable = $true }
    if ($ok) { Write-Host "  DLX exchange: $dlxName" }

    # 2. DLQ queue (durable)
    $ok = Invoke-RabbitApi Put "$api/queues/%2F/$dlqName" @{ durable = $true; auto_delete = $false }
    if ($ok) { Write-Host "  DLQ queue: $dlqName" }

    # 3. Bind DLQ to DLX
    $ok = Invoke-RabbitApi Post "$api/bindings/%2F/e/$dlxName/q/$dlqName" @{ routing_key = $routingKey }
    if ($ok) { Write-Host "  Binding: $dlqName --[$routingKey]--> $dlxName" }

    # 4. Main queue (durable, with DLX args)
    $mainArgs = @{
        "x-dead-letter-exchange" = $dlxName
        "x-dead-letter-routing-key" = $routingKey
    }
    $ok = Invoke-RabbitApi Put "$api/queues/%2F/$Queue" @{
        durable = $true
        auto_delete = $false
        arguments = $mainArgs
    }
    if ($ok) { Write-Host "  Main queue: $Queue (DLX -> $dlxName)" }

    Write-Host "=== Topology ready. ==="
    Write-Host ""
}

# ── Purge queue ──
if ($Purge) {
    Write-Host "Purging $Queue ..."
    try {
        $null = Invoke-RestMethod -Uri "$api/queues/%2F/$Queue/contents" -Method Delete -Headers $headers
        Write-Host "  Queue purged."
    } catch {
        Write-Host "  Purge failed: $_" -ForegroundColor Red
    }
    Write-Host ""
}

# ── Publish messages ──
if (-not $SetupOnly) {
    $factoryId = $Queue -replace "^amgateway\.", ""

    $body = @{
        properties       = @{ content_type = "application/json" }
        routing_key      = $Queue
        payload          = ""
        payload_encoding = "string"
    }

    for ($i = 1; $i -le $Count; $i++) {
        $batchId = [guid]::NewGuid().ToString()
        $ts = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

        $payload = @{
            batchId         = $batchId
            tenantId        = "default"
            factoryId       = $factoryId
            workshopId      = "ws-001"
            deviceId        = "dev-001"
            protocol        = "opcua"
            timestamp       = $ts
            points          = @(
                @{ tag = "Temperature"; value = 25.5; valueType = "float"; quality = "Good"; timestamp = $ts; groupName = $null },
                @{ tag = "Pressure";    value = 101.3; valueType = "float"; quality = "Good"; timestamp = $ts; groupName = $null }
            )
        }

        $body.payload = $payload | ConvertTo-Json -Depth 4 -Compress

        try {
            $null = Invoke-RestMethod -Uri "$api/exchanges/%2F/amq.default/publish" `
                -Method Post `
                -Headers $headers `
                -ContentType "application/json" `
                -Body ($body | ConvertTo-Json -Depth 4) `
                -TimeoutSec 5
            Write-Host "[$i/$Count] Published $batchId to $Queue"
        } catch {
            Write-Host "[$i/$Count] FAILED: $_" -ForegroundColor Red
        }
    }

    Write-Host "`nDone. Check: http://localhost:15672/#/queues  or query TimescaleDB."
}
