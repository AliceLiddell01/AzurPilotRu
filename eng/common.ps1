# =============================================================================
# eng/common.ps1 — общие helpers канонического PowerShell workflow AzurPilotRu.
#
# Ответственность этого файла:
#   - корень репозитория вычисляется от каталога самого файла ($PSScriptRoot), а не от
#     текущей рабочей директории: скрипты обязаны работать из любого cwd
#     (.codex/context/architecture.md);
#   - чтение закреплённых версий из eng/versions.json — единственного источника версий;
#   - структурированное русское логирование шагов и внешних команд;
#   - единая обёртка запуска внешних команд с проверкой exit code;
#   - разрешение toolchain (CMake, .NET SDK, MSVC через vswhere, Ninja, tar) без хардкода
#     machine-specific путей, без буквы диска и без молчаливого fallback
#     (.codex/context/build-contracts.md, раздел «Диагностика при отсутствующем или старом toolchain»).
#
# Файл не является entrypoint: он dot-sourced скриптами eng/. Каждый entrypoint обязан
# вызвать Assert-AzurPilotEnvironment первым шагом.
#
# Язык: комментарии, help-блоки и диагностика — по-русски; идентификаторы — латиницей
# (.codex/context/language.md).
# =============================================================================

Set-StrictMode -Version Latest

# -----------------------------------------------------------------------------
# Состояние, вычисляемое при dot-source
# -----------------------------------------------------------------------------

$script:AzurPilotEngDirectory = $PSScriptRoot
$script:AzurPilotRepositoryRootPath = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path -LiteralPath (Join-Path $script:AzurPilotEngDirectory 'versions.json') -PathType Leaf)) {
    throw "eng/common.ps1 обязан лежать рядом с eng/versions.json (единственный источник закреплённых версий). Обнаружен каталог: $script:AzurPilotEngDirectory"
}

# -----------------------------------------------------------------------------
# Окружение
# -----------------------------------------------------------------------------

function Assert-AzurPilotEnvironment {
    <#
    .SYNOPSIS
        Проверяет обязательные свойства окружения: PowerShell 7+, Windows, x64.
    .DESCRIPTION
        Проверка даёт понятную русскую ошибку вместо загадочного падения где-то в середине
        сборки. Фундамент Windows-only x64, других платформ в цели сборки нет.
    #>
    [CmdletBinding()]
    param()

    if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion.Major -lt 7) {
        throw "Требуется PowerShell 7 или новее (pwsh). Обнаружено: PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition)). Канонический путь сборки — pwsh ./eng/build.ps1 -Configuration Release."
    }

    if (-not $IsWindows) {
        throw "AzurPilotRu собирается только на Windows x64 (.codex/context/architecture.md). Обнаружена операционная система: $($PSVersionTable.OS)."
    }

    if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
        throw "AzurPilotRu собирается только для x64 (.codex/context/architecture.md). Архитектура процесса: '$env:PROCESSOR_ARCHITECTURE'."
    }
}

function Get-AzurPilotRepositoryRoot {
    <#
    .SYNOPSIS
        Возвращает абсолютный путь к корню репозитория AzurPilotRu.
    #>
    [CmdletBinding()]
    param()

    if (-not $script:AzurPilotRepositoryRootPath) {
        throw "Корень репозитория не вычислен: eng/common.ps1 не был dot-sourced корректно."
    }

    return $script:AzurPilotRepositoryRootPath
}

function Get-AzurPilotPowerShellPath {
    <#
    .SYNOPSIS
        Возвращает путь к текущему pwsh для запуска дочерних скриптов eng/.
    #>
    [CmdletBinding()]
    param()

    $command = Get-Command -Name 'pwsh' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }

    $fromHome = Join-Path $PSHOME 'pwsh.exe'
    if (Test-Path -LiteralPath $fromHome -PathType Leaf) { return $fromHome }

    throw "Не найден pwsh.exe: дочерние скрипты eng/ запускаются тем же PowerShell 7, которым запущен канонический путь."
}

# -----------------------------------------------------------------------------
# Логирование
# -----------------------------------------------------------------------------

function Write-AzurPilotLine {
    <#
    .SYNOPSIS
        Внутренняя функция вывода одной структурированной строки журнала.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$Color = 'Gray'
    )

    Write-Host "[azurpilot] $Tag — $Message" -ForegroundColor $Color
}

function Write-AzurPilotStep {
    <#
    .SYNOPSIS
        Отмечает начало шага канонического workflow.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][int]$Number,
        [Parameter(Mandatory = $true)][int]$Total,
        [Parameter(Mandatory = $true)][string]$Title
    )

    Write-Host "[azurpilot] шаг $Number/$Total — $Title" -ForegroundColor Cyan
}

function Write-AzurPilotInfo {
    <#
    .SYNOPSIS
        Информационное сообщение.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-AzurPilotLine -Tag 'сведения' -Message $Message -Color Gray
}

function Write-AzurPilotDetail {
    <#
    .SYNOPSIS
        Дополнительная деталь (отступ для читаемости журнала).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Host "             $Message" -ForegroundColor DarkGray
}

function Write-AzurPilotSuccess {
    <#
    .SYNOPSIS
        Сообщение об успешном результате проверки или шага.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-AzurPilotLine -Tag 'ок' -Message $Message -Color Green
}

function Write-AzurPilotWarning {
    <#
    .SYNOPSIS
        Предупреждение: не блокирует канонический путь, но обязано быть видимым.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-AzurPilotLine -Tag 'внимание' -Message $Message -Color Yellow
}

function Write-AzurPilotError {
    <#
    .SYNOPSIS
        Сообщение об ошибке.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-AzurPilotLine -Tag 'ошибка' -Message $Message -Color Red
}

function Format-AzurPilotCommandLine {
    <#
    .SYNOPSIS
        Собирает отображаемую командную строку для журнала и диагностики.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @()
    )

    $parts = @($FilePath)
    foreach ($argument in $ArgumentList) {
        if ($argument -match '\s') { $parts += '"' + $argument + '"' } else { $parts += $argument }
    }

    return ($parts -join ' ')
}

# -----------------------------------------------------------------------------
# Запуск внешних команд
# -----------------------------------------------------------------------------

