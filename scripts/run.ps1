param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$appPath = Join-Path $projectRoot 'artifacts\teaMind-win-x64\teaMind.exe'
if (-not (Test-Path -LiteralPath $appPath)) { throw '请先运行 scripts/build.ps1。' }
if ($SmokeTest) {
    $env:TEAMIND_DATA_DIRECTORY = Join-Path $projectRoot 'artifacts\validation-data'
    Start-Process -FilePath $appPath -ArgumentList @('--smoke-test', ('"' + (Join-Path $projectRoot 'artifacts\smoke') + '"'))
} else { Start-Process -FilePath $appPath }
