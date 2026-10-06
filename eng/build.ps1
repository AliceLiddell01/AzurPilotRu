# =============================================================================
# eng/build.ps1 — канонический build workflow AzurPilotRu.
#
# Канонический entrypoint (.codex/context/architecture.md):
#     pwsh ./eng/build.ps1 -Configuration Release
#
# Путь сборки целиком воспроизводим и состоит из шагов:
#   1. проверка toolchain (dotnet, standalone CMake, MSVC через vswhere, Ninja как
#      альтернативный путь) — с диагностикой при отсутствии или старой версии и без
#      молчаливого fallback;
#   2. получение закреплённых native dependencies (OpenCV с обязательной проверкой SHA256);
#   3. сборка native части (CMake configure/build) и native CTest;
#   4. staging native runtime в выход managed части (свойство AzurPilotNativeRuntimeDir);
#   5. restore и сборка managed solution;
#   6. managed тесты, доказывающие interop boundary.
#
# Логика сборки живёт только в eng/: прямые dotnet/cmake команды допустимы для локальной
# диагностики, но documented canonical path проходит через этот скрипт
# (.codex/context/architecture.md). Скрипт работает из любого текущего каталога и не
# предполагает букву диска.
#
# Все генерируемые артефакты остаются внутри единственной ignored boundary artifacts/
# и стандартных managed bin/obj (закрыты .gitignore).
# =============================================================================