function Invoke-AzurPilotExternalCommand {
    <#
    .SYNOPSIS
        Запускает внешнюю команду и проверяет её exit code.
    .DESCRIPTION
        Единая точка запуска инструментов: журналирует команду, при необходимости захватывает
        вывод, проверяет код возврата и при нарушении падает с русской диагностикой, называющей
        шаг, команду и код. Скрытых подстановок нет: запускается ровно тот исполняемый файл,
        который передан.
    .PARAMETER FilePath
        Путь к исполняемому файлу или имя команды, разрешаемое через PATH.
    .PARAMETER ArgumentList
        Массив аргументов. Передаётся без shell-разбора: кавычки и подстановки не интерпретируются.
    .PARAMETER WorkingDirectory
        Рабочий каталог команды. Текущий каталог восстанавливается после запуска.
    .PARAMETER Description
        Русское описание шага для диагностики.
    .PARAMETER Capture
        Захватить вывод (stdout+stderr) в результат вместо прямой трансляции в консоль.
    .PARAMETER AllowFailure
        Вернуть результат с ненулевым кодом вместо исключения (для проб версий).
    .PARAMETER SuccessExitCodes
        Коды возврата, считающиеся успешными. По умолчанию только 0.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$Capture,
        [switch]$AllowFailure,
        [int[]]$SuccessExitCodes = @(0)
    )

    $resolved = $FilePath
    if (Test-Path -LiteralPath $FilePath -PathType Leaf) {
        $resolved = (Resolve-Path -LiteralPath $FilePath).Path
    }
    elseif (-not (Get-Command -Name $FilePath -CommandType Application -ErrorAction SilentlyContinue)) {
        throw "Не найден исполняемый файл '$FilePath' для шага «$Description»."
    }

    $commandLine = Format-AzurPilotCommandLine -FilePath $resolved -ArgumentList $ArgumentList
    Write-AzurPilotDetail -Message "выполняется: $commandLine"

    $previousLocation = $null
    if ($WorkingDirectory) {
        if (-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) {
            throw "Не найден рабочий каталог '$WorkingDirectory' для шага «$Description»."
        }
        $previousLocation = Get-Location
        Set-Location -LiteralPath $WorkingDirectory
    }

    $captured = $null
    try {
        # ErrorActionPreference ослабляется только на время запуска внешнего инструмента:
        # строки stderr инструмента не должны превращаться в terminating error PowerShell.
        $ErrorActionPreference = 'Continue'

        # Внешний процесс обновляет $LASTEXITCODE в глобальной области, а не в области функции.
        # Инициализация и чтение идут через $global: иначе локальная копия скрывала бы реальный
        # отказ инструмента и любой сбой выглядел бы успехом.
        $global:LASTEXITCODE = 0

        try {
            if ($Capture) {
                $captured = & $resolved @ArgumentList 2>&1 | ForEach-Object { [string]$_ }
            }
            else {
                # Вывод инструмента идёт в журнал напрямую (host), а не в поток результата функции:
                # иначе он терялся бы у вызывающего кода, которому нужен только объект результата.
                & $resolved @ArgumentList 2>&1 | ForEach-Object { Write-Host ([string]$_) }
            }
            $exitCode = $global:LASTEXITCODE
        }
        catch [System.Management.Automation.CommandNotFoundException] {
            throw "Не удалось запустить '$resolved' для шага «$Description»: исполняемый файл не найден или не является исполняемым. $($_.Exception.Message)"
        }
    }
    finally {
        if ($previousLocation) { Set-Location -LiteralPath $previousLocation.Path }
    }

    $output = @()
    if ($null -ne $captured) { $output = @($captured) }

    $result = [pscustomobject]@{
        FilePath    = $resolved
        ExitCode    = $exitCode
        Output      = $output
        Text        = ($output -join "`n")
        CommandLine = $commandLine
        Description = $Description
    }

    if (-not $AllowFailure -and $SuccessExitCodes -notcontains $exitCode) {
        $message = @(
            "Шаг «$Description» завершился с кодом $exitCode (ожидался: $($SuccessExitCodes -join ', ')).",
            "Команда: $commandLine"
        )
        if ($output.Count -gt 0) {
            $message += "Вывод инструмента:"
            $message += ($output | Select-Object -Last 40 | ForEach-Object { "  $_" })
        }
        throw ($message -join "`n")
    }

    return $result
}

# -----------------------------------------------------------------------------
# Версии: разбор и сравнение
# -----------------------------------------------------------------------------

function ConvertTo-AzurPilotVersionParts {
    <#
    .SYNOPSIS
        Разбирает строку версии в числовые компоненты, переживая произвольный хвост.
    .DESCRIPTION
        Версии инструментов приходят строками с суффиксом: CMake из состава Visual Studio отдаёт
        номер с хвостом вида "-msvc1", Ninja — с хвостом вида ".git.<сборка>". Числовая часть берётся
        ведущим префиксом major[.minor[.patch[.revision]]], хвост сохраняется только для диагностики.
        Если числового префикса нет, это ошибка с исходной строкой, а не тихий пропуск проверки.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$VersionText,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($VersionText -notmatch '^(?<numbers>[0-9]+(?:\.[0-9]+){0,3})(?<suffix>.*)$') {
        throw "Не удалось разобрать $Description из строки '$VersionText': ожидается числовая версия вида major[.minor[.patch]] в начале строки."
    }

    $numbers = $Matches['numbers']
    $suffix = $Matches['suffix']
    $components = $numbers.Split('.')

    $values = @(0, 0, 0, 0)
    for ($index = 0; $index -lt $components.Count; $index++) { $values[$index] = [int]$components[$index] }

    return [pscustomobject]@{
        Text        = $VersionText
        NumericText = $numbers
        Major       = $values[0]
        Minor       = $values[1]
        Patch       = $values[2]
        Revision    = $values[3]
        Suffix      = $suffix
    }
}

function Compare-AzurPilotVersion {
    <#
    .SYNOPSIS
        Сравнивает две разобранные версии: -1, 0 или 1.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Left,
        [Parameter(Mandatory = $true)] $Right
    )

    foreach ($field in @('Major', 'Minor', 'Patch', 'Revision')) {
        if ($Left.$field -lt $Right.$field) { return -1 }
        if ($Left.$field -gt $Right.$field) { return 1 }
    }

    return 0
}

