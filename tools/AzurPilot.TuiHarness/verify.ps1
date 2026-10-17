[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$App,
    [string]$Harness = (Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/win-x64/AzurPilot.TuiHarness.exe'),
    [switch]$ReadOnlyProviders
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VerificationSupport.ps1')
$App = (Resolve-Path -LiteralPath $App).Path
$Harness = (Resolve-Path -LiteralPath $Harness).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$reportRoot = Join-Path $repo ('artifacts/tui-harness/verification/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$sessions = [Collections.Generic.List[object]]::new()

function Start-HarnessCommand([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Harness)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    return @{ Process = $process; Output = $process.StandardOutput.ReadToEndAsync(); Error = $process.StandardError.ReadToEndAsync(); Command = $Arguments[0] }
}
function Complete-HarnessCommand($Invocation, [string]$Case, [string]$Expected = 'PASS') {
    try {
        $budget = if ($Invocation.Command -eq 'start') { $commandTimeouts.start } else { $commandTimeouts.other }
        if (-not $Invocation.Process.WaitForExit($budget)) {
            $Invocation.Process.Kill($true)
            throw "Истёк срок ожидания CLI: $Case"
        }
        $stdout = $Invocation.Output.GetAwaiter().GetResult()
        $stderr = $Invocation.Error.GetAwaiter().GetResult()
        if ($stdout.Contains([char]27)) { throw "Управляющие VT в JSON stdout: $Case" }
        $response = $stdout | ConvertFrom-Json
        $mapping = @{ PASS = 0; FAIL = 1; BLOCKED = 2; NOT_TESTED = 3 }
        if ($Invocation.Process.ExitCode -ne $mapping[$response.status]) { throw "Несогласованный exit code: $Case" }
        $stdout | Set-Content -LiteralPath (Join-Path $reportRoot ($Case + '.json'))
        $stderr | Set-Content -LiteralPath (Join-Path $reportRoot ($Case + '.stderr.txt'))
        $results.Add(@{ case = $Case; status = $response.status; expected = $Expected; result = $response })
        if ($Expected -ne 'DIAGNOSTIC' -and $response.status -ne $Expected) { throw "$Case : $($response.status): $($response.message)" }
        return $response
    }
    finally { $Invocation.Process.Dispose() }
}
function Invoke-HarnessCommand([string[]]$Arguments, [string]$Case, [string]$Expected = 'PASS') {
    return Complete-HarnessCommand (Start-HarnessCommand $Arguments) $Case $Expected
}
function Open-OwnedSession([string]$Case, [int]$Lifetime = 600) {
    $id = [Guid]::NewGuid().ToString('N')
    # ID известен до старта: даже потерянный stdout не теряет адрес принадлежащего owner.
    $pending = @{ session = $id }
    $sessions.Add($pending)
    $response = Invoke-HarnessCommand @('start', '--session', $id, '--exe', $App, '--output', $reportRoot, '--lifetime-seconds', "$Lifetime") $Case
    $pending.data = $response.data
    return $response
}
function Wait-OwnedExit([int]$OwnedPid) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt 10) {
        if (-not (Get-Process -Id $OwnedPid -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Не завершился принадлежащий harness PID $OwnedPid"
}
function Get-SettingsDigest {
    $directory = Split-Path $help.data.configurationPaths.yaml
    $files = if (Test-Path -LiteralPath $directory) { @(Get-ChildItem -LiteralPath $directory -File -Filter 'config*' | Sort-Object Name) } else { @() }
    return ConvertTo-Json -Compress -InputObject @($files | ForEach-Object { [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
}
$commandTimeouts = @{ start = 0; other = 45000 }
$help = Invoke-HarnessCommand @('help') 'protocol-budgets'
$commandTimeouts = $help.data.commandTimeouts
$settingsBefore = Get-SettingsDigest
$overall = 'PASS'
try {
    $results.Add((Test-AbandonedHarnessStartup $Harness $App $reportRoot))
    $expiry = Open-OwnedSession 'expiry-start' 30
    foreach ($appearanceName in @('campbell', 'campbell-light')) {
        $appearanceProfile = Join-Path $PSScriptRoot "profiles/$appearanceName.json"
        $null = Invoke-HarnessCommand @('fidelity', '--output', $reportRoot, '--profile', $appearanceProfile) "fidelity-$appearanceName"
        $startup = Invoke-HarnessCommand @('run', '--exe', $App, '--output', $reportRoot, '--profile', $appearanceProfile,
            '--scenario', (Join-Path $PSScriptRoot 'scenarios/startup.json')) "startup-$appearanceName"
        $expectedAccent = if ($appearanceName -eq 'campbell') { 0x4e9acf } else { 0x2e6da4 }
        $appearanceCells = Get-Content (Join-Path $startup.data.directory '001-startup.cells.json') -Raw | ConvertFrom-Json
        if (-not ($appearanceCells.cells | Where-Object { $_.fg.mode -eq 'rgb' -and $_.fg.value -eq $expectedAccent })) {
            throw "Акцент приложения не соответствует полярности профиля: $appearanceName"
        }
        $appearanceEvidence = Get-Content (Join-Path $startup.data.directory '001-startup.evidence.json') -Raw | ConvertFrom-Json
        $accentSample = $appearanceEvidence.colorComparison.comparisons | Where-Object { $_.sample.kind -eq 'foreground' -and $_.sample.fg.value -eq $expectedAccent }
        if (-not $accentSample -or $accentSample.status -ne 'PASS' -or $accentSample.maxChannelDelta -ne 0) {
            throw "RGB приложения не подтверждён пикселями PNG: $appearanceName"
        }
        $results.Add(@{ case = "production-rgb-$appearanceName"; status = 'PASS'; expected = $expectedAccent; maxChannelDelta = 0 })
    }
    $null = Invoke-HarnessCommand @('run', '--exe', $App, '--output', $reportRoot,
        '--scenario', (Join-Path $PSScriptRoot 'scenarios/history.json')) 'history'
    $settings = Invoke-HarnessCommand @('run', '--exe', $App, '--output', $reportRoot,
        '--scenario', (Join-Path $PSScriptRoot 'scenarios/settings.json')) 'settings'
    $configPath = [IO.Path]::GetFullPath((Join-Path $settings.data.directory 'runtime/config.yaml'))
    if (-not (Get-ChildItem (Split-Path $configPath) -Filter '*.bak-*')) { throw 'Не создана резервная копия изолированного YAML.' }
    $settingsCells = Get-Content (Join-Path $settings.data.directory '001-settings.cells.json') -Raw | ConvertFrom-Json
    if (-not (Test-DisplayedConfigurationPath $settingsCells.lines $configPath)) { throw 'UI не показывает точный изолированный YAML.' }
    if ($ReadOnlyProviders) {
        $null = Invoke-HarnessCommand @('run', '--exe', $App, '--output', $reportRoot, '--providers', 'read-only',
            '--scenario', (Join-Path $PSScriptRoot 'scenarios/safe-confirmation.json')) 'safe-confirmation'
    }
    else { $results.Add(@{ case = 'safe-confirmation'; status = 'NOT_TESTED'; reason = 'Требует -ReadOnlyProviders и реального однозначного inventory.' }) }

    $first = Open-OwnedSession 'parallel-first'
    $second = Open-OwnedSession 'parallel-second'
    $null = Invoke-HarnessCommand @('text', '--session', $first.session, '--value', 'Первый независимый') 'parallel-input-first'
    $null = Invoke-HarnessCommand @('text', '--session', $second.session, '--value', 'Второй независимый') 'parallel-input-second'
    $one = Start-HarnessCommand @('capture', '--session', $first.session, '--contains', 'Первый независимый', '--name', 'parallel-first')
    $two = Start-HarnessCommand @('capture', '--session', $second.session, '--contains', 'Второй независимый', '--name', 'parallel-second')
    $captureOne = Complete-HarnessCommand $one 'parallel-capture-first'
    $captureTwo = Complete-HarnessCommand $two 'parallel-capture-second'
    $cellsOne = Get-Content $captureOne.data.cells -Raw | ConvertFrom-Json
    $cellsTwo = Get-Content $captureTwo.data.cells -Raw | ConvertFrom-Json
    if (($cellsOne.lines -join "`n").Contains('Второй независимый') -or ($cellsTwo.lines -join "`n").Contains('Первый независимый')) { throw 'Пересечение вводов независимых сессий.' }
    $ownerProbe = Get-Process -Id $first.data.ownerPid
    try {
        $ownerProbe.Refresh()
        $handlesBefore = $ownerProbe.HandleCount
        for ($captureIndex = 0; $captureIndex -lt 8; $captureIndex++) {
            $null = Invoke-HarnessCommand @('capture', '--session', $first.session, '--name', "handle-$captureIndex") "handle-$captureIndex"
        }
        $ownerProbe.Refresh()
        $handlesAfter = $ownerProbe.HandleCount
        if ($handlesAfter - $handlesBefore -gt 8) { throw 'Рост числа handles при повторных captures превысил бюджет.' }
        $results.Add(@{ case = 'bounded-handle-growth'; status = 'PASS'; before = $handlesBefore; after = $handlesAfter; samples = 8 })
    }
    finally { $ownerProbe.Dispose() }

    $null = Invoke-HarnessCommand @('key', '--session', $first.session, '--value', 'Control+a') 'select-composer'
    $null = Invoke-HarnessCommand @('key', '--session', $first.session, '--value', 'Backspace') 'clear-composer'
    $null = Invoke-HarnessCommand @('key', '--session', $first.session, '--value', 'F2') 'mouse-palette'
    $menu = Invoke-HarnessCommand @('capture', '--session', $first.session, '--contains', 'Настройки', '--name', 'mouse-menu') 'mouse-menu-capture'
    $menuCells = Get-Content $menu.data.cells -Raw | ConvertFrom-Json
    $settingsRow = -1
    for ($lineIndex = 0; $lineIndex -lt $menuCells.lines.Count; $lineIndex++) {
        if ($menuCells.lines[$lineIndex].Trim() -eq 'Настройки') { $settingsRow = $lineIndex; break }
    }
    if ($settingsRow -lt 0) { throw 'Не найдена строка настроек для проверки клика мышью.' }
    $click = Invoke-HarnessCommand @('mouse', '--session', $first.session, '--value', 'click', '--x', '8', '--y', "$settingsRow") 'mouse-click-settings' 'DIAGNOSTIC'
    $afterClick = Invoke-HarnessCommand @('capture', '--session', $first.session, '--name', 'after-click') 'after-click-capture'
    $clickCells = Get-Content $afterClick.data.cells -Raw | ConvertFrom-Json
    $assessment = Get-PaletteClickAssessment $click (($clickCells.lines -join "`n").Contains('Расположение конфигурации'))
    $assessment.case = 'production-palette-click'
    $results.Add($assessment)
    if ($assessment.status -in @('FAIL', 'BLOCKED')) { throw "Ошибка доставки клика: $($assessment.reason)" }
    foreach ($opened in @($first, $second)) {
        $null = Invoke-HarnessCommand @('close', '--session', $opened.session) ('close-' + $opened.session)
        Wait-OwnedExit $opened.data.ownerPid
        Wait-OwnedExit $opened.data.identity.productionPid
    }
    $null = Invoke-HarnessCommand @('status', '--session', $first.session, '--timeout-ms', '100') 'closed-session' 'BLOCKED'
    $null = Invoke-HarnessCommand @('status', '--session', ([Guid]::NewGuid().ToString('N')), '--timeout-ms', '100') 'unknown-session' 'BLOCKED'

    $crashed = Open-OwnedSession 'owner-crash-start'
    $ownedChildren = [Collections.Generic.List[int]]::new()
    $ownedChildren.Add([int]$crashed.data.ownerPid)
    $inventory = @(Get-CimInstance Win32_Process)
    for ($depth = 0; $depth -lt 6; $depth++) {
        foreach ($entry in $inventory) {
            if ($ownedChildren.Contains([int]$entry.ParentProcessId) -and -not $ownedChildren.Contains([int]$entry.ProcessId)) { $ownedChildren.Add([int]$entry.ProcessId) }
        }
    }
    # Убивается только owner. Chromium/driver/App должны завершиться через Job, а не ручной tree kill.
    Stop-Process -Id $crashed.data.ownerPid
    foreach ($ownedProcessId in $ownedChildren) { Wait-OwnedExit $ownedProcessId }
    $results.Add(@{ case = 'owner-crash-cleanup'; status = 'PASS'; ownedPids = $ownedChildren.ToArray() })

    $childCrash = Open-OwnedSession 'production-crash-start'
    Stop-Process -Id $childCrash.data.identity.productionPid
    Wait-OwnedExit $childCrash.data.ownerPid
    $results.Add(@{ case = 'production-crash-cleanup'; status = 'PASS' })

    $browserCrash = Open-OwnedSession 'browser-crash-start'
    $browserTree = [Collections.Generic.List[int]]::new()
    $browserTree.Add([int]$browserCrash.data.ownerPid)
    $browserInventory = @(Get-CimInstance Win32_Process)
    for ($depth = 0; $depth -lt 6; $depth++) {
        foreach ($entry in $browserInventory) {
            if ($browserTree.Contains([int]$entry.ParentProcessId) -and -not $browserTree.Contains([int]$entry.ProcessId)) { $browserTree.Add([int]$entry.ProcessId) }
        }
    }
    $browserMain = $browserInventory | Where-Object {
        $browserTree.Contains([int]$_.ProcessId) -and $_.Name -eq 'chrome-headless-shell.exe' -and
        $_.ParentProcessId -notin @($browserInventory | Where-Object Name -eq 'chrome-headless-shell.exe' | Select-Object -ExpandProperty ProcessId)
    } | Select-Object -First 1
    if (-not $browserMain) { throw 'Не найден принадлежащий сессии Chromium для crash test.' }
    Stop-Process -Id $browserMain.ProcessId
    foreach ($ownedProcessId in $browserTree) { Wait-OwnedExit $ownedProcessId }
    $results.Add(@{ case = 'browser-crash-cleanup'; status = 'PASS' })

    $cancelled = Open-OwnedSession 'cancel-start'
    $null = Invoke-HarnessCommand @('key', '--session', $cancelled.session, '--value', 'Control+c') 'cancel-input' 'BLOCKED'
    Wait-OwnedExit $cancelled.data.ownerPid
    $results.Add(@{ case = 'cancel-cleanup'; status = 'PASS' })

    Wait-OwnedExit $expiry.data.ownerPid
    Wait-OwnedExit $expiry.data.identity.productionPid
    $results.Add(@{ case = 'absolute-timeout-cleanup'; status = 'PASS' })
    if ((Get-SettingsDigest) -cne $settingsBefore) { throw 'Изменились пользовательские YAML/JSON/backups.' }
    $results.Add(@{ case = 'production-settings-unchanged'; status = 'PASS' })
}
catch {
    $overall = 'FAIL'
    $results.Add(@{ case = 'verification-error'; status = 'FAIL'; reason = $_.Exception.Message })
}
finally {
    foreach ($opened in $sessions) {
        if (-not $opened.data) {
            # При обрыве start owner сам завершится по наблюдению запускающего CLI.
            try { $null = Invoke-HarnessCommand @('close', '--session', $opened.session, '--timeout-ms', '1000') ('pending-close-' + $opened.session) }
            catch { $results.Add(@{ case = 'pending-start-cleanup'; status = 'BLOCKED'; reason = $_.Exception.Message }) }
        }
        elseif (Get-Process -Id $opened.data.ownerPid -ErrorAction SilentlyContinue) {
            try { $null = Invoke-HarnessCommand @('close', '--session', $opened.session, '--timeout-ms', '1000') ('final-close-' + $opened.session) }
            catch { Stop-Process -Id $opened.data.ownerPid -ErrorAction SilentlyContinue }
        }
    }
    @{ status = $overall; results = $results.ToArray() } | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $reportRoot 'verification.json')
}
@{ status = $overall; report = (Join-Path $reportRoot 'verification.json') } | ConvertTo-Json -Compress
if ($overall -ne 'PASS') { exit 1 }
