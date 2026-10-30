[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSuffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$taskDatabase = "tokenscope_verify_$taskSuffix"
$taskContainer = "tokenscope-local-verify-$taskSuffix"
$taskFixtures = Join-Path $taskRoot ".artifacts/local-usage-test/$taskSuffix"
$taskClaudeDirectory = Join-Path $taskFixtures 'claude'
$taskDbCreated = $false
$taskAppCreated = $false
$taskChecks = 0
$taskFixtureTime = [DateTimeOffset]::UtcNow.AddDays(-1)
# Keep a seventh fractional digit to exercise .NET/JSON/Postgres timestamp precision.
$taskYesterday = $taskFixtureTime.AddTicks(7 - ($taskFixtureTime.Ticks % 10)).ToString('o')
function Assert-Local([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAILED: $Name" }
    $script:taskChecks++
    Write-Host "PASS $Name"
}
function Claude-Record([string]$Id, [int]$Output, [string]$At = $taskYesterday) {
    @{ type='assistant'; timestamp=$At; sessionId='synthetic-local-session'; requestId=$Id; message=@{
        id="message-$Id"; model='claude-haiku-4-5'; usage=@{ input_tokens=100; output_tokens=$Output; cache_read_input_tokens=0; cache_creation_input_tokens=0 }
    } } | ConvertTo-Json -Depth 10 -Compress
}
function Append-Line([string]$Path, [string]$Line) { [IO.File]::AppendAllText($Path, $Line + "`n", [Text.UTF8Encoding]::new($false)) }
function Get-LocalSummary([string]$Source) { Invoke-RestMethod -NoProxy -TimeoutSec 10 -Uri "$taskUrl/api/summary?source=$Source&range=90d" }
function Wait-Local([scriptblock]$Condition, [string]$Name) {
    $taskWaitUntil = [DateTime]::UtcNow.AddSeconds(90)
    do {
        try { if (& $Condition) { return } } catch { }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $taskWaitUntil)
    throw "Timed out: $Name"
}
function Trigger-Sync { Invoke-RestMethod -NoProxy -TimeoutSec 10 -Method Post -Uri "$taskUrl/api/local-usage/sync" | Out-Null }
Push-Location $taskRoot
try {
    New-Item -ItemType Directory -Force -Path $taskClaudeDirectory | Out-Null
    $taskClaudeFile = Join-Path $taskClaudeDirectory 'synthetic.jsonl'
    Append-Line $taskClaudeFile (Claude-Record 'claude-1' 10)
    docker compose up -d --wait timescaledb
    if ($LASTEXITCODE -ne 0) { throw 'Database startup failed.' }
    if (-not $SkipBuild) { docker compose build app; if ($LASTEXITCODE -ne 0) { throw 'Image build failed.' } }
    $taskDbContainer = docker compose ps -q timescaledb
    $taskImage = (docker compose config --format json | ConvertFrom-Json).services.app.image
    $taskInspection = docker inspect $taskDbContainer | ConvertFrom-Json
    $taskNetwork = $taskInspection[0].NetworkSettings.Networks.PSObject.Properties.Name | Select-Object -First 1
    docker exec $taskDbContainer psql -U postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $taskDatabase" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Isolated database creation failed.' }
    $taskDbCreated = $true
    docker run -d --name $taskContainer --network $taskNetwork -p '127.0.0.1::8080' `
        -e "ConnectionStrings__tokenscope=Host=timescaledb;Port=5432;Database=$taskDatabase;Username=postgres;Password=postgres" `
        -e 'LocalUsage__Enabled=true' -e 'LocalUsage__ClaudePath=/usage/claude' `
        --mount "type=bind,source=$taskClaudeDirectory,target=/usage/claude,readonly" $taskImage | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Isolated app startup failed.' }
    $taskAppCreated = $true
    $taskPort = (docker port $taskContainer 8080/tcp).Trim().Split(':')[-1]
    $taskUrl = "http://127.0.0.1:$taskPort"
    Wait-Local { (Invoke-RestMethod -NoProxy -TimeoutSec 10 -Uri "$taskUrl/api/local-usage/status").initialScanComplete } 'initial backfill'
    $taskClaude = Get-LocalSummary 'claude-local'
    Assert-Local 'Initial Claude history is imported' ($taskClaude.totals.requests -eq 1 -and $taskClaude.totals.outputTokens -eq 10)
    Assert-Local 'Current known model rates produce labeled estimates' ($taskClaude.pricedRequests -eq 1 -and $taskClaude.totals.costUsdMicros -eq 150)

    Append-Line $taskClaudeFile (Claude-Record 'claude-1' 100)
    Trigger-Sync
    Wait-Local { (Get-LocalSummary 'claude-local').totals.outputTokens -eq 100 } 'partial request update and rollup refresh'
    Assert-Local 'Partial response is updated rather than duplicated' ((Get-LocalSummary 'claude-local').totals.requests -eq 1)
    Copy-Item -LiteralPath $taskClaudeFile -Destination (Join-Path $taskClaudeDirectory 'copied-session.jsonl')
    Trigger-Sync
    $taskBeforeScan = (Invoke-RestMethod -NoProxy -TimeoutSec 10 -Uri "$taskUrl/api/local-usage/status").lastScanAt
    Wait-Local { (Invoke-RestMethod -NoProxy -TimeoutSec 10 -Uri "$taskUrl/api/local-usage/status").lastScanAt -ne $taskBeforeScan } 'duplicate replay scan'
    Assert-Local 'Copied Claude logs do not inflate usage' ((Get-LocalSummary 'claude-local').totals.requests -eq 1)

    $taskPartial = Claude-Record 'claude-2' 50 ([DateTimeOffset]::UtcNow.ToString('o'))
    $taskHalf = [int]($taskPartial.Length / 2)
    [IO.File]::AppendAllText($taskClaudeFile, $taskPartial.Substring(0,$taskHalf), [Text.UTF8Encoding]::new($false))
    Trigger-Sync
    $taskBeforeScan = (Invoke-RestMethod -NoProxy -TimeoutSec 10 -Uri "$taskUrl/api/local-usage/status").lastScanAt
    Wait-Local { (Invoke-RestMethod -NoProxy -TimeoutSec 10 -Uri "$taskUrl/api/local-usage/status").lastScanAt -ne $taskBeforeScan } 'incomplete line scan'
    Assert-Local 'An unfinished JSONL line is not ingested' ((Get-LocalSummary 'claude-local').totals.requests -eq 1)
    Append-Line $taskClaudeFile ($taskPartial.Substring($taskHalf))
    Trigger-Sync
    Wait-Local { (Get-LocalSummary 'claude-local').totals.requests -eq 2 } 'new local usage'
    Assert-Local 'New Claude usage is collected after startup' ($true)
    $taskLive = Invoke-RestMethod -NoProxy -TimeoutSec 10 -Uri "$taskUrl/api/live?source=claude-local"
    Assert-Local 'Live view follows local Claude usage' ($taskLive.today.requests -eq 1 -and [bool]$taskLive.lastEventAt)

    docker stop $taskContainer | Out-Null
    Append-Line $taskClaudeFile (Claude-Record 'claude-3' 75 ([DateTimeOffset]::UtcNow.ToString('o')))
    docker start $taskContainer | Out-Null
    # Docker can allocate a different ephemeral host port after a container restart.
    $taskPort = (docker port $taskContainer 8080/tcp).Trim().Split(':')[-1]
    $taskUrl = "http://127.0.0.1:$taskPort"
    Wait-Local { (Invoke-RestMethod -NoProxy -TimeoutSec 10 -Uri "$taskUrl/api/local-usage/status").initialScanComplete -and (Get-LocalSummary 'claude-local').totals.requests -eq 3 } 'restart catch-up'
    Assert-Local 'Restart resumes and catches usage written while offline' ((Get-LocalSummary 'claude-local').totals.outputTokens -eq 225)
    [IO.File]::WriteAllText($taskClaudeFile, (Claude-Record 'claude-4' 25 ([DateTimeOffset]::UtcNow.ToString('o'))) + "`n", [Text.UTF8Encoding]::new($false))
    Trigger-Sync
    Wait-Local { (Get-LocalSummary 'claude-local').totals.requests -eq 4 } 'truncation recovery'
    Assert-Local 'Truncated/replaced log is safely rescanned' ((Get-LocalSummary 'claude-local').totals.outputTokens -eq 250)
    $taskBench = Invoke-RestMethod -NoProxy -TimeoutSec 10 -Method Post -Uri "$taskUrl/api/bench?source=claude-local"
    Assert-Local 'Claude raw and rollup totals match' $taskBench.equivalent
    Write-Host "$taskChecks local collector checks passed. No personal session files were modified."
} catch {
    if ($taskAppCreated) { docker logs --tail 100 $taskContainer }
    throw
} finally {
    if ($taskAppCreated) { docker rm -f $taskContainer | Out-Null }
    if ($taskDbCreated) { docker exec $taskDbContainer psql -U postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE $taskDatabase WITH (FORCE)" | Out-Null }
    if (Test-Path -LiteralPath $taskFixtures) {
        $taskResolvedFixtures = (Resolve-Path -LiteralPath $taskFixtures).Path
        $taskAllowedParent = [IO.Path]::GetFullPath((Join-Path $taskRoot '.artifacts/local-usage-test')) + [IO.Path]::DirectorySeparatorChar
        if (-not $taskResolvedFixtures.StartsWith($taskAllowedParent,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test cleanup path.' }
        Remove-Item -LiteralPath $taskResolvedFixtures -Recurse -Force
    }
    Pop-Location
}