function Assert-AzurPilotMinimumVersion {
    <#
    .SYNOPSIS
        Проверяет, что найденная версия не старше минимума, и падает с диагностикой.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FoundVersionText,
        [Parameter(Mandatory = $true)][string]$MinimumVersionText,
        [Parameter(Mandatory = $true)][string]$Component,
        [Parameter(Mandatory = $true)][string]$Owner,
        [string]$FoundPath,
        [string]$FixHint
    )

    $found = ConvertTo-AzurPilotVersionParts -VersionText $FoundVersionText -Description $Component
    $minimum = ConvertTo-AzurPilotVersionParts -VersionText $MinimumVersionText -Description "$Component (минимум)"

    if ((Compare-AzurPilotVersion -Left $found -Right $minimum) -lt 0) {
        $message = @("$Component $FoundVersionText старше требуемого минимума $MinimumVersionText.")
        if ($FoundPath) { $message += "Найдено: $FoundPath" }
        $message += "Владелец значения: $Owner."
        if ($FixHint) { $message += "Как исправить: $FixHint" }
        throw ($message -join "`n")
    }

    return $found
}

# -----------------------------------------------------------------------------
# Закреплённые версии: eng/versions.json — единственный источник
# -----------------------------------------------------------------------------

function Get-AzurPilotRequiredProperty {
    <#
    .SYNOPSIS
        Возвращает обязательное свойство из прочитанного JSON или падает с русской диагностикой.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $InputObject,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$SourcePath
    )

    if ($null -eq $InputObject -or -not ($InputObject.PSObject.Properties.Name -contains $Name)) {
        throw "В $SourcePath отсутствует обязательное поле «$Description». Владелец закреплённых значений — eng/versions.json; контракт версий нарушен (.codex/context/build-contracts.md)."
    }

    return $InputObject.$Name
}

function Get-AzurPilotVersions {
    <#
    .SYNOPSIS
        Читает и валидирует eng/versions.json.
    .DESCRIPTION
        Возвращает структурированный объект закреплённых значений. Скрипты eng/ передают значения
        дальше параметрами и не хранят их копий: владелец каждого номера — этот файл.
    #>
    [CmdletBinding()]
    param([string]$Path)

    if (-not $Path) {
        $Path = Join-Path (Join-Path (Get-AzurPilotRepositoryRoot) 'eng') 'versions.json'
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Не найден файл закреплённых версий '$Path'. Владелец версий — eng/versions.json в корне репозитория."
    }

    $json = $null
    try {
        $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Не удалось прочитать '$Path' как JSON: $($_.Exception.Message)"
    }

    $dotnetSdk = Get-AzurPilotRequiredProperty -InputObject $json -Name 'dotnetSdk' -Description 'dotnetSdk' -SourcePath $Path
    $toolchain = Get-AzurPilotRequiredProperty -InputObject $json -Name 'toolchain' -Description 'toolchain' -SourcePath $Path
    $openCv = Get-AzurPilotRequiredProperty -InputObject $json -Name 'opencv' -Description 'opencv' -SourcePath $Path
    $nativeAbi = Get-AzurPilotRequiredProperty -InputObject $json -Name 'nativeAbi' -Description 'nativeAbi' -SourcePath $Path

    $versions = [pscustomobject]@{
        Path                      = (Resolve-Path -LiteralPath $Path).Path
        DotNetSdkVersion          = [string](Get-AzurPilotRequiredProperty -InputObject $dotnetSdk -Name 'version' -Description 'dotnetSdk.version' -SourcePath $Path)
        DotNetSdkRollForward      = [string](Get-AzurPilotRequiredProperty -InputObject $dotnetSdk -Name 'rollForward' -Description 'dotnetSdk.rollForward' -SourcePath $Path)
        MsvcMinimumVersion        = [string](Get-AzurPilotRequiredProperty -InputObject $toolchain -Name 'msvcMinimumVersion' -Description 'toolchain.msvcMinimumVersion' -SourcePath $Path)
        MsvcReferenceVersion      = [string](Get-AzurPilotRequiredProperty -InputObject $toolchain -Name 'msvcReferenceVersion' -Description 'toolchain.msvcReferenceVersion' -SourcePath $Path)
        CMakeMinimumVersion       = [string](Get-AzurPilotRequiredProperty -InputObject $toolchain -Name 'cmakeMinimumVersion' -Description 'toolchain.cmakeMinimumVersion' -SourcePath $Path)
        CMakeLatestStable         = [string](Get-AzurPilotRequiredProperty -InputObject $toolchain -Name 'cmakeLatestStable' -Description 'toolchain.cmakeLatestStable' -SourcePath $Path)
        CMakeGenerator            = [string](Get-AzurPilotRequiredProperty -InputObject $toolchain -Name 'cmakeGenerator' -Description 'toolchain.cmakeGenerator' -SourcePath $Path)
        CMakeGeneratorAlternative = [string](Get-AzurPilotRequiredProperty -InputObject $toolchain -Name 'cmakeGeneratorAlternative' -Description 'toolchain.cmakeGeneratorAlternative' -SourcePath $Path)
        NinjaMinimumVersion       = [string](Get-AzurPilotRequiredProperty -InputObject $toolchain -Name 'ninjaMinimumVersion' -Description 'toolchain.ninjaMinimumVersion' -SourcePath $Path)
        OpenCvVersion             = [string](Get-AzurPilotRequiredProperty -InputObject $openCv -Name 'version' -Description 'opencv.version' -SourcePath $Path)
        OpenCvUrl                 = [string](Get-AzurPilotRequiredProperty -InputObject $openCv -Name 'url' -Description 'opencv.url' -SourcePath $Path)
        OpenCvSha256              = [string](Get-AzurPilotRequiredProperty -InputObject $openCv -Name 'sha256' -Description 'opencv.sha256' -SourcePath $Path)
        OpenCvArchiveRoot         = [string](Get-AzurPilotRequiredProperty -InputObject $openCv -Name 'archiveRoot' -Description 'opencv.archiveRoot' -SourcePath $Path)
        OpenCvCMakeDir            = [string](Get-AzurPilotRequiredProperty -InputObject $openCv -Name 'cmakeDir' -Description 'opencv.cmakeDir' -SourcePath $Path)
        OpenCvRuntimeDir          = [string](Get-AzurPilotRequiredProperty -InputObject $openCv -Name 'runtimeDir' -Description 'opencv.runtimeDir' -SourcePath $Path)
        NativeAbiVersion          = (Get-AzurPilotRequiredProperty -InputObject $nativeAbi -Name 'version' -Description 'nativeAbi.version' -SourcePath $Path)
        NativeAbiCapabilities     = @(Get-AzurPilotRequiredProperty -InputObject $nativeAbi -Name 'capabilities' -Description 'nativeAbi.capabilities' -SourcePath $Path)
    }

    if ($versions.DotNetSdkVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+') {
        throw "Значение dotnetSdk.version в '$Path' ('$($versions.DotNetSdkVersion)') не является версией вида major.minor.patch."
    }

    if ($versions.OpenCvUrl -match '(?i)(latest|/master|/main)') {
        throw "Значение opencv.url в '$Path' ('$($versions.OpenCvUrl)') содержит плавающий указатель (latest/master/main). Плавающие URL как build dependency запрещены (.codex/context/build-contracts.md)."
    }

    if ($versions.OpenCvSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Значение opencv.sha256 в '$Path' ('$($versions.OpenCvSha256)') не является SHA256 (64 шестнадцатеричных символа)."
    }

    if ($versions.NativeAbiVersion -isnot [int] -and $versions.NativeAbiVersion -isnot [long]) {
        throw "Значение nativeAbi.version в '$Path' ('$($versions.NativeAbiVersion)') не является целым числом."
    }

    return $versions
}

