# =============================================================================
# eng/Get-NativeDependencies.ps1 — получение закреплённых native зависимостей.
#
# Ответственность:
#   - скачать ровно opencv.url из eng/versions.json (никаких плавающих URL и тегов);
#   - обязательно проверить SHA256 пакета против opencv.sha256; при несовпадении — явная
#     ошибка с обоими значениями, сборка не продолжается и распаковка не выполняется;
#   - распаковать пакет в artifacts/opencv/<version> системным tar.exe (bsdtar), без установки
#     сторонних пакетов;
#   - быть идемпотентным: при совпадающем hash пакет не перекачивается, готовая распаковка не
#     распаковывается повторно.
#
# Контракт получения зависимости описан в .codex/context/build-contracts.md; значения версий
# живут только в eng/versions.json. Скрипт работает из любого текущего каталога.
#
# Запуск: pwsh ./eng/Get-NativeDependencies.ps1 [-Force]
# =============================================================================

[CmdletBinding()]
param(
    # Перекачать закреплённый пакет даже при наличии локальной копии.
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    . (Join-Path $PSScriptRoot 'common.ps1')
    Assert-AzurPilotEnvironment

    $versions = Get-AzurPilotVersions
    $layout = Get-AzurPilotArtifactsLayout -Versions $versions

    $expectedHash = $versions.OpenCvSha256.ToUpperInvariant()
    $archivePath = $layout.OpenCvArchivePath
    $configPath = Join-Path $layout.OpenCvCMakeDirectory 'OpenCVConfig.cmake'

    Write-AzurPilotInfo -Message "закреплённый пакет: OpenCV $($versions.OpenCvVersion) (владелец значений: eng/versions.json)"
    Write-AzurPilotDetail -Message "URL: $($versions.OpenCvUrl)"
    Write-AzurPilotDetail -Message "SHA256: $expectedHash"

    New-Item -ItemType Directory -Force -Path $layout.OpenCvDownloadDirectory | Out-Null

    if ($Force -and (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        Write-AzurPilotInfo -Message "запрошено повторное получение пакета (-Force): локальная копия удаляется"
        Remove-Item -LiteralPath $archivePath -Force
    }

    # --- Шаг 1: получить пакет -------------------------------------------------

    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        $existing = Get-Item -LiteralPath $archivePath
        Write-AzurPilotInfo -Message "пакет уже скачан ранее, повторная загрузка не выполняется: $archivePath ($([math]::Round($existing.Length / 1MB, 1)) МБ)"
    }
    else {
        $partialPath = "$archivePath.partial"
        if (Test-Path -LiteralPath $partialPath) { Remove-Item -LiteralPath $partialPath -Force }

        Write-AzurPilotInfo -Message "скачивание пакета: $($versions.OpenCvUrl)"
        # Индикатор прогресса отключён: он замедляет загрузку и засоряет журнал CI.
        $ProgressPreference = 'SilentlyContinue'
        $started = Get-Date
        try {
            Invoke-WebRequest -Uri $versions.OpenCvUrl -OutFile $partialPath -MaximumRedirection 5
        }
        catch {
            if (Test-Path -LiteralPath $partialPath) { Remove-Item -LiteralPath $partialPath -Force }
            throw "Не удалось скачать закреплённый пакет OpenCV по адресу '$($versions.OpenCvUrl)': $($_.Exception.Message). Владелец URL — eng/versions.json: opencv.url."
        }
        $elapsed = ((Get-Date) - $started).TotalSeconds

        # Проверка hash выполняется до того, как файл станет локальной копией: частичная загрузка
        # не должна выглядеть как готовый пакет.
        $partialHash = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($partialHash -ne $expectedHash) {
            Remove-Item -LiteralPath $partialPath -Force
            throw "SHA256 скачанного пакета OpenCV не совпал с eng/versions.json. Ожидалось: $expectedHash; получено: $partialHash. Скачанный файл удалён, распаковка не выполняется."
        }

        Move-Item -LiteralPath $partialPath -Destination $archivePath -Force
        Write-AzurPilotSuccess -Message "пакет скачан и проверен: $archivePath ($([math]::Round((Get-Item -LiteralPath $archivePath).Length / 1MB, 1)) МБ за $([math]::Round($elapsed, 1)) с)"
    }

    # --- Шаг 2: обязательная проверка SHA256 -----------------------------------

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $expectedHash) {
        throw @(
            'SHA256 локального пакета OpenCV не совпал с закреплённым значением: сборка не продолжается, распаковка не выполняется.',
            "Пакет: $archivePath",
            "Ожидалось (eng/versions.json: opencv.sha256): $expectedHash",
            "Получено: $actualHash",
            "Как исправить: удалите файл пакета и повторите запуск, либо запустите pwsh ./eng/Get-NativeDependencies.ps1 -Force для повторного скачивания."
        ) -join "`n"
    }
    Write-AzurPilotSuccess -Message 'SHA256 пакета совпал с закреплённым значением (eng/versions.json: opencv.sha256)'

    # --- Шаг 3: распаковка (идемпотентно) --------------------------------------

    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        Write-AzurPilotInfo -Message "распаковка уже выполнена, повторная распаковка не требуется: $($layout.OpenCvRoot)"
    }
    else {
        $archiveTool = Resolve-AzurPilotArchiveTool
        Write-AzurPilotDetail -Message "инструмент распаковки: $archiveTool"

        if (Test-Path -LiteralPath $layout.OpenCvRoot) {
            Write-AzurPilotInfo -Message "удаляется неполная распаковка предыдущего запуска: $($layout.OpenCvRoot)"
            Remove-Item -LiteralPath $layout.OpenCvRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $layout.OpenCvRoot | Out-Null

        $null = Invoke-AzurPilotExternalCommand -FilePath $archiveTool `
            -ArgumentList @('-x', '-f', $archivePath, '-C', $layout.OpenCvRoot) `
            -Description "распаковка закреплённого пакета OpenCV $($versions.OpenCvVersion)"

        Write-AzurPilotSuccess -Message "пакет распакован: $($layout.OpenCvRoot)"
    }

    # --- Шаг 4: проверка целостности содержимого -------------------------------

    $requiredDirectories = @(
        @{ Path = $layout.OpenCvUnpackedRoot; Description = "корневой каталог пакета (opencv.archiveRoot = '$($versions.OpenCvArchiveRoot)')" },
        @{ Path = $layout.OpenCvCMakeDirectory; Description = "каталог CMake-пакета (opencv.cmakeDir = '$($versions.OpenCvCMakeDir)')" },
        @{ Path = $layout.OpenCvRuntimeDirectory; Description = "каталог runtime DLL (opencv.runtimeDir = '$($versions.OpenCvRuntimeDir)')" }
    )

    foreach ($required in $requiredDirectories) {
        if (-not (Test-Path -LiteralPath $required.Path -PathType Container)) {
            throw "После распаковки не найден $($required.Description): '$($required.Path)'. Содержимое пакета не соответствует eng/versions.json; удалите '$($layout.OpenCvRoot)' и повторите запуск."
        }
    }

    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "После распаковки не найден '$configPath'. OpenCV_DIR указывать не на что: содержимое пакета не соответствует eng/versions.json."
    }

    Write-AzurPilotSuccess -Message "OpenCV $($versions.OpenCvVersion) готов: OpenCV_DIR = $($layout.OpenCvCMakeDirectory)"
    Write-AzurPilotDetail -Message "runtime DLL: $($layout.OpenCvRuntimeDirectory)"
    exit 0
}
catch {
    $message = $_.Exception.Message
    if (Get-Command -Name 'Write-AzurPilotError' -ErrorAction SilentlyContinue) {
        Write-AzurPilotError -Message $message
    }
    else {
        Write-Host "[azurpilot] ошибка — $message" -ForegroundColor Red
    }
    exit 1
}