[CmdletBinding()]
param(
    # Конфигурация сборки: Debug или Release.
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',

    # Не запускать native CTest и managed тесты (сборка без доказательств исполнения).
    [switch]$SkipTests,

    # Не собирать native часть: используется уже собранный native runtime, если он есть.
    [switch]$SkipNative,

    # Удалить build outputs перед сборкой: native build directories, staging native runtime и
    # managed bin/obj. Полученные закреплённые зависимости (artifacts/opencv) сохраняются:
    # повторное скачивание закреплённого пакета не является частью очистки сборки.
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    . (Join-Path $PSScriptRoot 'common.ps1')
    Assert-AzurPilotEnvironment

    $versions = Get-AzurPilotVersions
    $layout = Get-AzurPilotArtifactsLayout -Versions $versions -Configuration $Configuration
    $repositoryRoot = $layout.RepositoryRoot
    $powerShellPath = Get-AzurPilotPowerShellPath

    # --- План шагов: количество шагов известно заранее, чтобы журнал был структурированным ---

    $totalSteps = 1
    if (-not $SkipNative) { $totalSteps += 2 }
    $totalSteps += 1
    $totalSteps += 1
    if (-not $SkipTests) { $totalSteps += 1 }

    $step = 0

    Write-AzurPilotInfo -Message "канонический build workflow: pwsh ./eng/build.ps1 -Configuration $Configuration"
    Write-AzurPilotDetail -Message "корень репозитория: $repositoryRoot"
    Write-AzurPilotDetail -Message "владелец закреплённых версий: eng/versions.json"
    Write-AzurPilotDetail -Message ".NET SDK $($versions.DotNetSdkVersion) (rollForward=$($versions.DotNetSdkRollForward)); CMake >= $($versions.CMakeMinimumVersion); generator «$($versions.CMakeGenerator)»; MSVC >= $($versions.MsvcMinimumVersion); Ninja >= $($versions.NinjaMinimumVersion); OpenCV $($versions.OpenCvVersion); ABI v$($versions.NativeAbiVersion)"

    if ($SkipNative) { Write-AzurPilotWarning -Message 'native часть не собирается (-SkipNative): используется ранее собранный native runtime, если он есть' }
    if ($SkipTests) { Write-AzurPilotWarning -Message 'тесты не запускаются (-SkipTests): сборка не доказывает исполнение native кода и interop boundary' }

    if ($Clean) {
        Write-AzurPilotInfo -Message 'очистка build outputs (-Clean)'
        $removed = Clear-AzurPilotBuildOutputs -Layout $layout -Configuration $Configuration
        foreach ($directory in $removed) { Write-AzurPilotDetail -Message "удалён каталог: $directory" }
        Write-AzurPilotDetail -Message "сохранены закреплённые зависимости: $($layout.OpenCvRoot)"
    }

    # --- Шаг: проверка toolchain ----------------------------------------------

    $step++
    Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Проверка toolchain'

    $dotnet = Resolve-AzurPilotDotNetToolchain -Versions $versions

    if (-not $SkipNative) {
        $null = Resolve-AzurPilotCMakeToolchain -Versions $versions
        $visualStudio = Resolve-AzurPilotVisualStudioToolchain -Versions $versions
        $ninja = Resolve-AzurPilotNinjaToolchain -Versions $versions -VisualStudioInstancePath $visualStudio.InstancePath
        if ($ninja.Available) { Write-AzurPilotSuccess -Message $ninja.Message } else { Write-AzurPilotWarning -Message $ninja.Message }
    }
    else {
        Write-AzurPilotInfo -Message 'проверка CMake, MSVC и Ninja пропущена: native часть не собирается (-SkipNative)'
    }

    # --- Шаг: получение закреплённых native dependencies -----------------------

    if (-not $SkipNative) {
        $step++
        Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Получение закреплённых native dependencies (OpenCV)'
        $null = Invoke-AzurPilotExternalCommand -FilePath $powerShellPath `
            -ArgumentList @('-File', $layout.DependenciesScriptPath) `
            -WorkingDirectory $repositoryRoot `
            -Description 'получение закреплённых native dependencies'
    }

    # --- Шаг: сборка native части и native CTest -------------------------------

    if (-not $SkipNative) {
        $step++
        Write-AzurPilotStep -Number $step -Total $totalSteps -Title "Сборка native части и native CTest (конфигурация $Configuration)"

        $nativeArguments = @('-File', $layout.NativeBuildScriptPath, '-Configuration', $Configuration)
        if ($SkipTests) { $nativeArguments += '-SkipTests' }

        $null = Invoke-AzurPilotExternalCommand -FilePath $powerShellPath `
            -ArgumentList $nativeArguments `
            -WorkingDirectory $repositoryRoot `
            -Description "сборка native части (конфигурация $Configuration)"
    }

    # --- Шаг: staging native runtime ------------------------------------------

    $step++
    Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Staging native runtime в выход managed части'

    $nativeOutputDirectory = Join-Path $layout.NativeOutputDirectory $Configuration
    $nativeLibraryInOutput = Join-Path $nativeOutputDirectory $layout.NativeLibraryFileName
    $stagingDirectory = $null

    if (Test-Path -LiteralPath $nativeLibraryInOutput -PathType Leaf) {
        $staging = Sync-AzurPilotNativeRuntimeStaging -Layout $layout -Configuration $Configuration -NativeLibraryFileName $layout.NativeLibraryFileName
        $stagingDirectory = $staging.Directory
        Write-AzurPilotSuccess -Message "staging подготовлен: $stagingDirectory"
        Write-AzurPilotDetail -Message "скопировано файлов: $($staging.Copied); удалено устаревших: $($staging.Removed)"
        Write-AzurPilotDetail -Message "файлы: $($staging.Files -join ', ')"
    }
    elseif (-not $SkipNative) {
        throw "Native сборка отработала, но не найден '$nativeLibraryInOutput': staging native runtime невозможен."
    }
    else {
        $stagingLibrary = Join-Path $layout.NativeStagingDirectory $layout.NativeLibraryFileName
        if (Test-Path -LiteralPath $stagingLibrary -PathType Leaf) {
            $stagingDirectory = $layout.NativeStagingDirectory
            Write-AzurPilotInfo -Message "используется ранее подготовленный staging (-SkipNative): $stagingDirectory"
        }
        elseif (-not $SkipTests) {
            throw @(
                'Native runtime не найден: managed тесты не смогли бы доказать interop boundary, а ложный зелёный результат запрещён.',
                "Проверено: '$nativeLibraryInOutput' и '$stagingLibrary'",
                'Как исправить: запустите сборку без -SkipNative либо добавьте -SkipTests, если native часть намеренно не собирается.'
            ) -join "`n"
        }
        else {
            Write-AzurPilotWarning -Message 'staging native runtime недоступен: managed часть собирается без native библиотеки (-SkipNative -SkipTests)'
        }
    }

    # --- Шаг: сборка managed solution -----------------------------------------

    $step++
    Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Сборка managed solution (restore + build)'

    $null = Invoke-AzurPilotExternalCommand -FilePath $dotnet.Path `
        -ArgumentList @('restore', $layout.SolutionPath) `
        -WorkingDirectory $repositoryRoot `
        -Description 'restore managed solution'

    $managedBuildArguments = @('build', $layout.SolutionPath, '-c', $Configuration, '--no-restore')
    if ($stagingDirectory) { $managedBuildArguments += "-p:AzurPilotNativeRuntimeDir=$stagingDirectory" }

    $null = Invoke-AzurPilotExternalCommand -FilePath $dotnet.Path `
        -ArgumentList $managedBuildArguments `
        -WorkingDirectory $repositoryRoot `
        -Description "сборка managed solution (конфигурация $Configuration)"

    Write-AzurPilotSuccess -Message 'managed solution собран'

    # --- Шаг: managed тесты ----------------------------------------------------

    if (-not $SkipTests) {
        $step++
        Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Managed тесты (interop boundary)'

        # Выбор runner'а — global.json ("test": {"runner": "Microsoft.Testing.Platform"}): .NET 10
        # SDK с xunit.v3 работает только в режиме Microsoft.Testing.Platform. Опция --nologo не
        # передаётся: в этом режиме неизвестная опция уходит приложению и валит запуск кодом 5.
        # Проба tests/AzurPilot.NativeAbsenceProbe не является тестовым проектом и здесь не
        # запускается: её вызывает негативный interop тест из отдельного процесса.
        $managedTestArguments = @('test', $layout.ManagedTestProjectPath, '-c', $Configuration)
        if ($stagingDirectory) { $managedTestArguments += "-p:AzurPilotNativeRuntimeDir=$stagingDirectory" }

        $null = Invoke-AzurPilotExternalCommand -FilePath $dotnet.Path `
            -ArgumentList $managedTestArguments `
            -WorkingDirectory $repositoryRoot `
            -Description "managed тесты (конфигурация $Configuration)"

        Write-AzurPilotSuccess -Message 'managed тесты пройдены'
    }

    # --- Итог -----------------------------------------------------------------

    Write-AzurPilotSuccess -Message "Сборка и проверки завершены успешно (конфигурация $Configuration)"
    Write-AzurPilotDetail -Message "native библиотека: $nativeLibraryInOutput"
    if ($stagingDirectory) { Write-AzurPilotDetail -Message "staging native runtime: $stagingDirectory" }
    Write-AzurPilotDetail -Message "закреплённый пакет OpenCV: $($layout.OpenCvRoot)"
    Write-AzurPilotDetail -Message "каталог сборки native части: $($layout.NativeBuildDirectory)"

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
