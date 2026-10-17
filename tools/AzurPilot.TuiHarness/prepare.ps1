[CmdletBinding()]
param([switch]$SkipBrowser, [switch]$AssetsOnly)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$vendorRoot = Join-Path $repo 'artifacts/tui-harness/vendor'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'renderer.lock.json') -Raw | ConvertFrom-Json
function Get-RendererIntegrity([string]$archivePath) {
    $bytes = [IO.File]::ReadAllBytes($archivePath)
    return 'sha512-' + [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($bytes))
}

foreach ($package in $manifest.packages) {
    $directory = Join-Path $vendorRoot $package.name
    New-Item -ItemType Directory -Force $directory | Out-Null
    $archive = Join-Path $directory 'package.tgz'
    $archiveValid = Test-Path -LiteralPath $archive
    if ($archiveValid) {
        $archiveValid = (Get-RendererIntegrity $archive) -ceq $package.integrity
        if (-not $archiveValid) { Remove-Item -LiteralPath $archive -Force }
    }
    if (-not $archiveValid) {
        $partial = Join-Path $directory ('package.tgz.' + [guid]::NewGuid().ToString('N') + '.partial')
        try {
            Invoke-WebRequest -Uri $package.url -OutFile $partial
            if ((Get-RendererIntegrity $partial) -cne $package.integrity) {
                throw "Неверный hash renderer package: $($package.name)"
            }
            Move-Item -LiteralPath $partial -Destination $archive -Force
        }
        finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        }
    }
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
