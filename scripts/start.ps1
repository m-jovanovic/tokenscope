[CmdletBinding()]
param([switch]$NoBuild, [switch]$DatabaseOnly, [switch]$LocalUsage, [switch]$DisableLocalUsage)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if ($LocalUsage -and $DisableLocalUsage) { throw 'Choose either -LocalUsage or -DisableLocalUsage.' }
$taskPreferencePath = Join-Path $taskRoot '.artifacts/local-usage-preference.json'
$taskLocalEnabled = $false
if (Test-Path -LiteralPath $taskPreferencePath) {
    $taskLocalEnabled = [bool]((Get-Content -LiteralPath $taskPreferencePath -Raw | ConvertFrom-Json).enabled)
}
if ($LocalUsage) { $taskLocalEnabled = $true }
if ($DisableLocalUsage) { $taskLocalEnabled = $false }
$taskComposeArgs = @('compose')
if ($taskLocalEnabled -and -not $DatabaseOnly) {
    $taskUserDirectory = [Environment]::GetFolderPath('UserProfile')
    $taskRoots = @(
        @{ Key='ClaudePath'; HostPath=$(if ($env:TOKENSCOPE_CLAUDE_PROJECTS) { $env:TOKENSCOPE_CLAUDE_PROJECTS } else { Join-Path $taskUserDirectory '.claude/projects' }); ContainerPath='/usage/claude' }
    )
    $taskMounts = @()
    $taskCollectorEnvironment = @{ LocalUsage__Enabled='true' }
    foreach ($taskUsageRoot in $taskRoots) {
        if (Test-Path -LiteralPath $taskUsageRoot.HostPath -PathType Container) {
            $taskResolved = (Resolve-Path -LiteralPath $taskUsageRoot.HostPath).Path
            $taskMounts += @{ type='bind'; source=$taskResolved; target=$taskUsageRoot.ContainerPath; read_only=$true }
            $taskCollectorEnvironment["LocalUsage__$($taskUsageRoot.Key)"] = $taskUsageRoot.ContainerPath
        }
    }
    if ($taskMounts.Count -eq 0) { throw 'No local Claude Code session folder was found. Set TOKENSCOPE_CLAUDE_PROJECTS to its location.' }
    New-Item -ItemType Directory -Force -Path (Join-Path $taskRoot '.artifacts') | Out-Null
    $taskOverridePath = Join-Path $taskRoot '.artifacts/local-usage.compose.json'
    @{ services=@{ app=@{ environment=$taskCollectorEnvironment; volumes=$taskMounts } } } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskOverridePath -Encoding utf8
    $taskComposeArgs += @('-f', (Join-Path $taskRoot 'docker-compose.yml'), '-f', $taskOverridePath)
}
Push-Location $taskRoot
try {
    if ($DatabaseOnly) {
        docker @taskComposeArgs up -d --wait timescaledb
    } elseif ($NoBuild) {
        docker @taskComposeArgs up -d
    } else {
        docker @taskComposeArgs up --build -d
    }
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose failed. Check that Docker Desktop is running.' }
    if (($LocalUsage -or $DisableLocalUsage) -and -not $DatabaseOnly) {
        New-Item -ItemType Directory -Force -Path (Join-Path $taskRoot '.artifacts') | Out-Null
        @{ enabled=$taskLocalEnabled } | ConvertTo-Json | Set-Content -LiteralPath $taskPreferencePath -Encoding utf8
    }
    if ($DatabaseOnly) {
        $taskDbPort = if ($env:TOKENSCOPE_DB_PORT) { $env:TOKENSCOPE_DB_PORT } else { '55432' }
        Write-Host "TimescaleDB is ready on localhost:$taskDbPort."
        return
    }
    $taskPort = if ($env:TOKENSCOPE_PORT) { $env:TOKENSCOPE_PORT } else { '5299' }
    $taskUrl = "http://localhost:$taskPort"
    # Compose binds IPv4 loopback; avoid machine-specific localhost DNS/proxy behavior.
    $taskHealthUrl = "http://127.0.0.1:$taskPort"
    $taskReady = $false
    for ($taskAttempt = 0; $taskAttempt -lt 45; $taskAttempt++) {
        try {
            $taskResponse = Invoke-WebRequest -NoProxy -Uri "$taskHealthUrl/api/status" -TimeoutSec 3
            if ($taskResponse.StatusCode -eq 200) { $taskReady = $true; break }
        } catch { Start-Sleep -Seconds 2 }
    }
    if (-not $taskReady) { throw 'App did not become ready. Run docker compose logs app.' }
    Write-Host "TokenScope is ready: $taskUrl"
    Write-Host 'Open Data & setup to connect Claude Code telemetry or import history.'
    if ($taskLocalEnabled) { Write-Host 'Local usage collection is enabled. Initial history scanning may take several minutes; progress is in Data & setup.' }
} finally { Pop-Location }
