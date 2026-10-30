[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5299',
    [switch]$IncludeFixtureImport,
    [switch]$IncludeDemoPulse
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskResults = [System.Collections.Generic.List[object]]::new()
function Assert-Check([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAILED: $Name" }
    $taskResults.Add([pscustomobject]@{ Check = $Name; Result = 'PASS' })
}
function Get-Api([string]$Path) { Invoke-RestMethod -NoProxy -Uri "$BaseUrl$Path" -TimeoutSec 60 }
function Post-Api([string]$Path, [object]$Payload) {
    Invoke-RestMethod -NoProxy -Method Post -Uri "$BaseUrl$Path" -ContentType 'application/json' -Body ($Payload | ConvertTo-Json -Depth 30) -TimeoutSec 180
}
function Invoke-Mcp([string]$Method, [object]$Parameters) {
    $taskResponse = Invoke-WebRequest -NoProxy -Uri "$BaseUrl/mcp" -Method Post -ContentType 'application/json' `
        -Headers @{ Accept = 'application/json, text/event-stream' } `
        -Body (@{ jsonrpc = '2.0'; id = 1; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 30) -TimeoutSec 30
    if ($taskResponse.Headers.'Content-Type' -match 'text/event-stream') {
        $taskLine = ($taskResponse.Content -split "`n" | Where-Object { $_ -match '^data:' } | Select-Object -Last 1) -replace '^data:\s*', ''
        return $taskLine | ConvertFrom-Json -Depth 40
    }
    return $taskResponse.Content | ConvertFrom-Json -Depth 40
}

Assert-Check 'HTTP health reports a database connection' ((Get-Api '/health').status -eq 'healthy')
$taskStatus = Get-Api '/api/status'
Assert-Check 'TimescaleDB extension detected' ([bool]$taskStatus.database.timescaleVersion)
Assert-Check 'Three continuous aggregates are present' ($taskStatus.aggregates.Count -eq 3)
Assert-Check 'Real-time aggregation is enabled' (@($taskStatus.aggregates | Where-Object materializedOnly).Count -eq 0)
Assert-Check 'Complete browser app is served' ((Invoke-WebRequest -NoProxy -Uri $BaseUrl -TimeoutSec 20).Content -match '<div id="root">')
$taskPack = Invoke-WebRequest -NoProxy -Uri "$BaseUrl/rehearsal/pack.json" -SkipHttpErrorCheck
Assert-Check 'Private recording pack is absent' ($taskPack.StatusCode -eq 404)
Assert-Check 'Source catalog contains only Claude datasets' (@($taskStatus.sources | Where-Object source -NotIn @('live','history','demo','claude-local')).Count -eq 0)
foreach ($taskSource in @('live', 'history', 'demo', 'claude-local')) {
    foreach ($taskRange in @('1h', '24h', '7d', '30d', '90d')) {
        $taskSummary = Get-Api "/api/summary?source=$taskSource&range=$taskRange"
        Assert-Check "$taskSource / $taskRange returns an explicit window" ([bool]$taskSummary.windowStart -and [bool]$taskSummary.windowEnd)
    }
    $taskBench = Post-Api "/api/bench?source=$taskSource" @{}
    Assert-Check "$taskSource raw and rollup answers match" ($taskBench.equivalent -eq $true)
}
$taskBadRoute = Invoke-WebRequest -NoProxy -Uri "$BaseUrl/api/missing-test-route" -SkipHttpErrorCheck
Assert-Check 'Unknown API routes stay 404' ($taskBadRoute.StatusCode -eq 404)
$taskBadInput = Invoke-WebRequest -NoProxy -Uri "$BaseUrl/api/summary?source=invalid" -SkipHttpErrorCheck
Assert-Check 'Invalid source is rejected' ($taskBadInput.StatusCode -eq 400)
$taskTools = Invoke-Mcp 'tools/list' @{}
Assert-Check 'MCP exposes fixed report tools' (@($taskTools.result.tools | Where-Object name -EQ 'run_report').Count -eq 1)
$taskDenied = Invoke-Mcp 'tools/call' @{ name = 'run_report'; arguments = @{ report = 'delete_data'; source = 'demo' } }
Assert-Check 'Write request is refused before SQL execution' ($taskDenied.result.isError -eq $true)
$taskReport = Invoke-Mcp 'tools/call' @{ name = 'get_daily_spend'; arguments = @{ source = 'demo'; days = 30 } }
Assert-Check 'MCP read report works' (-not $taskReport.error -and $taskReport.result.isError -ne $true)

if ($IncludeDemoPulse) {
    $taskPulse = Post-Api '/api/demo/pulse' @{}
    Assert-Check 'Synthetic pulse stays in demo source' ($taskPulse.inserted -eq 1 -and $taskPulse.source -eq 'demo' -and $taskPulse.synthetic)
}
if ($IncludeFixtureImport) {
    Write-Host 'Importing the explicitly synthetic fixture into history. Use an isolated test instance for this check.'
    $taskFixture = Join-Path $taskRoot 'tests/fixtures/claude-history.synthetic.jsonl'
    $taskPreview = Invoke-RestMethod -NoProxy -Method Post -Uri "$BaseUrl/api/history/preview" -ContentType 'application/x-ndjson' -InFile $taskFixture -TimeoutSec 30
    Assert-Check 'History preview detects partial-message duplicates' ($taskPreview.requests -eq 4 -and $taskPreview.duplicates -eq 1)
    Assert-Check 'Unknown model stays unpriced and fixture stays labeled' ($taskPreview.unpricedRequests -eq 1 -and $taskPreview.synthetic)
    $taskFirst = Post-Api '/api/history/import' @{ previewId = $taskPreview.previewId }
    $taskAgain = Post-Api '/api/history/import' @{ previewId = $taskPreview.previewId }
    Assert-Check 'Repeated history import is idempotent' ($taskAgain.inserted -eq 0 -and $taskAgain.alreadyImported -eq 4)
    $taskHistoryBench = Post-Api '/api/bench?source=history' @{}
    Assert-Check 'Imported historical aggregates match raw data' ($taskHistoryBench.equivalent)
}
$taskResults | Format-Table -AutoSize
Write-Host "$($taskResults.Count) checks passed."
