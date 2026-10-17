# Общие проверки отчётов; этот файл не запускает процессы и не изменяет окружение при подключении.
function Test-DisplayedConfigurationPath([string[]]$Lines, [string]$ExpectedPath) {
    $label = 'Расположение конфигурации: '
    $text = $Lines -join ''
    $expected = $label + $ExpectedPath
    $offset = $text.IndexOf($expected, [StringComparison]::Ordinal)
    if ($offset -lt 0) { return $false }
    # Переносы пути заполняют строку; удалять значимые пробелы или склеивать слова нельзя.
    $end = $offset + $expected.Length
    $rowEnd = 0
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $line = $Lines[$index]
        $rowEnd += $line.Length
        if ($rowEnd -ge $end) {
            if (-not [string]::IsNullOrWhiteSpace($text.Substring($end, $rowEnd - $end))) { return $false }
            # Резервная копия имеет продолжение `.bak-*`; оно может начаться на следующей ячейке-строке,
            # когда основной путь заканчивается ровно на границе терминального ряда.
            if ($rowEnd -eq $end -and $index + 1 -lt $Lines.Count -and
                $Lines[$index + 1].StartsWith('.bak', [StringComparison]::OrdinalIgnoreCase)) { return $false }
            return $true
        }
    }
    return $false
}

function Get-PaletteClickAssessment($Response, [bool]$SettingsObserved) {
    if ($Response.status -eq 'NOT_TESTED' -and $Response.data.delivered -eq $true -and
        $Response.data.reason -eq 'click_effect_not_observed') {
        return @{ status = 'NOT_TESTED'; reason = $Response.message; delivered = $true }
    }
    if ($Response.status -eq 'PASS' -and $SettingsObserved) {
        return @{ status = 'PASS'; reason = 'Открытие настроек кликом подтверждено видимыми ячейками.' }
    }
    # Смена курсора/выделения сама по себе не доказывает открытие нужного экрана.
    if ($Response.status -eq 'PASS') {
        return @{ status = 'NOT_TESTED'; reason = 'Ввод доставлен, но открытие настроек не подтверждено.' }
    }
    return @{ status = $Response.status; reason = $Response.message }
}

# Требует подготовленный Chromium, как и остальной runtime-прогон verify.ps1.
function Test-AbandonedHarnessStartup([string]$HarnessPath, [string]$AppPath, [string]$OutputRoot) {
    function Start-WaitingClient {
        $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardInput = $true
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', '[Console]::ReadLine() | Out-Null')) { $info.ArgumentList.Add($argument) }
        return [Diagnostics.Process]::Start($info)
    }
    $client = Start-WaitingClient
    $unrelated = Start-WaitingClient
    $owner = $null
    $child = $null
    try {
        $directory = Join-Path $OutputRoot 'abandoned-startup'
        New-Item -ItemType Directory -Path $directory | Out-Null
        $request = Join-Path $directory 'start.json'
        @{ command = 'start'; session = [Guid]::NewGuid().ToString('N'); output = $directory
           exe = $AppPath; startupClientPid = $client.Id } | ConvertTo-Json | Set-Content -LiteralPath $request
        $info = [Diagnostics.ProcessStartInfo]::new($HarnessPath)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.ArgumentList.Add('__owner')
        $info.ArgumentList.Add($request)
        $owner = [Diagnostics.Process]::Start($info)
        $identityFile = Join-Path $directory 'identity.json'
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $identityFile) -and -not $owner.HasExited -and $deadline.Elapsed.TotalSeconds -lt 30) {
            Start-Sleep -Milliseconds 30
        }
        if ($owner.HasExited -or -not (Test-Path -LiteralPath $identityFile)) { throw 'Владелец не дошёл до запуска production child.' }
        $identity = Get-Content -LiteralPath $identityFile -Raw | ConvertFrom-Json
        $child = [Diagnostics.Process]::GetProcessById($identity.productionPid)
        # Capture доказывает, что owner дошёл до готовой production UI и отвечает по IPC;
        # status не вызываем, чтобы startup watchdog продолжал следить за исходным клиентом.
        $probeInfo = [Diagnostics.ProcessStartInfo]::new($HarnessPath)
        $probeInfo.UseShellExecute = $false
        $probeInfo.CreateNoWindow = $true
        $probeInfo.RedirectStandardOutput = $true
        $probeInfo.RedirectStandardError = $true
        foreach ($argument in @('capture', '--session', $identity.session, '--name', 'startup-ready', '--timeout-ms', '30000')) {
            $probeInfo.ArgumentList.Add($argument)
        }
        $probe = [Diagnostics.Process]::Start($probeInfo)
        $probeOutput = $probe.StandardOutput.ReadToEndAsync()
        $probeError = $probe.StandardError.ReadToEndAsync()
        try {
            if (-not $probe.WaitForExit(45000)) { throw 'Production TUI не ответил на контрольный capture.' }
            $probeResult = $probeOutput.GetAwaiter().GetResult() | ConvertFrom-Json
            $null = $probeError.GetAwaiter().GetResult()
            if ($probe.ExitCode -ne 0 -or $probeResult.status -ne 'PASS') {
                throw 'Production TUI не прошёл контрольный capture до обрыва клиента.'
            }
        }
        finally {
            if (-not $probe.HasExited) { $probe.Kill(); if (-not $probe.WaitForExit(10000)) { throw 'Не завершился capture-процесс проверки.' } }
            $probe.Dispose()
        }
        $owner.Refresh()
        $child.Refresh()
        if ($owner.HasExited -or $child.HasExited) { throw 'Владелец или production child завершился до обрыва клиента.' }
        $client.Kill()
        if (-not $owner.WaitForExit(10000) -or -not $child.WaitForExit(10000)) { throw 'Обрыв клиента оставил принадлежащий процесс.' }
        if ($unrelated.HasExited) { throw 'Обрыв клиента затронул посторонний процесс.' }
        return @{ case = 'abandoned-startup-cleanup'; status = 'PASS'; ownerPid = $owner.Id; productionPid = $child.Id; unrelatedPid = $unrelated.Id }
    }
    finally {
        foreach ($process in @($owner, $client, $unrelated)) {
            if ($process) {
                if (-not $process.HasExited) { $process.Kill(); if (-not $process.WaitForExit(10000)) { throw 'Не завершился принадлежащий процесс проверки.' } }
                $process.Dispose()
            }
        }
        if ($child) { $child.Dispose() }
    }
}