function Get-AzurPilotArtifactsLayout {
    <#
    .SYNOPSIS
        Вычисляет пути внутри единственной ignored boundary artifacts/.
    .DESCRIPTION
        Все пути выводятся из корня репозитория и значений eng/versions.json. Абсолютных
        machine-specific путей и буквы диска в репозитории нет (.codex/context/architecture.md).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Versions,
        [string]$Configuration
    )

    $root = Get-AzurPilotRepositoryRoot
    $artifactsRoot = Join-Path $root 'artifacts'
    $openCvRoot = Join-Path (Join-Path $artifactsRoot 'opencv') $Versions.OpenCvVersion
    $nativeRoot = Join-Path $artifactsRoot 'native'
    $downloadDirectory = Join-Path $artifactsRoot 'downloads'
    $archiveFileName = ($Versions.OpenCvUrl -split '/')[-1]

    $stagingDirectory = $null
    if ($Configuration) { $stagingDirectory = Join-Path (Join-Path $nativeRoot 'runtime') $Configuration }

    return [pscustomobject]@{
        RepositoryRoot            = $root
        ArtifactsRoot             = $artifactsRoot
        OpenCvRoot                = $openCvRoot
        OpenCvUnpackedRoot        = Join-Path $openCvRoot $Versions.OpenCvArchiveRoot
        OpenCvCMakeDirectory      = Join-Path $openCvRoot $Versions.OpenCvCMakeDir
        OpenCvRuntimeDirectory    = Join-Path $openCvRoot $Versions.OpenCvRuntimeDir
        OpenCvDownloadDirectory   = $downloadDirectory
        OpenCvArchivePath         = Join-Path $downloadDirectory $archiveFileName
        NativeBuildDirectory      = Join-Path $nativeRoot 'cmake'
        NativeOutputDirectory     = Join-Path $nativeRoot 'bin'
        NativeStagingDirectory    = $stagingDirectory
        NativeSourceDirectory     = Join-Path $root 'native'
        NativePresetsPath         = Join-Path (Join-Path $root 'native') 'CMakePresets.json'
        SolutionPath              = Join-Path $root 'AzurPilot.slnx'
        ManagedTestProjectPath    = Join-Path (Join-Path (Join-Path $root 'tests') 'AzurPilot.Tests') 'AzurPilot.Tests.csproj'
        ManagedSourceRoot         = Join-Path $root 'src'
        ManagedTestRoot           = Join-Path $root 'tests'
        DependenciesScriptPath    = Join-Path $script:AzurPilotEngDirectory 'Get-NativeDependencies.ps1'
        NativeBuildScriptPath     = Join-Path $script:AzurPilotEngDirectory 'Invoke-NativeBuild.ps1'
        NativeLibraryFileName     = 'AzurPilot.Native.dll'
    }
}

# -----------------------------------------------------------------------------
# Toolchain: .NET SDK
# -----------------------------------------------------------------------------

