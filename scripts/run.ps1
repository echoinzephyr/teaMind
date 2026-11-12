param([switch]$SmokeTest, [switch]$InteractionTest, [switch]$WindowRegressionTest, [switch]$CloseAfterTest)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$appPath = Join-Path $projectRoot 'artifacts\teaMind-win-x64\teaMind.exe'
if (-not (Test-Path -LiteralPath $appPath)) { throw '请先运行 scripts/build.ps1。' }
if (@($SmokeTest, $InteractionTest, $WindowRegressionTest).Where({ $_ }).Count -gt 1) { throw '一次只选择一种验证方式。' }
$launchId = [Guid]::NewGuid().ToString('N')
$logs = Join-Path $projectRoot 'artifacts\launch'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$launchArguments = @()
$previousDataDirectory = $env:TEAMIND_DATA_DIRECTORY
try {
    $testFlag = if ($SmokeTest) { '--smoke-test' } elseif ($InteractionTest) { '--interaction-perf-test' } elseif ($WindowRegressionTest) { '--window-regression-test' }
    if ($testFlag) {
        $testName = $testFlag.TrimStart('-')
        $reportDirectory = Join-Path $projectRoot "artifacts\$testName-$launchId"
        $env:TEAMIND_DATA_DIRECTORY = Join-Path $reportDirectory 'data'
        $launchArguments += @($testFlag, ('"' + $reportDirectory + '"'))
    }
    if ($CloseAfterTest) { $launchArguments += '--close-after-test' }
    $launch = @{
        FilePath = $appPath
        PassThru = $true
        RedirectStandardOutput = Join-Path $logs "$launchId-output.txt"
        RedirectStandardError = Join-Path $logs "$launchId-error.txt"
    }
    if ($launchArguments.Count) { $launch.ArgumentList = $launchArguments }
    $process = Start-Process @launch
    [pscustomobject]@{ ProcessId = $process.Id; Application = $appPath; Report = $reportDirectory }
} finally { $env:TEAMIND_DATA_DIRECTORY = $previousDataDirectory }
