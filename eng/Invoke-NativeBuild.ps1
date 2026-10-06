# =============================================================================
# eng/Invoke-NativeBuild.ps1 — configure, build и CTest native boundary.
#
# Ответственность:
#   - разрешить toolchain с диагностикой при отсутствии или старой версии: CMake (только
#     standalone, не ниже toolchain.cmakeMinimumVersion), MSVC toolset (не ниже
#     toolchain.msvcMinimumVersion); Ninja проверяется как необязательный альтернативный путь;
#   - сконфигурировать native boundary каноническим CMake configure preset'ом из
#     native/CMakePresets.json, передав OpenCV_DIR и требуемую версию ABI значениями из
#     eng/versions.json;
#   - собрать native targets и, если не указано -SkipTests, прогнать native CTest;
#   - упасть с ненулевым кодом и русской диагностикой при любом реальном нарушении.
#
# Значения версий не дублируются: владелец — eng/versions.json; generator, архитектура и каталог
# сборки — native/CMakePresets.json (.codex/context/build-contracts.md).
#
# Запуск: pwsh ./eng/Invoke-NativeBuild.ps1 -Configuration Release [-OpenCvDir <путь>] [-Clean] [-SkipTests]
# =============================================================================

[CmdletBinding()]
param(
    # Конфигурация сборки native части (multi-config generator).
    [Parameter(Mandatory = $true)][ValidateSet('Debug', 'Release')][string]$Configuration,

    # Каталог с OpenCVConfig.cmake. По умолчанию — каталог закреплённого пакета внутри artifacts/.
    [string]$OpenCvDir,

    # Удалить build directories native части перед сборкой.
    [switch]$Clean,

    # Не запускать native CTest.
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    . (Join-Path $PSScriptRoot 'common.ps1')
    Assert-AzurPilotEnvironment

    $versions = Get-AzurPilotVersions
    $layout = Get-AzurPilotArtifactsLayout -Versions $versions -Configuration $Configuration

    Write-AzurPilotInfo -Message "native сборка: конфигурация $Configuration"

    # --- Toolchain -------------------------------------------------------------

    Write-AzurPilotInfo -Message 'проверка toolchain native части'
    $cmake = Resolve-AzurPilotCMakeToolchain -Versions $versions
    $visualStudio = Resolve-AzurPilotVisualStudioToolchain -Versions $versions

    # Ninja нужен только альтернативному пути: его отсутствие не блокирует канонический путь,
    # но обязано быть явно сообщено (.codex/context/build-contracts.md).
    $ninja = Resolve-AzurPilotNinjaToolchain -Versions $versions -VisualStudioInstancePath $visualStudio.InstancePath
    if ($ninja.Available) { Write-AzurPilotSuccess -Message $ninja.Message } else { Write-AzurPilotWarning -Message $ninja.Message }

    # --- Presets ---------------------------------------------------------------

    $presets = Get-AzurPilotCMakePreset -PresetsPath $layout.NativePresetsPath -Versions $versions -Configuration $Configuration
    Write-AzurPilotDetail -Message "configure preset: $($presets.ConfigurePreset); build preset: $($presets.BuildPreset); test preset: $($presets.TestPreset)"

    # --- Очистка ---------------------------------------------------------------

    if ($Clean) {
        Write-AzurPilotInfo -Message 'очистка build directories native части (-Clean)'
        # Удаляется каталог сборки из preset'а и выход native targets: значение каталога сборки
        # имеет одного владельца — native/CMakePresets.json.
        foreach ($directory in @($presets.BinaryDirectory, $layout.NativeOutputDirectory)) {
            if (Test-Path -LiteralPath $directory) {
                Remove-Item -LiteralPath $directory -Recurse -Force
                Write-AzurPilotDetail -Message "удалён каталог: $directory"
            }
        }
    }

    # --- OpenCV ----------------------------------------------------------------

    $openCvCmakeDirectory = $OpenCvDir
    if (-not $openCvCmakeDirectory) { $openCvCmakeDirectory = $layout.OpenCvCMakeDirectory }

    $openCvConfigPath = Join-Path $openCvCmakeDirectory 'OpenCVConfig.cmake'
    if (-not (Test-Path -LiteralPath $openCvConfigPath -PathType Leaf)) {
        throw @(
            "Не найден OpenCVConfig.cmake: конфигурация закреплённого OpenCV не получена.",
            "Проверенный каталог: '$openCvCmakeDirectory'",
            "Ожидаемый каталог закреплённого пакета: $($layout.OpenCvCMakeDirectory)",
            "Как исправить: получите закреплённый пакет каноническим путём — pwsh ./eng/build.ps1 -Configuration $Configuration, или pwsh ./eng/Get-NativeDependencies.ps1, либо передайте -OpenCvDir явно."
        ) -join "`n"
    }
    Write-AzurPilotSuccess -Message "OpenCV_DIR: $openCvCmakeDirectory"

    # --- Configure -------------------------------------------------------------

    Write-AzurPilotInfo -Message 'configure native части'

    # Требуемая версия ABI передаётся как cache variable, чтобы значение из eng/versions.json было
    # видно в кэше сборки (интерфейс описан в .codex/context/build-contracts.md). Нормативный
    # владелец номера — native/include/azurpilot_native_abi.h; native/CMakeLists.txt читает
    # зеркальное значение из eng/versions.json напрямую и валит configure при расхождении.
    $null = Invoke-AzurPilotExternalCommand -FilePath $cmake.Path `
        -ArgumentList @('--preset', $presets.ConfigurePreset, "-DOpenCV_DIR=$openCvCmakeDirectory", "-DAZURPILOT_ABI_VERSION=$($versions.NativeAbiVersion)") `
        -WorkingDirectory $layout.NativeSourceDirectory `
        -Description 'configure native части (CMake)'

    # Проверка, что configure действительно прошёл с ожидаемыми значениями: кэш читается обратно,
    # расхождение — ошибка, а не тихое продолжение.
    $cachePath = Join-Path $presets.BinaryDirectory 'CMakeCache.txt'
    if (-not (Test-Path -LiteralPath $cachePath -PathType Leaf)) {
        throw "После configure не найден '$cachePath': каталог сборки native части не создан."
    }

    $cacheLines = Get-Content -LiteralPath $cachePath

    $generatorLine = @($cacheLines | Where-Object { $_ -match '^CMAKE_GENERATOR:' }) | Select-Object -First 1
    if (-not $generatorLine) {
        throw "В '$cachePath' отсутствует запись CMAKE_GENERATOR: configure не зафиксировал generator."
    }
    $configuredGenerator = ($generatorLine -split '=', 2)[1]
    if ($configuredGenerator -ne $versions.CMakeGenerator) {
        throw "Native часть сконфигурирована generator'ом '$configuredGenerator', а eng/versions.json закрепляет '$($versions.CMakeGenerator)'. Как исправить: удалите каталог сборки '$($presets.BinaryDirectory)' и повторите запуск."
    }

    $abiLine = @($cacheLines | Where-Object { $_ -match '^AZURPILOT_ABI_VERSION:' }) | Select-Object -First 1
    if (-not $abiLine) {
        throw "В '$cachePath' отсутствует запись AZURPILOT_ABI_VERSION: требуемая версия ABI не дошла до configure."
    }
    $configuredAbiVersion = ($abiLine -split '=', 2)[1]
    if ($configuredAbiVersion -ne [string]$versions.NativeAbiVersion) {
        throw "Cache variable AZURPILOT_ABI_VERSION = '$configuredAbiVersion', а eng/versions.json закрепляет '$($versions.NativeAbiVersion)'."
    }

    Write-AzurPilotSuccess -Message "configure выполнен: generator '$configuredGenerator' (eng/versions.json), требуемая версия ABI $configuredAbiVersion"
    Write-AzurPilotDetail -Message "каталог сборки: $($presets.BinaryDirectory)"

    # --- Build -----------------------------------------------------------------

    Write-AzurPilotInfo -Message "сборка native targets (конфигурация $Configuration)"
    $null = Invoke-AzurPilotExternalCommand -FilePath $cmake.Path `
        -ArgumentList @('--build', '--preset', $presets.BuildPreset) `
        -WorkingDirectory $layout.NativeSourceDirectory `
        -Description "сборка native части (CMake, $Configuration)"

    $nativeLibraryPath = Join-Path (Join-Path $layout.NativeOutputDirectory $Configuration) $layout.NativeLibraryFileName
    if (-not (Test-Path -LiteralPath $nativeLibraryPath -PathType Leaf)) {
        throw "После сборки не найден '$nativeLibraryPath'. Native boundary обязана выпускать ровно этот артефакт (контракт имени — native/include/azurpilot_native_abi.h)."
    }
    Write-AzurPilotSuccess -Message "native библиотека собрана: $nativeLibraryPath"

    # --- CTest -----------------------------------------------------------------

    if ($SkipTests) {
        Write-AzurPilotWarning -Message 'native CTest пропущен (-SkipTests): сборка не доказывает исполнение native кода'
    }
    else {
        Write-AzurPilotInfo -Message "native тесты (CTest, конфигурация $Configuration)"
        $null = Invoke-AzurPilotExternalCommand -FilePath 'ctest' `
            -ArgumentList @('--preset', $presets.TestPreset) `
            -WorkingDirectory $layout.NativeSourceDirectory `
            -Description "native CTest ($Configuration)"
        Write-AzurPilotSuccess -Message 'native CTest пройден'
    }

    Write-AzurPilotSuccess -Message "native часть собрана: $($layout.NativeOutputDirectory)\$Configuration"
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