function Resolve-AzurPilotDotNetToolchain {
    <#
    .SYNOPSIS
        Находит .NET SDK, соответствующий закреплённой версии и политике rollForward.
    .DESCRIPTION
        Версия SDK, выбранная global.json, определяется запуском `dotnet --version` из корня
        репозитория. Проверка идёт по правилу dotnetSdk.rollForward = latestPatch: та же feature
        band, патч не ниже закреплённого. Другая feature band, другой minor или major — ошибка,
        а не молчаливый roll-forward.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Versions,
        [string]$ExplicitPath
    )

    $root = Get-AzurPilotRepositoryRoot
    $pinned = ConvertTo-AzurPilotVersionParts -VersionText $Versions.DotNetSdkVersion -Description '.NET SDK (закреплённая версия)'
    $pinnedFeatureBand = $pinned.Patch - ($pinned.Patch % 100)

    $candidates = @()
    if ($ExplicitPath) { $candidates += $ExplicitPath }
    $fromPath = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($fromPath) { $candidates += $fromPath.Source }
    $candidates = @($candidates | Select-Object -Unique)

    if ($candidates.Count -eq 0) {
        throw "Не найден dotnet в PATH. Требуется .NET SDK $($Versions.DotNetSdkVersion) (владелец значения: eng/versions.json: dotnetSdk.version; потребитель — global.json). Как исправить: установите .NET SDK закреплённой feature band и повторите запуск."
    }

    $diagnostics = @()
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $diagnostics += "$candidate — файл не найден"
            continue
        }

        # Рабочий каталог — корень репозитория: версию SDK выбирает global.json, а он ищется
        # от текущего каталога.
        $probe = Invoke-AzurPilotExternalCommand -FilePath $candidate -ArgumentList @('--version') -WorkingDirectory $root -Description "определение версии .NET SDK ($candidate)" -Capture -AllowFailure

        if ($probe.ExitCode -ne 0) {
            $detail = ($probe.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' / '
            $diagnostics += "$candidate — не удалось определить версию SDK (exit $($probe.ExitCode)): $detail"
            continue
        }

        $firstLine = ($probe.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        if (-not $firstLine) {
            $diagnostics += "$candidate — пустой вывод версии SDK"
            continue
        }

        $versionText = $firstLine.Trim()
        $found = ConvertTo-AzurPilotVersionParts -VersionText $versionText -Description '.NET SDK'

        if ($found.Major -ne $pinned.Major -or $found.Minor -ne $pinned.Minor) {
            $diagnostics += "$candidate — SDK $versionText не соответствует пину $($Versions.DotNetSdkVersion): другая линия major/minor"
            continue
        }

        $foundFeatureBand = $found.Patch - ($found.Patch % 100)
        if ($foundFeatureBand -ne $pinnedFeatureBand) {
            $diagnostics += "$candidate — SDK $versionText не соответствует пину $($Versions.DotNetSdkVersion): другая feature band (rollForward=$($Versions.DotNetSdkRollForward))"
            continue
        }

        if ($found.Patch -lt $pinned.Patch) {
            $diagnostics += "$candidate — SDK $versionText старше пина $($Versions.DotNetSdkVersion)"
            continue
        }

        Write-AzurPilotSuccess -Message ".NET SDK $versionText соответствует закреплённой версии $($Versions.DotNetSdkVersion) (rollForward=$($Versions.DotNetSdkRollForward))"
        return [pscustomobject]@{ Path = $candidate; VersionText = $versionText; Version = $found }
    }

    $lines = @()
    $lines += "Не найден .NET SDK, соответствующий закреплённой версии $($Versions.DotNetSdkVersion)."
    $lines += "Владелец значения: eng/versions.json: dotnetSdk.version; потребитель — global.json (rollForward=$($Versions.DotNetSdkRollForward))."
    $lines += "Проверенные кандидаты:"
    foreach ($item in $diagnostics) { $lines += "  - $item" }
    $lines += "Как исправить: установите .NET SDK $($Versions.DotNetSdkVersion) (или патч той же feature band) и повторите запуск."
    throw ($lines -join "`n")
}

# -----------------------------------------------------------------------------
# Toolchain: CMake
# -----------------------------------------------------------------------------

function Resolve-AzurPilotCMakeToolchain {
    <#
    .SYNOPSIS
        Находит standalone CMake не ниже закреплённого минимума.
    .DESCRIPTION
        Кандидаты ищутся явно: явный путь, PATH, стандартные каталоги установки standalone CMake,
        выведенные из переменных окружения. CMake из состава Visual Studio
        (Common7/IDE/CommonExtensions/Microsoft/CMake) не используется ни в каком случае — даже
        если его версия формально проходит: canonical build обязан брать standalone CMake не ниже
        toolchain.cmakeMinimumVersion (.codex/context/build-contracts.md). Если подходящий CMake не
        найден, скрипт падает с диагностикой, а не подставляет другой.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Versions,
        [string]$ExplicitPath
    )

    $minimumText = $Versions.CMakeMinimumVersion
    $minimum = ConvertTo-AzurPilotVersionParts -VersionText $minimumText -Description 'CMake (минимум)'
    $owner = 'eng/versions.json: toolchain.cmakeMinimumVersion'

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($ExplicitPath) { $candidates.Add($ExplicitPath) }

    $fromPath = Get-Command -Name 'cmake.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($fromPath) { $candidates.Add($fromPath.Source) }

    foreach ($programFilesDirectory in @($env:ProgramFiles, ${env:ProgramW6432})) {
        if ($programFilesDirectory) { $candidates.Add((Join-Path $programFilesDirectory 'CMake\bin\cmake.exe')) }
    }

    $checked = @()
    $rejected = @()

    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $checked += "  - $candidate — файл не найден"
            continue
        }

        if ($candidate -match '(?i)[\\/]CommonExtensions[\\/]Microsoft[\\/]CMake[\\/]') {
            $rejected += "$candidate — CMake из состава Visual Studio, не используется как подстановка"
            continue
        }

        $probe = Invoke-AzurPilotExternalCommand -FilePath $candidate -ArgumentList @('--version') -Description "определение версии CMake ($candidate)" -Capture -AllowFailure

        if ($probe.ExitCode -ne 0) {
            $checked += "  - $candidate — не удалось определить версию (exit $($probe.ExitCode))"
            continue
        }

        $firstLine = ($probe.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        $versionText = $null
        if ($firstLine -and $firstLine -match '(?i)cmake version\s+(?<version>\S+)') { $versionText = $Matches['version'] }

        if (-not $versionText) {
            $checked += "  - $candidate — не удалось разобрать строку версии: '$firstLine'"
            continue
        }

        $parts = ConvertTo-AzurPilotVersionParts -VersionText $versionText -Description 'CMake'

        if ((Compare-AzurPilotVersion -Left $parts -Right $minimum) -lt 0) {
            $checked += "  - $candidate — CMake $versionText старше минимума $minimumText"
            continue
        }

        Write-AzurPilotSuccess -Message "CMake $versionText найден: $candidate"
        if ($parts.Suffix) {
            Write-AzurPilotDetail -Message "хвост строки версии сохранён только для диагностики: '$($parts.Suffix)'"
        }

        return [pscustomobject]@{ Path = $candidate; VersionText = $versionText; Version = $parts }
    }

    $lines = @()
    $lines += "Не найден standalone CMake требуемой версии: нужен CMake >= $minimumText."
    $lines += "Владелец значения: $owner."
    $lines += 'Проверенные кандидаты:'
    $lines += $checked
    if ($rejected.Count -gt 0) {
        $lines += 'Отклонённые кандидаты (CMake из состава Visual Studio не берётся как подстановка):'
        $lines += ($rejected | ForEach-Object { "  - $_" })
    }
    $lines += "Как исправить: установите отдельно standalone CMake не ниже $minimumText и повторите запуск. Молчаливая подстановка другого CMake запрещена."
    throw ($lines -join "`n")
}

# -----------------------------------------------------------------------------
# Toolchain: Visual Studio и MSVC через vswhere
# -----------------------------------------------------------------------------

