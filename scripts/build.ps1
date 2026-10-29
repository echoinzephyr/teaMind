param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    dotnet restore teaMind.slnx --packages .packages -p:Platform=x64 --locked-mode --disable-parallel -m:1 -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '依赖还原失败。' }
    if (-not $SkipTests) {
        dotnet run --project tests/TeaMind.Tests/TeaMind.Tests.csproj -c Release --no-restore -- artifacts/tests
        if ($LASTEXITCODE -ne 0) { throw '测试未通过。' }
    }
    dotnet publish src/TeaMind.App/TeaMind.App.csproj -c Release --no-restore -p:Platform=x64 -o artifacts/teaMind-win-x64
    if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
    Copy-Item -LiteralPath README.md -Destination artifacts/teaMind-win-x64/README.md
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination artifacts/teaMind-win-x64/THIRD-PARTY-NOTICES.md
    New-Item -ItemType Directory -Path artifacts/teaMind-win-x64/Licenses -Force | Out-Null
    Copy-Item -LiteralPath licenses/MIT-COMPONENTS.txt -Destination artifacts/teaMind-win-x64/Licenses/MIT-COMPONENTS.txt
    foreach ($package in Get-ChildItem .packages -Directory) {
        foreach ($version in Get-ChildItem -LiteralPath $package.FullName -Directory) {
            foreach ($notice in Get-ChildItem -LiteralPath $version.FullName -File | Where-Object Name -Match '^(licen[cs]e|third.party.notices)') {
                $destination = Join-Path 'artifacts/teaMind-win-x64/Licenses' ($package.Name + '-' + $version.Name + '-' + $notice.Name)
                Copy-Item -LiteralPath $notice.FullName -Destination $destination
            }
        }
    }
    Write-Output '发布完成：artifacts/teaMind-win-x64/teaMind.exe'
} finally { Pop-Location }
