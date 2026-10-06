# =============================================================================
# eng/verify.ps1 — единый verification entrypoint AzurPilotRu.
#
# Канонический entrypoint (.codex/context/verification.md):
#     pwsh ./eng/verify.ps1
#
# Возвращает 0 только при полном успехе и ненулевой код при любом реальном нарушении
# build/test/analyzer/format контракта. Build-логика не дублируется: канонический путь сборки
# целиком выполняет eng/build.ps1, а общие helpers берутся из eng/common.ps1.
#
# Что именно проверяется:
#   1. канонический build целиком (build.ps1: toolchain → native dependencies → native сборка +
#      CTest → staging → managed сборка → managed interop тесты) и наличие доказательств staging;
#   2. analyzers и project-owned warnings: effective-свойства каждого проекта из AzurPilot.slnx
#      и негативная проба, доказывающая, что project-owned warning реально валит сборку;
#   3. code-style и форматирование: фактически применяемый механизм — build-time enforcement
#      (.editorconfig + EnforceCodeStyleInBuild + TreatWarningsAsErrors), доказанный негативной
#      пробой на нарушениях форматирования и мёртвого кода;
#   4. согласованность закреплённых версий с eng/versions.json и отсутствие разъехавшихся
#      владельцев одного номера;
#   5. repository-wide проверки: machine-specific абсолютные пути, случайный hardcode
#      фундаментального разрешения, fixed sleep;
#   6. Git-гигиена: build outputs не попадают в Git, packages.lock.json остаётся отслеживаемым,
#      в индексе нет сторонних бинарников;
#   7. единственный владелец настройки restore lock.
#
# Проверки детерминированные: без фиксированных sleep, без зависимости от текущего каталога и от
# порядка запуска. Язык диагностики — русский (.codex/context/language.md).
# =============================================================================