function Resolve-AzurPilotVisualStudioToolchain {
    <#
    .SYNOPSIS
        Находит установку Visual Studio с toolset MSVC x64 через vswhere и проверяет версию toolset.
    .DESCRIPTION
        Путь к vswhere выводится из переменных окружения установщика Visual Studio: литеральных
        machine-specific путей в репозитории нет. Требуемая линия Visual Studio выводится из
        канонического generator (toolchain.cmakeGenerator), требуемая версия toolset — из
        toolchain.msvcMinimumVersion. Несоответствие — ошибка с диагностикой, а не тихий выбор
        другого toolset.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Versions)

    $vswhereCandidates = New-Object System.Collections.Generic.List[string]
    foreach ($programFilesDirectory in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, ${env:ProgramW6432})) {
        if ($programFilesDirectory) { $vswhereCandidates.Add((Join-Path $programFilesDirectory 'Microsoft Visual Studio\Installer\vswhere.exe')) }
    }

    $vswherePath = $null
    foreach ($candidate in @($vswhereCandidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $vswherePath = $candidate; break }
    }

    if (-not $vswherePath) {
        throw "Не найден vswhere.exe (установщик Visual Studio). Невозможно определить toolset MSVC x64. Как исправить: установите Visual Studio с workload «Desktop development with C++» (компонент Microsoft.VisualStudio.Component.VC.Tools.x86.x64)."
    }

    if ($Versions.CMakeGenerator -notmatch '(?i)Visual Studio\s+(?<major>[0-9]+)') {
        throw "Не удалось определить линию Visual Studio из канонического generator '$($Versions.CMakeGenerator)'. Владелец значения: eng/versions.json: toolchain.cmakeGenerator."
    }
    $requiredMajor = [int]$Matches['major']

    $probe = Invoke-AzurPilotExternalCommand -FilePath $vswherePath -ArgumentList @('-all', '-prerelease', '-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-format', 'json') -Description 'поиск установок Visual Studio с toolset C++ x64 (vswhere)' -Capture -AllowFailure

    if ($probe.ExitCode -ne 0) {
        throw "vswhere завершился с кодом $($probe.ExitCode): $($probe.Text)"
    }

    $instances = @()
    if ($probe.Text.Trim()) {
        try { $instances = @($probe.Text | ConvertFrom-Json) }
        catch { throw "Не удалось разобрать JSON-вывод vswhere: $($_.Exception.Message)" }
    }

    if ($instances.Count -eq 0) {
        throw "Не найдена ни одна установка Visual Studio с toolset MSVC x64 (компонент Microsoft.VisualStudio.Component.VC.Tools.x86.x64). Как исправить: установите Visual Studio с workload «Desktop development with C++»."
    }

    $matching = @()
    $foundLines = @()
    foreach ($instance in $instances) {
        $instancePath = $null
        $instanceVersion = $null
        if ($instance.PSObject.Properties.Name -contains 'installationPath') { $instancePath = $instance.installationPath }
        if ($instance.PSObject.Properties.Name -contains 'installationVersion') { $instanceVersion = $instance.installationVersion }
        $foundLines += "  - $instancePath (версия $instanceVersion)"

        if (-not $instancePath -or -not $instanceVersion) { continue }
        if ($instanceVersion -notmatch '^(?<major>[0-9]+)\.') { continue }
        if ([int]$Matches['major'] -ne $requiredMajor) { continue }
        $matching += [pscustomobject]@{ Path = $instancePath; Version = $instanceVersion }
    }

    if ($matching.Count -eq 0) {
        $lines = @()
        $lines += "Не найдена установка Visual Studio линии $requiredMajor, требуемая каноническим generator «$($Versions.CMakeGenerator)» (владелец значения: eng/versions.json: toolchain.cmakeGenerator)."
        $lines += 'Найденные установки с toolset C++ x64:'
        $lines += $foundLines
        $lines += 'Как исправить: установите требуемую линию Visual Studio или обновите toolchain.cmakeGenerator согласованно с eng/versions.json.'
        throw ($lines -join "`n")
    }

    $instancePathResolved = ($matching | Select-Object -First 1).Path
    $instanceVersionResolved = ($matching | Select-Object -First 1).Version

    $toolsetRoot = Join-Path $instancePathResolved 'VC\Tools\MSVC'
    if (-not (Test-Path -LiteralPath $toolsetRoot -PathType Container)) {
        throw "В установке Visual Studio '$instancePathResolved' не найден каталог toolset MSVC: '$toolsetRoot'."
    }

    $bestText = $null
    $bestParts = $null
    foreach ($directory in @(Get-ChildItem -LiteralPath $toolsetRoot -Directory)) {
        $parts = $null
        try { $parts = ConvertTo-AzurPilotVersionParts -VersionText $directory.Name -Description 'MSVC toolset' }
        catch { continue }

        if ($null -eq $bestParts -or (Compare-AzurPilotVersion -Left $parts -Right $bestParts) -gt 0) {
            $bestText = $directory.Name
            $bestParts = $parts
        }
    }

    if (-not $bestText) {
        throw "В '$toolsetRoot' не найдено ни одного toolset MSVC."
    }

    $null = Assert-AzurPilotMinimumVersion -FoundVersionText $bestText -MinimumVersionText $Versions.MsvcMinimumVersion -Component 'MSVC toolset' -Owner 'eng/versions.json: toolchain.msvcMinimumVersion' -FoundPath $toolsetRoot -FixHint "обновите Visual Studio или установите toolset MSVC $($Versions.MsvcMinimumVersion) или новее."

    Write-AzurPilotSuccess -Message "MSVC toolset $bestText найден: $toolsetRoot"
    Write-AzurPilotDetail -Message "Visual Studio ${instanceVersionResolved}: $instancePathResolved"
    Write-AzurPilotDetail -Message "референсная версия сборки (диагностика): $($Versions.MsvcReferenceVersion)"

    return [pscustomobject]@{
        VswherePath      = $vswherePath
        InstancePath     = $instancePathResolved
        InstanceVersion  = $instanceVersionResolved
        ToolsetRoot      = $toolsetRoot
        ToolsetVersion   = $bestText
    }
}

