[CmdletBinding()]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOriginalPath = $env:Path
# cmd.exe drops an inherited PATH longer than its 8191-character limit. Some
# development machines accumulate SDK paths. Narrow only this build process.
if ($env:Path.Length -gt 7000) {
    $taskToolPaths = @('node', 'dotnet', 'git', 'pwsh') | ForEach-Object {
        $taskCommand = Get-Command $_ -ErrorAction SilentlyContinue
        if ($taskCommand) { Split-Path -Parent $taskCommand.Source }
    }
    $env:Path = (@($taskToolPaths) + @("$env:SystemRoot\System32", $env:SystemRoot) | Select-Object -Unique) -join ';'
}
Push-Location $taskRoot
try {
    Push-Location web
    try {
        if (-not (Test-Path -LiteralPath node_modules)) {
            npm ci
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
        }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }
    } finally { Pop-Location }
    $taskStatic = [IO.Path]::GetFullPath((Join-Path $taskRoot 'src/TokenScope.Api/wwwroot'))
    # Replace only generated static output inside this repository's API project.
    $taskExpectedStatic = [IO.Path]::GetFullPath((Join-Path $taskRoot 'src/TokenScope.Api')) + [IO.Path]::DirectorySeparatorChar
    if (-not $taskStatic.StartsWith($taskExpectedStatic,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected static output directory.' }
    if (Test-Path -LiteralPath $taskStatic) { Remove-Item -LiteralPath $taskStatic -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $taskStatic | Out-Null
    Copy-Item -Path 'web/dist/*' -Destination $taskStatic -Recurse -Force
    dotnet build TokenScope.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw '.NET build failed.' }
    if (-not $SkipTests) {
        dotnet test TokenScope.slnx -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw '.NET tests failed.' }
    }
    Write-Host 'Built the client and .NET API.'
    Write-Host 'Run dotnet run --project src/TokenScope.Api to serve the complete app on localhost:5299.'
} finally { Pop-Location; $env:Path = $taskOriginalPath }
