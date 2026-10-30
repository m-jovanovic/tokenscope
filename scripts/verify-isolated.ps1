[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [ValidateSet('tokenscope','Database')][string]$ConnectionStringName='tokenscope'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSuffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$taskDatabase = "tokenscope_verify_$taskSuffix"
$taskContainer = "tokenscope-verify-$taskSuffix"
$taskDbCreated = $false
$taskAppCreated = $false
Push-Location $taskRoot
try {
    docker compose up -d --wait timescaledb
    if ($LASTEXITCODE -ne 0) { throw 'Database startup failed.' }
    if (-not $SkipBuild) {
        docker compose build app
        if ($LASTEXITCODE -ne 0) { throw 'Application image build failed.' }
    }
    $taskDbContainer = docker compose ps -q timescaledb
    $taskImage = (docker compose config --format json | ConvertFrom-Json).services.app.image
    $taskInspect = docker inspect $taskDbContainer | ConvertFrom-Json
    $taskNetwork = $taskInspect[0].NetworkSettings.Networks.PSObject.Properties.Name | Select-Object -First 1
    docker exec $taskDbContainer psql -U postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $taskDatabase"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create isolated verification database.' }
    $taskDbCreated = $true
    docker run -d --name $taskContainer --network $taskNetwork -p '127.0.0.1::8080' `
        -e "ConnectionStrings__${ConnectionStringName}=Host=timescaledb;Port=5432;Database=$taskDatabase;Username=postgres;Password=postgres" `
        $taskImage | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Isolated app failed to start.' }
    $taskAppCreated = $true
    $taskPort = (docker port $taskContainer 8080/tcp).Trim().Split(':')[-1]
    $taskUrl = "http://127.0.0.1:$taskPort"
    $taskReady = $false
    for ($taskAttempt = 0; $taskAttempt -lt 30; $taskAttempt++) {
        try {
            if ((Invoke-RestMethod -NoProxy -Uri "$taskUrl/health" -TimeoutSec 2).status -eq 'healthy') { $taskReady = $true; break }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $taskReady) { docker logs $taskContainer; throw 'Isolated app did not become healthy.' }
    & "$PSScriptRoot/verify.ps1" -BaseUrl $taskUrl -IncludeFixtureImport -IncludeDemoPulse

    # Synthetic OTLP is sent only to this disposable test database.
    $taskNow = [DateTimeOffset]::UtcNow
    $taskRecord = @{
        timeUnixNano = ($taskNow.ToUnixTimeMilliseconds() * 1000000L).ToString()
        body = @{ stringValue = 'claude_code.api_request' }
        attributes = @(
            @{ key = 'event.id'; value = @{ stringValue = "fixture-$taskSuffix" } },
            @{ key = 'session.id'; value = @{ stringValue = 'synthetic-verification-session' } },
            @{ key = 'model'; value = @{ stringValue = 'claude-sonnet-4-5' } },
            @{ key = 'cost_usd_micros'; value = @{ intValue = '15000' } },
            @{ key = 'input_tokens'; value = @{ intValue = '1000' } },
            @{ key = 'output_tokens'; value = @{ intValue = '800' } }
        )
    }
    $taskPayload = @{ resourceLogs = @(@{ scopeLogs = @(@{ logRecords = @($taskRecord) }) }) } | ConvertTo-Json -Depth 20
    1..2 | ForEach-Object { Invoke-RestMethod -NoProxy -Method Post -Uri "$taskUrl/v1/logs" -ContentType 'application/json' -Body $taskPayload | Out-Null }
    for ($taskAttempt = 0; $taskAttempt -lt 40; $taskAttempt++) {
        $taskState = Invoke-RestMethod -NoProxy -Uri "$taskUrl/api/status"
        if ($taskState.ingestion.persisted -eq 1 -and $taskState.ingestion.duplicates -eq 1) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($taskState.ingestion.persisted -ne 1 -or $taskState.ingestion.duplicates -ne 1) { throw 'OTLP replay protection failed.' }
    $taskLive = Invoke-RestMethod -NoProxy -Uri "$taskUrl/api/summary?source=live&range=1h"
    if ($taskLive.totals.requests -ne 1 -or $taskLive.totals.costUsdMicros -ne 15000) { throw 'OTLP request/cost totals differ.' }
    $taskInvalid = Invoke-WebRequest -NoProxy -Method Post -Uri "$taskUrl/v1/logs" -ContentType 'application/json' -Body '{' -SkipHttpErrorCheck
    if ($taskInvalid.StatusCode -ne 400) { throw 'Malformed JSON should return 400.' }
    $taskForeign = Invoke-WebRequest -NoProxy -Method Post -Uri "$taskUrl/api/demo/pulse" -Headers @{ Origin = 'https://example.com' } -SkipHttpErrorCheck
    if ($taskForeign.StatusCode -ne 403) { throw 'Unrelated browser origin should be refused.' }

    # A history request in this minute has no completed hourly/daily bucket to refresh.
    $taskRecent = @(
        @{ type = 'tokenscope_fixture'; synthetic = $true },
        @{ type = 'assistant'; timestamp = [DateTimeOffset]::UtcNow.ToString('o'); sessionId = 'synthetic-current'; requestId = "current-$taskSuffix"; message = @{
            id = "msg-$taskSuffix"; model = 'claude-haiku-4-5'; usage = @{ input_tokens = 100; output_tokens = 50 } } }
    ) | ForEach-Object { $_ | ConvertTo-Json -Depth 10 -Compress }
    $taskPreview = Invoke-RestMethod -NoProxy -Method Post -Uri "$taskUrl/api/history/preview" -ContentType 'application/x-ndjson' -Body ($taskRecent -join "`n")
    $taskImport = Invoke-RestMethod -NoProxy -Method Post -Uri "$taskUrl/api/history/import" -ContentType 'application/json' -Body (@{ previewId = $taskPreview.previewId } | ConvertTo-Json)
    if ($taskImport.inserted -ne 1) { throw 'Current-bucket history import failed.' }
    $taskPatch = Invoke-WebRequest -NoProxy -Uri "$taskUrl/rehearsal/diffs/hypertable.patch" -SkipHttpErrorCheck
    if ($taskPatch.StatusCode -ne 404) { throw 'Private teaching artifacts must not be present in the public image.' }
    Write-Host '6 additional checks passed: OTLP replay, live totals, malformed JSON, origin guard, current-bucket import, private artifact exclusion.'
    Write-Host 'Isolated verification passed. Synthetic events were written only to the disposable database.'
} catch {
    if ($taskAppCreated) { docker logs --tail 100 $taskContainer }
    throw
} finally {
    if ($taskAppCreated) { docker rm -f $taskContainer | Out-Null }
    if ($taskDbCreated) {
        docker exec $taskDbContainer psql -U postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE $taskDatabase WITH (FORCE)" | Out-Null
    }
    Pop-Location
}