# -----------------------------------------------------------------------------
# Toolchain: Ninja (только альтернативный путь) и tar (распаковка архива)
# -----------------------------------------------------------------------------

function Resolve-AzurPilotNinjaToolchain {
    <#
    .SYNOPSIS
        Определяет доступность Ninja для альтернативного пути Ninja Multi-Config.
    .DESCRIPTION
        Ninja нужен только альтернативному generator. Отсутствие или старость Ninja не блокирует
        канонический путь Visual Studio, но обязано быть явно сообщено
        (.codex/context/build-contracts.md). Ninja из состава Visual Studio допустим как источник
        для альтернативного пути.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Versions,
        [string]$VisualStudioInstancePath
    )

    $minimumText = $Versions.NinjaMinimumVersion
    $minimum = ConvertTo-AzurPilotVersionParts -VersionText $minimumText -Description 'Ninja (минимум)'

    $candidates = New-Object System.Collections.Generic.List[string]
    $fromPath = Get-Command -Name 'ninja' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($fromPath) { $candidates.Add($fromPath.Source) }
    if ($VisualStudioInstancePath) { $candidates.Add((Join-Path $VisualStudioInstancePath 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe')) }

    $checked = @()
    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $checked += "  - $candidate — файл не найден"
            continue
        }

        $probe = Invoke-AzurPilotExternalCommand -FilePath $candidate -ArgumentList @('--version') -Description "определение версии Ninja ($candidate)" -Capture -AllowFailure
        if ($probe.ExitCode -ne 0) {
            $checked += "  - $candidate — не удалось определить версию (exit $($probe.ExitCode))"
            continue
        }

        $firstLine = ($probe.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        if (-not $firstLine) {
            $checked += "  - $candidate — пустой вывод версии"
            continue
        }

        $versionText = $firstLine.Trim()
        $parts = $null
        try { $parts = ConvertTo-AzurPilotVersionParts -VersionText $versionText -Description 'Ninja' }
        catch {
            $checked += "  - $candidate — не удалось разобрать строку версии: '$versionText'"
            continue
        }

        if ((Compare-AzurPilotVersion -Left $parts -Right $minimum) -lt 0) {
            $checked += "  - $candidate — Ninja $versionText старше минимума $minimumText"
            continue
        }

        return [pscustomobject]@{
            Available   = $true
            Path        = $candidate
            VersionText = $versionText
            Message     = "Ninja $versionText найден: $candidate — альтернативный путь «$($Versions.CMakeGeneratorAlternative)» доступен."
        }
    }

    return [pscustomobject]@{
        Available   = $false
        Path        = $null
        VersionText = $null
        Message     = "Ninja не найден или старше минимума $minimumText ($($checked -join '; ')). Альтернативный путь «$($Versions.CMakeGeneratorAlternative)» недоступен; канонический путь «$($Versions.CMakeGenerator)» не затронут."
    }
}

function Resolve-AzurPilotArchiveTool {
    <#
    .SYNOPSIS
        Находит системный инструмент распаковки tar.exe (bsdtar).
    .DESCRIPTION
        Закреплённый пакет OpenCV — самораспаковывающийся архив; распаковка выполняется системным
        инструментом. Сторонние пакеты для распаковки не устанавливаются
        (.codex/context/build-contracts.md).
    #>
    [CmdletBinding()]
    param()

    $candidates = New-Object System.Collections.Generic.List[string]
    $fromPath = Get-Command -Name 'tar' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($fromPath) { $candidates.Add($fromPath.Source) }
    if ($env:SystemRoot) { $candidates.Add((Join-Path $env:SystemRoot 'System32\tar.exe')) }

    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }

    throw "Не найден tar.exe (bsdtar) — системный инструмент распаковки архива OpenCV. Сторонние пакеты для распаковки не устанавливаются; используйте Windows 10 1803 или новее, где tar.exe входит в состав системы."
}

# -----------------------------------------------------------------------------
# CMake presets: поиск канонического preset по значению из eng/versions.json
# -----------------------------------------------------------------------------

function Get-AzurPilotCMakePreset {
    <#
    .SYNOPSIS
        Находит в native/CMakePresets.json канонический configure preset и соответствующие ему
        build/test preset'ы для указанной конфигурации.
    .DESCRIPTION
        Канонический configure preset определяется по совпадению generator со значением
        toolchain.cmakeGenerator из eng/versions.json — так generator остаётся с одним владельцем,
        а не дублируется в скриптах. Если подходящего preset'а нет или их несколько, это ошибка
        контракта с русской диагностикой.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PresetsPath,
        [Parameter(Mandatory = $true)] $Versions,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    if (-not (Test-Path -LiteralPath $PresetsPath -PathType Leaf)) {
        throw "Не найден '$PresetsPath': канонический CMake configure preset обязателен (.codex/context/verification.md)."
    }

    try { $presets = Get-Content -LiteralPath $PresetsPath -Raw | ConvertFrom-Json }
    catch { throw "Не удалось прочитать '$PresetsPath' как JSON: $($_.Exception.Message)" }

    $configurePresets = @()
    if ($presets.PSObject.Properties.Name -contains 'configurePresets') { $configurePresets = @($presets.configurePresets) }

    $canonical = @()
    foreach ($preset in $configurePresets) {
        if ($preset.PSObject.Properties.Name -contains 'generator' -and $preset.generator -eq $Versions.CMakeGenerator) {
            $canonical += $preset
        }
    }

    if ($canonical.Count -ne 1) {
        throw "В '$PresetsPath' ожидается ровно один configure preset с каноническим generator «$($Versions.CMakeGenerator)» (eng/versions.json: toolchain.cmakeGenerator); найдено: $($canonical.Count)."
    }

    $configurePresetName = $canonical[0].name

    $buildPresets = @()
    if ($presets.PSObject.Properties.Name -contains 'buildPresets') { $buildPresets = @($presets.buildPresets) }

    $testPresets = @()
    if ($presets.PSObject.Properties.Name -contains 'testPresets') { $testPresets = @($presets.testPresets) }

    $buildPreset = @()
    foreach ($preset in $buildPresets) {
        $names = $preset.PSObject.Properties.Name
        if (($names -contains 'configurePreset') -and ($names -contains 'configuration') -and
            $preset.configurePreset -eq $configurePresetName -and $preset.configuration -eq $Configuration) {
            $buildPreset += $preset
        }
    }

    $testPreset = @()
    foreach ($preset in $testPresets) {
        $names = $preset.PSObject.Properties.Name
        if (($names -contains 'configurePreset') -and ($names -contains 'configuration') -and
            $preset.configurePreset -eq $configurePresetName -and $preset.configuration -eq $Configuration) {
            $testPreset += $preset
        }
    }

    if ($buildPreset.Count -ne 1) {
        throw "В '$PresetsPath' ожидается ровно один build preset для configure preset «$configurePresetName» и конфигурации «$Configuration»; найдено: $($buildPreset.Count)."
    }

    if ($testPreset.Count -ne 1) {
        throw "В '$PresetsPath' ожидается ровно один test preset для configure preset «$configurePresetName» и конфигурации «$Configuration»; найдено: $($testPreset.Count)."
    }

    # Каталог сборки берётся из preset'а, а не задаётся повторно в скрипте: у значения один владелец.
    if (-not ($canonical[0].PSObject.Properties.Name -contains 'binaryDir')) {
        throw "В '$PresetsPath' у канонического configure preset «$configurePresetName» не задан binaryDir."
    }

    $sourceDirectory = Split-Path -Parent $PresetsPath
    $binaryDirectory = $canonical[0].binaryDir -replace '\$\{sourceDir\}', $sourceDirectory
    # Приводим путь к нормальному виду: в preset'е он записан как ${sourceDir}/../artifacts/...
    $binaryDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($binaryDirectory)

    return [pscustomobject]@{
        PresetsPath        = $PresetsPath
        ConfigurePreset    = $configurePresetName
        BuildPreset        = $buildPreset[0].name
        TestPreset         = $testPreset[0].name
        BinaryDirectory    = $binaryDirectory
    }
}

