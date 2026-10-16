[CmdletBinding()]
param([switch]$SkipBrowser, [switch]$AssetsOnly)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$vendorRoot = Join-Path $repo 'artifacts/tui-harness/vendor'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'renderer.lock.json') -Raw | ConvertFrom-Json
foreach ($package in $manifest.packages) {
    $directory = Join-Path $vendorRoot $package.name
    New-Item -ItemType Directory -Force $directory | Out-Null
    $archive = Join-Path $directory 'package.tgz'
    if (-not (Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -Uri $package.url -OutFile $archive
    }
    $bytes = [IO.File]::ReadAllBytes($archive)
    $integrity = 'sha512-' + [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($bytes))
    if ($integrity -cne $package.integrity) { throw "Неверный hash renderer package: $($package.name)" }
    & tar.exe -xf $archive -C $directory
    if ($LASTEXITCODE -ne 0) { throw "Не удалось распаковать $($package.name)" }
}
if ($AssetsOnly) { return }

Push-Location $repo
try {
    & dotnet restore tools/AzurPilot.TuiHarness --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore harness завершился отказом.' }
    & dotnet build tools/AzurPilot.TuiHarness -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Сборка harness завершилась отказом.' }
    if (-not $SkipBrowser) {
        $env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $repo 'artifacts/tui-harness/browsers'
        & pwsh -NoProfile -File tools/AzurPilot.TuiHarness/bin/Release/net10.0-windows/win-x64/playwright.ps1 install chromium
        if ($LASTEXITCODE -ne 0) { throw 'Установка Chromium завершилась отказом.' }
    }
}
finally { Pop-Location }
