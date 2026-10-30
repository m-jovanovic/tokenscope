[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDatabase = 'tokenscope_test_' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
$taskCreated = $false
$taskOriginalConnection = $env:TOKENSCOPE_TEST_CONNECTION
Push-Location $taskRoot
try {
    docker compose up -d --wait timescaledb
    if ($LASTEXITCODE -ne 0) { throw 'Database startup failed.' }
    $taskDbContainer = docker compose ps -q timescaledb
    $taskDbPort = (docker compose port timescaledb 5432).Trim().Split(':')[-1]
    docker exec $taskDbContainer psql -U postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $taskDatabase"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create disposable test database.' }
    $taskCreated = $true
    $env:TOKENSCOPE_TEST_CONNECTION = "Host=127.0.0.1;Port=$taskDbPort;Database=$taskDatabase;Username=postgres;Password=postgres"
    dotnet test TokenScope.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally {
    $env:TOKENSCOPE_TEST_CONNECTION = $taskOriginalConnection
    if ($taskCreated) {
        docker exec $taskDbContainer psql -U postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE $taskDatabase WITH (FORCE)" | Out-Null
    }
    Pop-Location
}