# -----------------------------------------------------------------------------
# Staging native runtime в выход managed части
# -----------------------------------------------------------------------------

function Sync-AzurPilotNativeRuntimeStaging {
    <#
    .SYNOPSIS
        Собирает staging-каталог native runtime, который получает managed сборка.
    .DESCRIPTION
        Источник — выход native сборки для указанной конфигурации: там лежат native DLL и runtime
        DLL OpenCV, которую native build стажит рядом с собой для каждой конфигурации. Staging
        каталог является зеркалом этого выхода по *.dll/*.pdb (без бинарей native теста), поэтому
        устаревшие файлы не переживают пересборку. Значение каталога передаётся managed сборке
        свойством AzurPilotNativeRuntimeDir (.codex/context/architecture.md).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Layout,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$NativeLibraryFileName
    )

    $source = Join-Path $Layout.NativeOutputDirectory $Configuration
    $target = $Layout.NativeStagingDirectory

    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "Не найден выход native сборки: '$source'. Staging native runtime невозможен: сначала соберите native часть."
    }

    $sourceFiles = @(Get-ChildItem -LiteralPath $source -File | Where-Object {
            ($_.Extension -eq '.dll' -or $_.Extension -eq '.pdb') -and ($_.Name -notlike 'AzurPilot.Native.SmokeTest*')
        })

    if ($sourceFiles.Count -eq 0) {
        throw "В '$source' не найдено ни одного файла *.dll/*.pdb: native сборка не дала runtime артефактов."
    }

    New-Item -ItemType Directory -Force -Path $target | Out-Null

    $copied = 0
    $expectedNames = @()
    foreach ($file in $sourceFiles) {
        $expectedNames += $file.Name
        $destination = Join-Path $target $file.Name

        $needsCopy = $true
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $existing = Get-Item -LiteralPath $destination
            if ($existing.Length -eq $file.Length -and $existing.LastWriteTimeUtc -eq $file.LastWriteTimeUtc) { $needsCopy = $false }
        }

        if ($needsCopy) {
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
            $copied++
        }
    }

    $removed = 0
    foreach ($existing in @(Get-ChildItem -LiteralPath $target -File | Where-Object { $_.Extension -eq '.dll' -or $_.Extension -eq '.pdb' })) {
        if ($expectedNames -notcontains $existing.Name) {
            Remove-Item -LiteralPath $existing.FullName -Force
            $removed++
        }
    }

    $stagedLibrary = Join-Path $target $NativeLibraryFileName
    if (-not (Test-Path -LiteralPath $stagedLibrary -PathType Leaf)) {
        throw "В staging-каталоге '$target' отсутствует '$NativeLibraryFileName': managed часть не получила native библиотеку, и interop тест упал бы без внятной причины."
    }

    return [pscustomobject]@{
        Directory = $target
        Copied    = $copied
        Removed   = $removed
        Files     = $expectedNames
    }
}

function Clear-AzurPilotBuildOutputs {
    <#
    .SYNOPSIS
        Удаляет build outputs native и managed частей.
    .DESCRIPTION
        Удаляются только генерируемые артефакты: build directories native части, staging native
        runtime и bin/obj managed проектов. Полученные закреплённые зависимости (artifacts/opencv)
        и скачанный пакет сохраняются: повторное скачивание не является частью очистки сборки.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Layout,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    $removed = @()

    # Каталог native сборки целиком: build directory, выход native targets и staging native runtime.
    # Каталог сборки задаётся preset'ом, поэтому удаляется весь artifacts/native, а не список
    # угаданных подкаталогов.
    $nativeArtifacts = Join-Path $Layout.ArtifactsRoot 'native'
    if (Test-Path -LiteralPath $nativeArtifacts) {
        Remove-Item -LiteralPath $nativeArtifacts -Recurse -Force
        $removed += $nativeArtifacts
    }

    foreach ($projectRoot in @($Layout.ManagedSourceRoot, $Layout.ManagedTestRoot)) {
        if (-not (Test-Path -LiteralPath $projectRoot -PathType Container)) { continue }
        foreach ($projectDirectory in @(Get-ChildItem -LiteralPath $projectRoot -Directory)) {
            foreach ($name in @('bin', 'obj')) {
                $candidate = Join-Path $projectDirectory.FullName $name
                if (Test-Path -LiteralPath $candidate) {
                    Remove-Item -LiteralPath $candidate -Recurse -Force
                    $removed += $candidate
                }
            }
        }
    }

    return $removed
}