[CmdletBinding()]
param(
    # Конфигурация, в которой проверяется фундамент.
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',

    # Пропустить канонический build (только для отладки самих проверок). Каноническая
    # verification всегда включает сборку: CI и приёмка используют форму без этого ключа.
    [switch]$SkipBuild,

    # Сделать `dotnet format --verify-no-changes` жёстким гейтом. По умолчанию он не является
    # частью контракта: фактический механизм — build-time enforcement (см. шаг про code-style).
    [switch]$CheckDotNetFormat
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
    $dotnet = Resolve-AzurPilotDotNetToolchain -Versions $versions

    # --- Состояние verification -------------------------------------------------

    $script:checks = New-Object System.Collections.Generic.List[object]
    $script:findings = New-Object System.Collections.Generic.List[string]
    $script:projectProperties = @{}

    function Add-AzurPilotCheck {
        <#
        .SYNOPSIS
            Регистрирует результат одной проверки: успех или реальное нарушение.
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][bool]$Passed,
            [string]$Detail
        )

        $script:checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })

        if ($Passed) {
            if ($Detail) { Write-AzurPilotSuccess -Message "$Name — $Detail" } else { Write-AzurPilotSuccess -Message $Name }
        }
        else {
            Write-AzurPilotError -Message "$Name — $Detail"
        }
    }

    function Add-AzurPilotFinding {
        <#
        .SYNOPSIS
            Регистрирует находку, которая не является нарушением контракта, но обязана быть видимой.
        #>
        [CmdletBinding()]
        param([Parameter(Mandatory = $true)][string]$Message)

        $script:findings.Add($Message)
        Write-AzurPilotWarning -Message $Message
    }

    function Get-AzurPilotScannedFiles {
        <#
        .SYNOPSIS
            Возвращает project-owned код, конфигурацию и build-файлы для repository-wide поисков.
        .DESCRIPTION
            Область поиска ровно та, что описана в .codex/context/verification.md: project-owned
            код, конфигурация и build-файлы. Документация и .codex/context/** исключены: сами
            запреты сформулированы там словами, и текстовый поиск дал бы ложное срабатывание на
            тексте запрета. Генерируемые каталоги (artifacts/, bin/, obj/) исключаются при обходе.
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $true)][string]$Root,
            [string[]]$Extensions = @('.cs', '.h', '.hpp', '.cpp', '.c', '.cc', '.ps1', '.psm1', '.json', '.yml', '.yaml', '.cmake', '.props', '.targets', '.csproj', '.slnx'),
            [string[]]$FileNames = @('CMakeLists.txt', '.editorconfig', '.gitignore'),
            [string[]]$ExcludedDirectories = @('artifacts', 'bin', 'obj', '.git', '.agent-teams', '.vs', '.codex', 'node_modules', 'TestResults'),
            [string[]]$ExcludedFileNames = @('packages.lock.json')
        )

        $files = New-Object System.Collections.Generic.List[string]
        $pending = New-Object System.Collections.Generic.Stack[string]
        $pending.Push($Root)

        while ($pending.Count -gt 0) {
            $directory = $pending.Pop()

            foreach ($child in @(Get-ChildItem -LiteralPath $directory -Directory -Force -ErrorAction SilentlyContinue)) {
                if ($ExcludedDirectories -contains $child.Name) { continue }
                $pending.Push($child.FullName)
            }

            foreach ($file in @(Get-ChildItem -LiteralPath $directory -File -Force -ErrorAction SilentlyContinue)) {
                if ($ExcludedFileNames -contains $file.Name) { continue }
                if ($file.Extension -eq '.md') { continue }
                if ($Extensions -contains $file.Extension -or $FileNames -contains $file.Name) { $files.Add($file.FullName) }
            }
        }

        return @($files | Sort-Object)
    }

    function Get-AzurPilotProjectProperties {
        <#
        .SYNOPSIS
            Читает effective-свойства managed проекта и кэширует их на время прогона.
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $true)][string]$ProjectPath,
            [Parameter(Mandatory = $true)][string]$DotNetPath,
            [Parameter(Mandatory = $true)][string]$WorkingDirectory,
            [Parameter(Mandatory = $true)][string]$ConfigurationName
        )

        if ($script:projectProperties.ContainsKey($ProjectPath)) { return $script:projectProperties[$ProjectPath] }

        $probe = Invoke-AzurPilotExternalCommand -FilePath $DotNetPath `
            -ArgumentList @('msbuild', $ProjectPath, '-nologo', "-p:Configuration=$ConfigurationName", '-getProperty:TreatWarningsAsErrors', '-getProperty:AzurPilotStrictProject', '-getProperty:EnableNETAnalyzers', '-getProperty:EnforceCodeStyleInBuild', '-getProperty:AnalysisLevel', '-getProperty:RestorePackagesWithLockFile') `
            -WorkingDirectory $WorkingDirectory `
            -Description "чтение effective-свойств проекта $(Split-Path -Leaf $ProjectPath)" `
            -Capture

        $parsed = $probe.Text | ConvertFrom-Json
        $script:projectProperties[$ProjectPath] = $parsed.Properties
        return $parsed.Properties
    }

    function Get-AzurPilotSolutionProjects {
        <#
        .SYNOPSIS
            Возвращает проекты managed solution — владельца границ (AzurPilot.slnx).
        #>
        [CmdletBinding()]
        param([Parameter(Mandatory = $true)][string]$SolutionPath)

        if (-not (Test-Path -LiteralPath $SolutionPath -PathType Leaf)) {
            throw "Не найден managed solution '$SolutionPath': проверка свойств проектов невозможна."
        }

        try { [xml]$solution = Get-Content -LiteralPath $SolutionPath -Raw }
        catch { throw "Не удалось прочитать '$SolutionPath' как XML: $($_.Exception.Message)" }

        $paths = New-Object System.Collections.Generic.List[string]
        foreach ($node in @($solution.SelectNodes('//Project'))) {
            $relative = $node.GetAttribute('Path')
            if (-not $relative) { continue }
            $paths.Add((Join-Path (Split-Path -Parent $SolutionPath) $relative))
        }

        if ($paths.Count -eq 0) { throw "В '$SolutionPath' не найдено ни одного проекта." }
        return @($paths)
    }

    function Invoke-AzurPilotStyleEnforcementProbe {
        <#
        .SYNOPSIS
            Доказывает, что build-time enforcement реально валит сборку на project-owned нарушении.
        .DESCRIPTION
            Проба создаётся внутри ignored boundary artifacts/ и наследует .editorconfig,
            Directory.Build.props и Directory.Build.targets репозитория, поэтому проверяется
            фактический механизм, а не его декларация. Каждая инъекция обязана привести к
            ненулевому коду сборки: если сборка проходит, механизм не работает — это нарушение.
            Каталог пробы удаляется в любом случае.
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $true)][string]$ProbeDirectory,
            [Parameter(Mandatory = $true)][string]$DotNetPath,
            [Parameter(Mandatory = $true)][string]$WorkingDirectory,
            [Parameter(Mandatory = $true)][string]$ConfigurationName,
            [Parameter(Mandatory = $true)] $Variants
        )

        $projectPath = Join-Path $ProbeDirectory 'probe.csproj'
        $projectContent = @(
            '<Project Sdk="Microsoft.NET.Sdk">',
            '  <PropertyGroup>',
            '    <OutputType>Library</OutputType>',
            '    <IsPackable>false</IsPackable>',
            '  </PropertyGroup>',
            '</Project>'
        ) -join "`n"

        New-Item -ItemType Directory -Force -Path $ProbeDirectory | Out-Null
        Set-Content -LiteralPath $projectPath -Value $projectContent -Encoding utf8

        try {
            foreach ($variant in @($Variants)) {
                foreach ($existing in @(Get-ChildItem -LiteralPath $ProbeDirectory -File -Filter '*.cs' -ErrorAction SilentlyContinue)) {
                    Remove-Item -LiteralPath $existing.FullName -Force
                }

                Set-Content -LiteralPath (Join-Path $ProbeDirectory $variant.FileName) -Value $variant.Code -Encoding utf8

                $build = Invoke-AzurPilotExternalCommand -FilePath $DotNetPath `
                    -ArgumentList @('build', $projectPath, '-c', $ConfigurationName, '--nologo', '-v', 'minimal') `
                    -WorkingDirectory $WorkingDirectory `
                    -Description "негативная проба code-style: $($variant.Name)" `
                    -Capture `
                    -AllowFailure

                $diagnostics = @($build.Output | Where-Object { $_ -match "$($variant.ExpectedDiagnostic)" } | Select-Object -First 2)
                $detail = if ($diagnostics.Count -gt 0) { ($diagnostics -join ' | ') } else { "код сборки $($build.ExitCode)" }

                Add-AzurPilotCheck -Name "project-owned нарушение «$($variant.Name)» валит сборку" -Passed ($build.ExitCode -ne 0) -Detail $detail
            }
        }
        finally {
            if (Test-Path -LiteralPath $ProbeDirectory) { Remove-Item -LiteralPath $ProbeDirectory -Recurse -Force }
        }
    }

    # --- План шагов -------------------------------------------------------------

    $totalSteps = 7
    if ($SkipBuild) { $totalSteps-- }
    if ($CheckDotNetFormat) { $totalSteps++ }
    $step = 0

    Write-AzurPilotInfo -Message "единая verification AzurPilotRu: pwsh ./eng/verify.ps1 (конфигурация $Configuration)"
    Write-AzurPilotDetail -Message "корень репозитория: $repositoryRoot"
    Write-AzurPilotDetail -Message "владелец закреплённых версий: eng/versions.json"

    if ($SkipBuild) {
        Write-AzurPilotWarning -Message 'канонический build пропущен (-SkipBuild): это режим отладки самих проверок, а не verification'
    }

    # --- Шаг: канонический build и тесты ---------------------------------------

    if (-not $SkipBuild) {
        $step++
        Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Канонический build, native CTest и managed interop тесты'

        $build = Invoke-AzurPilotExternalCommand -FilePath $powerShellPath `
            -ArgumentList @('-File', (Join-Path $PSScriptRoot 'build.ps1'), '-Configuration', $Configuration) `
            -WorkingDirectory $repositoryRoot `
            -Description "канонический build ($Configuration)" `
            -AllowFailure

        Add-AzurPilotCheck -Name 'канонический путь pwsh ./eng/build.ps1 завершился успешно' -Passed ($build.ExitCode -eq 0) -Detail "код возврата: $($build.ExitCode); build включает native CTest и managed interop тесты"
    }
    else {
        $step++
        Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Канонический build пропущен (-SkipBuild)'
    }

    # Доказательство staging: проверяется наличие файлов, а не счётчик копирования в журнале.
    $stagingDirectory = $layout.NativeStagingDirectory
    $stagedLibrary = Join-Path $stagingDirectory $layout.NativeLibraryFileName
    $stagedOpenCv = @(Get-ChildItem -LiteralPath $stagingDirectory -File -Filter 'opencv_*.dll' -ErrorAction SilentlyContinue)

    Add-AzurPilotCheck -Name 'staging native runtime содержит native библиотеку и runtime OpenCV' `
        -Passed ((Test-Path -LiteralPath $stagedLibrary -PathType Leaf) -and ($stagedOpenCv.Count -gt 0)) `
        -Detail "каталог: $stagingDirectory; файлы: $(@(Get-ChildItem -LiteralPath $stagingDirectory -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join ', ')"

    $testOutputRoot = Join-Path (Join-Path $layout.ManagedTestRoot 'AzurPilot.Tests') (Join-Path 'bin' $Configuration)
    $testOutputNative = @(Get-ChildItem -LiteralPath $testOutputRoot -Recurse -File -Filter $layout.NativeLibraryFileName -ErrorAction SilentlyContinue)
    Add-AzurPilotCheck -Name 'выход managed теста содержит native библиотеку (interop выполним)' `
        -Passed ($testOutputNative.Count -gt 0) `
        -Detail "проверено: $testOutputRoot"

    # --- Шаг: analyzers и project-owned warnings -------------------------------

    $step++
    Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Analyzers и project-owned warnings'

    $projects = Get-AzurPilotSolutionProjects -SolutionPath $layout.SolutionPath
    foreach ($projectPath in $projects) {
        $name = Split-Path -Leaf $projectPath
        $properties = Get-AzurPilotProjectProperties -ProjectPath $projectPath -DotNetPath $dotnet.Path -WorkingDirectory $repositoryRoot -ConfigurationName $Configuration

        Add-AzurPilotCheck -Name "${name}: analyzers включены" -Passed ($properties.EnableNETAnalyzers -eq 'true') -Detail "EnableNETAnalyzers=$($properties.EnableNETAnalyzers); AnalysisLevel=$($properties.AnalysisLevel)"

        if ($properties.AzurPilotStrictProject -eq 'false') {
            Add-AzurPilotCheck -Name "${name}: ослабление строгости объявлено явно" -Passed ($properties.TreatWarningsAsErrors -eq 'false') -Detail "AzurPilotStrictProject=false → TreatWarningsAsErrors=$($properties.TreatWarningsAsErrors) (осознанное исключение в Directory.Build.targets)"
        }
        else {
            Add-AzurPilotCheck -Name "${name}: warnings as errors реально включены" -Passed ($properties.TreatWarningsAsErrors -eq 'true') -Detail "TreatWarningsAsErrors=$($properties.TreatWarningsAsErrors) (владелец: Directory.Build.props)"
        }
    }

    $probeDirectory = Join-Path $layout.ArtifactsRoot 'verify-probe'
    $warningVariants = @(
        [pscustomobject]@{
            Name               = 'CS0219 (неиспользуемая локальная переменная)'
            FileName           = 'CompilerWarningProbe.cs'
            ExpectedDiagnostic = 'CS0219'
            Code               = @(
                'namespace VerifyProbe;',
                '',
                'internal static class CompilerWarningProbe',
                '{',
                '    internal static int Compute()',
                '    {',
                '        int unusedLocal = 42;',
                '        return 1;',
                '    }',
                '}'
            ) -join "`n"
        }
    )

    Invoke-AzurPilotStyleEnforcementProbe -ProbeDirectory $probeDirectory -DotNetPath $dotnet.Path -WorkingDirectory $repositoryRoot -ConfigurationName $Configuration -Variants $warningVariants

    # --- Шаг: code-style и форматирование --------------------------------------

    $step++
    Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Code-style и форматирование (build-time enforcement)'

    $styleVariants = @(
        [pscustomobject]@{
            Name               = 'IDE0055 (нарушение форматирования)'
            FileName           = 'FormattingProbe.cs'
            ExpectedDiagnostic = 'IDE0055'
            Code               = @(
                'namespace VerifyProbe;',
                '',
                'internal static class FormattingProbe',
                '{',
                '  internal static int Compute()',
                '  {',
                '      int value = 1;',
                '        return value;',
                '  }',
                '}'
            ) -join "`n"
        },
        [pscustomobject]@{
            Name               = 'IDE0005 (лишняя директива using)'
            FileName           = 'DeadCodeProbe.cs'
            ExpectedDiagnostic = 'IDE0005'
            Code               = @(
                'using System.Text;',
                '',
                'namespace VerifyProbe;',
                '',
                'internal static class DeadCodeProbe',
                '{',
                '    internal static string Describe(int value)',
                '    {',
                '        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);',
                '    }',
                '}'
            ) -join "`n"
        }
    )

    foreach ($projectPath in $projects) {
        $properties = Get-AzurPilotProjectProperties -ProjectPath $projectPath -DotNetPath $dotnet.Path -WorkingDirectory $repositoryRoot -ConfigurationName $Configuration
        Add-AzurPilotCheck -Name "$(Split-Path -Leaf $projectPath): code-style проверяется в сборке" -Passed ($properties.EnforceCodeStyleInBuild -eq 'true') -Detail "EnforceCodeStyleInBuild=$($properties.EnforceCodeStyleInBuild) (владелец: Directory.Build.props)"
    }

    Invoke-AzurPilotStyleEnforcementProbe -ProbeDirectory $probeDirectory -DotNetPath $dotnet.Path -WorkingDirectory $repositoryRoot -ConfigurationName $Configuration -Variants $styleVariants

    if ($CheckDotNetFormat) {
        Write-AzurPilotInfo -Message 'дополнительно запрошен жёсткий гейт dotnet format --verify-no-changes (-CheckDotNetFormat)'
        $format = Invoke-AzurPilotExternalCommand -FilePath $dotnet.Path `
            -ArgumentList @('format', $layout.SolutionPath, '--verify-no-changes', '--no-restore') `
            -WorkingDirectory $repositoryRoot `
            -Description 'проверка форматирования (dotnet format --verify-no-changes)' `
            -Capture `
            -AllowFailure
        Add-AzurPilotCheck -Name 'dotnet format --verify-no-changes не находит изменений' -Passed ($format.ExitCode -eq 0) -Detail "код возврата: $($format.ExitCode)"
    }
    else {
        Add-AzurPilotFinding -Message 'Форматирование проверяется build-time enforcement (IDE0055=error в .editorconfig + EnforceCodeStyleInBuild + TreatWarningsAsErrors): нарушения форматирования ломают сборку, что и доказывает негативная проба выше. `dotnet format --verify-no-changes` частью контракта не является: он применяет и правила, настроенные как silent/suggestion, поэтому как гейт требует отдельного решения владельца src/** (см. отчёт t5). Жёсткий гейт доступен ключом -CheckDotNetFormat.'
    }

    # --- Шаг: согласованность закреплённых версий -------------------------------

    $step++
    Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Согласованность закреплённых версий с eng/versions.json'

    $headerPath = Join-Path (Join-Path $repositoryRoot 'native') 'include\azurpilot_native_abi.h'
    if (Test-Path -LiteralPath $headerPath -PathType Leaf) {
        $headerText = Get-Content -LiteralPath $headerPath -Raw

        $headerAbi = $null
        if ($headerText -match '#define\s+AZURPILOT_NATIVE_ABI_VERSION\s+(?<value>[0-9]+)') { $headerAbi = [int]$Matches['value'] }
        Add-AzurPilotCheck -Name 'версия ABI: заголовок совпадает с eng/versions.json' -Passed ($null -ne $headerAbi -and $headerAbi -eq [int]$versions.NativeAbiVersion) -Detail "заголовок: $headerAbi; eng/versions.json (nativeAbi.version): $($versions.NativeAbiVersion); нормативный владелец — заголовок"

        $headerAbiString = $null
        if ($headerText -match '#define\s+AZURPILOT_NATIVE_ABI_VERSION_STRING\s+"(?<value>[^"]+)"') { $headerAbiString = $Matches['value'] }
        Add-AzurPilotCheck -Name 'версия ABI: строка версии в заголовке согласована с числом' -Passed ($headerAbiString -eq [string]$headerAbi) -Detail "AZURPILOT_NATIVE_ABI_VERSION_STRING='$headerAbiString'; AZURPILOT_NATIVE_ABI_VERSION=$headerAbi"
    }
    else {
        Add-AzurPilotCheck -Name 'версия ABI: заголовок найден' -Passed $false -Detail "не найден '$headerPath'"
    }

    $globalJsonPath = Join-Path $repositoryRoot 'global.json'
    if (Test-Path -LiteralPath $globalJsonPath -PathType Leaf) {
        $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
        Add-AzurPilotCheck -Name 'global.json: версия .NET SDK совпадает с пином' -Passed ($globalJson.sdk.version -eq $versions.DotNetSdkVersion) -Detail "global.json: $($globalJson.sdk.version); eng/versions.json: $($versions.DotNetSdkVersion)"
        Add-AzurPilotCheck -Name 'global.json: rollForward совпадает с пином' -Passed ($globalJson.sdk.rollForward -eq $versions.DotNetSdkRollForward) -Detail "global.json: $($globalJson.sdk.rollForward); eng/versions.json: $($versions.DotNetSdkRollForward)"
    }
    else {
        Add-AzurPilotCheck -Name 'global.json найден' -Passed $false -Detail "не найден '$globalJsonPath'"
    }

    $cmakeListsPath = Join-Path (Join-Path $repositoryRoot 'native') 'CMakeLists.txt'
    if (Test-Path -LiteralPath $cmakeListsPath -PathType Leaf) {
        $cmakeListsText = Get-Content -LiteralPath $cmakeListsPath -Raw

        $cmakeLiteral = $null
        if ($cmakeListsText -match 'cmake_minimum_required\(VERSION\s+(?<value>[0-9][0-9.]*)\)') { $cmakeLiteral = $Matches['value'] }
        Add-AzurPilotCheck -Name 'native/CMakeLists.txt: литерал cmake_minimum_required совпадает с пином' -Passed ($cmakeLiteral -eq $versions.CMakeMinimumVersion) -Detail "литерал: $cmakeLiteral; eng/versions.json: $($versions.CMakeMinimumVersion)"

        $requiredOpenCv = $null
        if ($cmakeListsText -match 'find_package\(OpenCV\s+(?<value>[0-9][0-9.]*)') { $requiredOpenCv = $Matches['value'] }
        $pinnedOpenCvLine = ($versions.OpenCvVersion -split '\.')[0..1] -join '.'
        Add-AzurPilotCheck -Name 'native/CMakeLists.txt: требуемая линия OpenCV совпадает с пином' -Passed ($requiredOpenCv -eq $pinnedOpenCvLine) -Detail "find_package(OpenCV $requiredOpenCv); eng/versions.json (opencv.version): $($versions.OpenCvVersion)"
    }
    else {
        Add-AzurPilotCheck -Name 'native/CMakeLists.txt найден' -Passed $false -Detail "не найден '$cmakeListsPath'"
    }

    try {
        $preset = Get-AzurPilotCMakePreset -PresetsPath $layout.NativePresetsPath -Versions $versions -Configuration $Configuration
        Add-AzurPilotCheck -Name 'native/CMakePresets.json: канонический generator совпадает с пином' -Passed $true -Detail "configure preset «$($preset.ConfigurePreset)» использует generator «$($versions.CMakeGenerator)»"

        $presetsJson = Get-Content -LiteralPath $layout.NativePresetsPath -Raw | ConvertFrom-Json
        $presetMinimum = "$($presetsJson.cmakeMinimumRequired.major).$($presetsJson.cmakeMinimumRequired.minor).$($presetsJson.cmakeMinimumRequired.patch)"
        Add-AzurPilotCheck -Name 'native/CMakePresets.json: cmakeMinimumRequired совпадает с пином' -Passed ($presetMinimum -eq $versions.CMakeMinimumVersion) -Detail "preset: $presetMinimum; eng/versions.json: $($versions.CMakeMinimumVersion)"
    }
    catch {
        Add-AzurPilotCheck -Name 'native/CMakePresets.json: канонический generator совпадает с пином' -Passed $false -Detail $_.Exception.Message
    }

    # Один номер — один владелец: пины инструментов не должны встречаться литералами в других местах.
    $ownerAllowList = @{
        'eng\versions.json'          = 'владелец закреплённых значений'
        'global.json'                = 'объявленный потребитель dotnetSdk.version и dotnetSdk.rollForward'
        'native\CMakeLists.txt'      = 'литерал cmake_minimum_required, сверен с пином выше'
        'native\CMakePresets.json'   = 'cmakeMinimumRequired, сверен с пином выше'
    }

    $pinnedLiterals = [ordered]@{
        'toolchain.cmakeMinimumVersion'  = $versions.CMakeMinimumVersion
        'dotnetSdk.version'              = $versions.DotNetSdkVersion
        'opencv.version'                 = $versions.OpenCvVersion
        'toolchain.msvcMinimumVersion'   = $versions.MsvcMinimumVersion
        'toolchain.msvcReferenceVersion' = $versions.MsvcReferenceVersion
        'toolchain.ninjaMinimumVersion'  = $versions.NinjaMinimumVersion
    }

    $strayLiterals = New-Object System.Collections.Generic.List[string]
    foreach ($file in @(Get-AzurPilotScannedFiles -Root $repositoryRoot)) {
        $relative = $file.Substring($repositoryRoot.Length).TrimStart('\', '/')
        if ($ownerAllowList.ContainsKey($relative)) { continue }

        $lines = @(Get-Content -LiteralPath $file -ErrorAction SilentlyContinue)
        for ($index = 0; $index -lt $lines.Count; $index++) {
            foreach ($owner in $pinnedLiterals.Keys) {
                if ($lines[$index].Contains($pinnedLiterals[$owner])) {
                    $strayLiterals.Add("${relative}:$($index + 1) содержит литерал $owner = $($pinnedLiterals[$owner])")
                }
            }
        }
    }

    Add-AzurPilotCheck -Name 'ни один закреплённый номер не продублирован вне владельца' -Passed ($strayLiterals.Count -eq 0) -Detail $(if ($strayLiterals.Count -eq 0) { 'литералы пинов встречаются только у владельцев' } else { ($strayLiterals -join '; ') })

    # --- Шаг: repository-wide проверки -----------------------------------------

    $step++
    Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Repository-wide проверки (machine-specific пути, hardcode разрешения, fixed sleep)'

    # Шаблоны собираются из частей: иначе сам файл проверки давал бы ложное срабатывание на тексте
    # собственного шаблона.
    $driveLetterPattern = '(?<![A-Za-z0-9])' + '[A-Za-z]' + ':' + '[\\/]'
    $resolutionPattern = '1280' + '\s*[xXхХ×,]\s*' + '720'
    $sleepPattern = 'Start-' + 'Sleep'

    $pathViolations = New-Object System.Collections.Generic.List[string]
    $resolutionViolations = New-Object System.Collections.Generic.List[string]
    $sleepViolations = New-Object System.Collections.Generic.List[string]

    $scannedFiles = @(Get-AzurPilotScannedFiles -Root $repositoryRoot)
    foreach ($file in $scannedFiles) {
        $relative = $file.Substring($repositoryRoot.Length).TrimStart('\', '/')
        $lines = @(Get-Content -LiteralPath $file -ErrorAction SilentlyContinue)

        for ($index = 0; $index -lt $lines.Count; $index++) {
            if ($lines[$index] -match $driveLetterPattern) { $pathViolations.Add("${relative}:$($index + 1)") }
            if ($lines[$index] -match $resolutionPattern) { $resolutionViolations.Add("${relative}:$($index + 1)") }
            if ($file.EndsWith('.ps1') -and $lines[$index] -match $sleepPattern) { $sleepViolations.Add("${relative}:$($index + 1)") }
        }
    }

    Add-AzurPilotCheck -Name 'нет machine-specific абсолютных путей' -Passed ($pathViolations.Count -eq 0) -Detail $(if ($pathViolations.Count -eq 0) { "проверено файлов: $($scannedFiles.Count) (project-owned код, конфигурация и build-файлы; .md и .codex/** исключены)" } else { ($pathViolations -join ', ') })

    Add-AzurPilotCheck -Name 'нет случайного hardcode фундаментального разрешения' -Passed ($resolutionViolations.Count -eq 0) -Detail $(if ($resolutionViolations.Count -eq 0) { "проверено файлов: $($scannedFiles.Count)" } else { ($resolutionViolations -join ', ') })

    Add-AzurPilotCheck -Name 'в скриптах eng/ нет fixed sleep как части корректности' -Passed ($sleepViolations.Count -eq 0) -Detail $(if ($sleepViolations.Count -eq 0) { "$sleepPattern не найден" } else { ($sleepViolations -join ', ') })

    # --- Шаг: Git-гигиена -------------------------------------------------------

    $step++
    Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Git-гигиена: build outputs, lock-файлы и сторонние бинарники'

    $gitAvailable = $null -ne (Get-Command -Name 'git' -CommandType Application -ErrorAction SilentlyContinue)
    if (-not $gitAvailable) {
        Add-AzurPilotCheck -Name 'git доступен для проверки гигиены' -Passed $false -Detail 'git не найден в PATH: проверка попадания build outputs в Git невозможна'
    }
    else {
        $untracked = Invoke-AzurPilotExternalCommand -FilePath 'git' -ArgumentList @('ls-files', '--others', '--exclude-standard') -WorkingDirectory $repositoryRoot -Description 'список untracked файлов, видимых Git' -Capture -AllowFailure
        $tracked = Invoke-AzurPilotExternalCommand -FilePath 'git' -ArgumentList @('ls-files') -WorkingDirectory $repositoryRoot -Description 'список отслеживаемых файлов' -Capture -AllowFailure

        $leakedOutputs = @($untracked.Output | Where-Object {
                $candidate = $_ -replace '\\', '/'
                $candidate -match '(^|/)(bin|obj)/' -or $candidate.StartsWith('artifacts/')
            })

        Add-AzurPilotCheck -Name 'ни один build output не попал бы в Git' -Passed ($leakedOutputs.Count -eq 0) -Detail $(if ($leakedOutputs.Count -eq 0) { 'в списке untracked нет путей bin/, obj/ и artifacts/ — проверка не опирается на текст .gitignore, а спрашивает Git' } else { ($leakedOutputs -join ', ') })

        $binaryExtensions = @('.dll', '.lib', '.exe', '.pdb', '.obj', '.so', '.dylib', '.zip', '.7z', '.nupkg')
        $committedBinaries = @($tracked.Output | Where-Object {
                $candidate = $_ -replace '\\', '/'
                if ($candidate.StartsWith('artifacts/')) { return $true }
                return $binaryExtensions -contains [System.IO.Path]::GetExtension($candidate).ToLowerInvariant()
            })

        Add-AzurPilotCheck -Name 'в индексе нет сторонних бинарников и build outputs' -Passed ($committedBinaries.Count -eq 0) -Detail $(if ($committedBinaries.Count -eq 0) { 'отслеживаемых бинарников и путей artifacts/ нет' } else { ($committedBinaries -join ', ') })

        $lockFiles = @(Get-AzurPilotScannedFiles -Root $repositoryRoot -Extensions @() -FileNames @('packages.lock.json') -ExcludedFileNames @())
        if ($lockFiles.Count -eq 0) {
            Add-AzurPilotCheck -Name 'lock-файлы найдены' -Passed $false -Detail 'packages.lock.json не найден: контракт воспроизводимого restore не подтверждён'
        }
        else {
            $ignoredLocks = New-Object System.Collections.Generic.List[string]
            $invisibleLocks = New-Object System.Collections.Generic.List[string]
            $visibleUntracked = @($untracked.Output | ForEach-Object { $_ -replace '\\', '/' })

            foreach ($lockFile in $lockFiles) {
                $relative = $lockFile.Substring($repositoryRoot.Length).TrimStart('\', '/')
                $normalized = $relative -replace '\\', '/'

                $ignoreProbe = Invoke-AzurPilotExternalCommand -FilePath 'git' -ArgumentList @('check-ignore', '-q', $normalized) -WorkingDirectory $repositoryRoot -Description "проверка игнорирования $normalized" -Capture -AllowFailure
                if ($ignoreProbe.ExitCode -eq 0) { $ignoredLocks.Add($normalized) }

                $isVisible = ($visibleUntracked -contains $normalized) -or (@($tracked.Output | ForEach-Object { $_ -replace '\\', '/' }) -contains $normalized)
                if (-not $isVisible) { $invisibleLocks.Add($normalized) }
            }

            Add-AzurPilotCheck -Name 'packages.lock.json остаётся отслеживаемым' -Passed ($ignoredLocks.Count -eq 0) -Detail $(if ($ignoredLocks.Count -eq 0) { "проверено lock-файлов: $($lockFiles.Count); ни один не игнорируется" } else { 'игнорируются: ' + ($ignoredLocks -join ', ') })

            Add-AzurPilotCheck -Name 'lock-файлы видны Git как untracked (будут добавлены)' -Passed ($invisibleLocks.Count -eq 0) -Detail $(if ($invisibleLocks.Count -eq 0) { "видимы: $($lockFiles.Count)" } else { 'не видны: ' + ($invisibleLocks -join ', ') })
        }
    }

    # --- Шаг: владелец настройки restore lock ----------------------------------

    $step++
    Write-AzurPilotStep -Number $step -Total $totalSteps -Title 'Единственный владелец настройки RestorePackagesWithLockFile'

    $propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
    $propsText = if (Test-Path -LiteralPath $propsPath -PathType Leaf) { Get-Content -LiteralPath $propsPath -Raw } else { '' }
    $ownerDeclares = $propsText -match '<RestorePackagesWithLockFile>\s*(?<value>[^<]+?)\s*</RestorePackagesWithLockFile>'
    $ownerValue = if ($ownerDeclares) { $Matches['value'] } else { $null }

    Add-AzurPilotCheck -Name 'владелец настройки restore lock объявлен в Directory.Build.props' -Passed ($ownerDeclares -and $ownerValue -eq 'true') -Detail "Directory.Build.props: RestorePackagesWithLockFile=$ownerValue"

    $nugetConfigPath = Join-Path $repositoryRoot 'NuGet.config'
    if (Test-Path -LiteralPath $nugetConfigPath -PathType Leaf) {
        $nugetText = Get-Content -LiteralPath $nugetConfigPath -Raw
        $nugetDeclares = $nugetText -match '<add\s+key="RestorePackagesWithLockFile"\s+value="(?<value>[^"]+)"\s*/>'

        if ($nugetDeclares) {
            $nugetValue = $Matches['value']
            Add-AzurPilotCheck -Name 'объявления настройки restore lock не противоречат друг другу' -Passed ($nugetValue -eq $ownerValue) -Detail "NuGet.config: $nugetValue; Directory.Build.props: $ownerValue"

            Add-AzurPilotFinding -Message "Настройка RestorePackagesWithLockFile объявлена дважды: NuGet.config (value=$nugetValue) и Directory.Build.props (value=$ownerValue). Фактический владелец — MSBuild-свойство в Directory.Build.props: NuGet не читает этот ключ из NuGet.config (проверено: проект с ключом только в NuGet.config не получает packages.lock.json, а с MSBuild-свойством получает). Поведение одно, но дублирующее объявление в NuGet.config вводит в заблуждение и может разойтись при правке одного места. Решение о его удалении — за владельцем NuGet.config (T2), молча не правлю."
        }
        else {
            Add-AzurPilotCheck -Name 'объявления настройки restore lock не противоречат друг другу' -Passed $true -Detail 'в NuGet.config настройки нет: единственное объявление — Directory.Build.props'
        }
    }

    foreach ($projectPath in $projects) {
        $properties = Get-AzurPilotProjectProperties -ProjectPath $projectPath -DotNetPath $dotnet.Path -WorkingDirectory $repositoryRoot -ConfigurationName $Configuration
        Add-AzurPilotCheck -Name "$(Split-Path -Leaf $projectPath): lock-файл реально включён в restore" -Passed ($properties.RestorePackagesWithLockFile -eq 'true') -Detail "effective RestorePackagesWithLockFile=$($properties.RestorePackagesWithLockFile)"
    }

    # --- Итог -------------------------------------------------------------------

    $failed = @($script:checks | Where-Object { -not $_.Passed })

    if ($failed.Count -gt 0) {
        Write-AzurPilotError -Message "Verification провалена: нарушений $($failed.Count) из $($script:checks.Count) проверок"
        foreach ($check in $failed) { Write-AzurPilotDetail -Message "нарушено: $($check.Name) — $($check.Detail)" }
        exit 1
    }

    Write-AzurPilotSuccess -Message "Verification пройдена: $($script:checks.Count) проверок, нарушений нет (конфигурация $Configuration)"

    if ($script:findings.Count -gt 0) {
        Write-AzurPilotInfo -Message "находок без нарушения контракта: $($script:findings.Count)"
        foreach ($finding in $script:findings) { Write-AzurPilotDetail -Message $finding }
    }

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
