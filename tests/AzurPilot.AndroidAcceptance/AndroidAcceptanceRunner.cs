using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AcceptanceSanitizer = AzurPilot.MuMuAcceptance.AcceptanceSanitizer;

namespace AzurPilot.AndroidAcceptance;

/// <summary>
/// Lifecycle-операция игры, которой адресуется один переход матрицы приёмки.
/// </summary>
internal enum AcceptanceGameOperation
{
    /// <summary>Остановка игры.</summary>
    Stop,

    /// <summary>Запуск игры.</summary>
    Start,

    /// <summary>Перезапуск игры: остановка с подтверждением и последующий запуск.</summary>
    Restart,
}

/// <summary>
/// Прогон приёмки Android: цепочка контракта, безопасная матрица и восстановление начального состояния.
/// </summary>
/// <remarks>
/// <para>
/// Приёмка выполняется production-кодом: обнаружение установки, выбор exact instance и host-side
/// состояние — через <see cref="IMuMuHost"/> и <see cref="MuMuLifecycleService"/>, exact ADB endpoint,
/// готовность Android и lifecycle игры — через <see cref="AndroidReadinessService"/>,
/// <see cref="AzurLaneGameStateService"/> и <see cref="AzurLaneGameLifecycleService"/> платформенной
/// реализации <c>AzurPilot.Windows</c>. Собственной копии логики у инструмента нет: он только компонует
/// production-типы и проверяет наблюдаемые postconditions независимым наблюдением.
/// </para>
/// <para>
/// Предусловие проверяется до единой mutation устройства: выбранный exact instance обязан быть доказанно
/// <see cref="MuMuLifecycleState.Running"/>, иначе прогон заканчивается отказом до readiness и до
/// lifecycle игры.
/// </para>
/// <para>
/// Матрица зависит от наблюдённого начального состояния игры: для остановленной игры это
/// stop → start → restart → stop, для игры на переднем плане — stop → start → restart. В обоих случаях
/// матрица заканчивается тем же состоянием, с которого началась, и это состояние проверяется
/// наблюдением, а не предполагается. Начальное состояние Background приёмкой не поддерживается и
/// отвергается до mutation: Background не считается эквивалентом Foreground.
/// </para>
/// </remarks>
internal sealed class AndroidAcceptanceRunner
{
    private const int StepPrecondition = 1;
    private const int StepAdbExecutable = 2;
    private const int StepEndpoint = 3;
    private const int StepReadiness = 4;
    private const int StepPackage = 5;
    private const int StepInitialState = 6;
    private const int StepMutation = 7;
    private const int StepPostcondition = 8;
    private const int StepRestoration = 9;
    private const int StepAudit = 10;

    /// <summary>Локальный код отказа: exact instance не доказанно Running.</summary>
    private const string PreconditionNotMet = "acceptance_precondition_not_met";

    /// <summary>Локальный код отказа: identity разрешённого экземпляра не совпала с запрошенной.</summary>
    private const string IdentityMismatch = "acceptance_instance_identity_mismatch";

    /// <summary>Локальный код отказа: начальное состояние игры не поддерживается восстановлением.</summary>
    private const string UnsupportedInitialState = "acceptance_unsupported_initial_state";

    /// <summary>Локальный код отказа: состояние игры не доказано наблюдением.</summary>
    private const string StateNotProven = "acceptance_state_not_proven";

    /// <summary>Локальный код отказа: пакет Global/EN не подтверждён установленным.</summary>
    private const string PackageNotInstalled = "acceptance_package_not_installed";

    /// <summary>Локальный код отказа: postcondition перехода не подтверждён независимым наблюдением.</summary>
    private const string PostconditionNotMet = "acceptance_postcondition_not_met";

    /// <summary>Локальный код отказа: перезапуск не подтверждён сменой процесса игры.</summary>
    private const string RestartNotProven = "acceptance_restart_not_proven";

    /// <summary>Локальный код отказа: начальное состояние не восстановлено и не подтверждено.</summary>
    private const string RestorationNotProven = "acceptance_restoration_not_proven";

    /// <summary>Локальный код отказа: аудит обнаружил запрещённое приёмке действие.</summary>
    private const string ForbiddenCommand = "acceptance_forbidden_command";

    /// <summary>Локальный код отказа: цепочка приёмки не выполнялась.</summary>
    private const string NotExecuted = "acceptance_not_executed";

    /// <summary>Плейсхолдер пути bundled ADB в отчёте.</summary>
    private const string AdbExecutablePlaceholder = "<bundled-adb>";

    /// <summary>Имя операции запуска в контракте lifecycle игры.</summary>
    private const string StartOperationName = "start";

    /// <summary>Имя операции остановки в контракте lifecycle игры.</summary>
    private const string StopOperationName = "stop";

    /// <summary>Имя операции перезапуска в контракте lifecycle игры.</summary>
    private const string RestartOperationName = "restart";

    private readonly IMuMuHost _muMuHost;
    private readonly MuMuLifecycleService _muMuLifecycle;
    private readonly IAndroidHost _androidHost;
    private readonly AndroidReadinessService _readiness;
    private readonly AzurLaneGameStateService _stateService;
    private readonly AzurLaneGameLifecycleService _lifecycle;
    private readonly AndroidCommandAudit _audit;
    private readonly AndroidAcceptanceReport _report;

    /// <summary>Создаёт прогон приёмки поверх production-сервисов MuMu и Android.</summary>
    /// <param name="muMuHost">Production host-side поверхность MuMu (предусловие).</param>
    /// <param name="muMuLifecycle">Production orchestration выбора exact instance MuMu.</param>
    /// <param name="androidHost">Production host-side поверхность Android.</param>
    /// <param name="readiness">Production orchestration готовности Android.</param>
    /// <param name="stateService">Production наблюдение состояния игры.</param>
    /// <param name="lifecycle">Production orchestration lifecycle игры.</param>
    /// <param name="audit">Аудит выполненных запусков процессов.</param>
    /// <param name="report">Отчёт приёмки.</param>
    internal AndroidAcceptanceRunner(
        IMuMuHost muMuHost,
        MuMuLifecycleService muMuLifecycle,
        IAndroidHost androidHost,
        AndroidReadinessService readiness,
        AzurLaneGameStateService stateService,
        AzurLaneGameLifecycleService lifecycle,
        AndroidCommandAudit audit,
        AndroidAcceptanceReport report)
    {
        ArgumentNullException.ThrowIfNull(muMuHost);
        ArgumentNullException.ThrowIfNull(muMuLifecycle);
        ArgumentNullException.ThrowIfNull(androidHost);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(stateService);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(report);

        _muMuHost = muMuHost;
        _muMuLifecycle = muMuLifecycle;
        _androidHost = androidHost;
        _readiness = readiness;
        _stateService = stateService;
        _lifecycle = lifecycle;
        _audit = audit;
        _report = report;
    }

    /// <summary>Выполняет цепочку приёмки и восстановление начального состояния игры.</summary>
    /// <remarks>
    /// Восстановление и аудит выполняются в <c>finally</c>: прерывание прогона не должно оставлять игру в
    /// состоянии, которого оператор не выбирал, а отказ цепочки не должен скрывать аудит выполненных
    /// команд. Восстановление идёт без отмены, а ограничивает его deadline самой lifecycle-операции.
    /// </remarks>
    /// <param name="options">Проверенные аргументы прогона.</param>
    /// <param name="cancellationToken">Запрос отмены матрицы переходов.</param>
    /// <returns>Исход приёмки.</returns>
    internal async Task<AcceptanceResult> RunAsync(
        AcceptanceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        AcceptanceContext context = new();
        AcceptanceResult? chain = null;

        try
        {
            chain = await RunChainAsync(options, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AcceptanceResult restoration = await RestoreInitialStateAsync(context).ConfigureAwait(false);
            AcceptanceResult audit = VerifyCommandAudit(options, context);
            chain = Combine(chain, restoration, audit);
        }

        return chain ?? AcceptanceResult.NotProven(
            NotExecuted,
            "acceptance_not_executed: цепочка приёмки не выполнялась.");
    }

    /// <summary>Выполняет шаги 1–8 цепочки приёмки.</summary>
    /// <param name="options">Проверенные аргументы прогона.</param>
    /// <param name="context">Состояние прогона, которое заполняется по ходу цепочки.</param>
    /// <param name="cancellationToken">Запрос отмены матрицы переходов.</param>
    /// <returns>Успех цепочки либо отказ на первом недостигнутом шаге.</returns>
    private async Task<AcceptanceResult> RunChainAsync(
        AcceptanceOptions options,
        AcceptanceContext context,
        CancellationToken cancellationToken)
    {
        // Шаг 1: предусловие — обнаруженная установка MuMu и доказанно Running у запрошенного instance.
        ApplicationResult<MuMuInstallation> discovery = _muMuHost.DiscoverInstallation();
        if (discovery.IsFailure)
        {
            return Fail(StepPrecondition, "Установка MuMuPlayer", discovery.FailureInfo!);
        }

        MuMuInstallation installation = discovery.Value!;
        context.Installation = installation;
        _report.Sanitizer.Protect(installation.InstallRoot, AcceptanceSanitizer.InstallRootPlaceholder);
        _report.Sanitizer.Protect(
            installation.ControlExecutablePath, AcceptanceSanitizer.ControlSurfacePlaceholder);

        MuMuInstanceResolution resolution = _muMuLifecycle.Resolve(
            installation, MuMuInstanceSelection.Explicit(options.InstanceId));

        if (resolution.IsFailed)
        {
            return Fail(StepPrecondition, "Exact instance MuMu", resolution.Failure!);
        }

        MuMuInstance instance = resolution.Instance!;
        if (instance.Id != options.InstanceId)
        {
            return Fail(
                StepPrecondition,
                "Exact instance MuMu",
                IdentityMismatch,
                "Разрешённый экземпляр " + instance.Id.ToString()
                + " не совпал с запрошенным " + options.InstanceValue + ".");
        }

        context.Instance = instance;

        ApplicationResult<MuMuInstanceState> observation =
            _muMuHost.ObserveInstanceState(installation, options.InstanceId);

        if (observation.IsFailure)
        {
            return Fail(StepPrecondition, "Precondition: MuMu Running", observation.FailureInfo!);
        }

        MuMuInstanceState muMuState = observation.Value!;
        if (muMuState.State != MuMuLifecycleState.Running)
        {
            return Fail(
                StepPrecondition,
                "Precondition: MuMu Running",
                PreconditionNotMet,
                "Экземпляр " + options.InstanceValue + " не доказанно Running ("
                + MuMuStateName(muMuState.State) + "), а приёмка требует запущенного экземпляра; evidence: "
                + muMuState.Evidence + "; mutation устройства и игры не выполнялись.");
        }

        _report.Record(
            StepPrecondition,
            "Precondition: установка и Running",
            true,
            "версия MuMu " + installation.Version
            + "; каталог установки " + AcceptanceSanitizer.InstallRootPlaceholder
            + "; identity " + instance.Id.ToString()
            + "; имя «" + instance.DisplayName + "»"
            + "; Android " + instance.AndroidVersion
            + "; состояние " + MuMuStateName(muMuState.State)
            + "; evidence: " + muMuState.Evidence);

        // Шаг 2: bundled ADB обнаруженной установки.
        ApplicationResult<AndroidAdbExecutable> adb = _androidHost.DiscoverAdbExecutable(installation);
        if (adb.IsFailure)
        {
            return Fail(StepAdbExecutable, "Bundled ADB обнаруженной установки", adb.FailureInfo!);
        }

        AndroidAdbExecutable adbExecutable = adb.Value!;
        context.AdbExecutablePath = adbExecutable.Path;
        _report.Sanitizer.Protect(adbExecutable.Path, AdbExecutablePlaceholder);

        _report.Record(
            StepAdbExecutable,
            "Bundled ADB обнаруженной установки",
            true,
            "executable " + AdbExecutablePlaceholder + "; " + adbExecutable.VersionEvidence);

        // Шаг 3: exact ADB endpoint выбранного instance.
        ApplicationResult<AndroidEndpoint> resolvedEndpoint =
            _readiness.ResolveEndpoint(installation, options.InstanceId);

        if (resolvedEndpoint.IsFailure)
        {
            return Fail(StepEndpoint, "Exact ADB endpoint", resolvedEndpoint.FailureInfo!);
        }

        AndroidEndpoint endpoint = resolvedEndpoint.Value!;
        context.Endpoint = endpoint;

        _report.Record(
            StepEndpoint,
            "Exact ADB endpoint",
            true,
            "endpoint " + endpoint.ToString() + "; identity " + instance.Id.ToString()
            + " (значение разрешено из сведений установки, а не вычислено и не подставлено по умолчанию)");

        // Шаг 4: готовность Android — transport, shell и завершённая загрузка.
        ApplicationResult<AndroidReadinessFacts> observedReadiness =
            _readiness.Observe(installation, options.InstanceId);

        if (observedReadiness.IsFailure)
        {
            return Fail(StepReadiness, "Наблюдение Android", observedReadiness.FailureInfo!);
        }

        _report.Record(
            StepReadiness,
            "Наблюдение Android до mutation",
            true,
            "read-only: " + observedReadiness.Value!.Evidence);

        ApplicationResult<AndroidReadinessOutcome> ready =
            await _readiness.EnsureReadyAsync(installation, options.InstanceId, cancellationToken)
                .ConfigureAwait(false);

        if (ready.IsFailure)
        {
            return Fail(StepReadiness, "Готовность Android", ready.FailureInfo!);
        }

        AndroidReadinessOutcome readiness = ready.Value!;
        AndroidBootObservation boot = readiness.Boot;

        _report.Record(
            StepReadiness,
            "Готовность Android",
            true,
            "shell " + (boot.ShellAvailable ? "доступен" : "недоступен")
            + "; boot_completed=" + Observed(boot.BootCompleted)
            + "; Android " + (boot.AndroidRelease ?? "не наблюдалась")
            + "; sdk " + Observed(boot.SdkLevel)
            + "; evidence: " + readiness.Evidence);

        // Шаг 5: установка пакета Global/EN.
        AndroidPackageId package = AzurLaneGameStateService.Package;
        ApplicationResult<AndroidPackagePresence> presence = _androidHost.QueryPackage(endpoint, package);
        if (presence.IsFailure)
        {
            return Fail(StepPackage, "Пакет Azur Lane Global/EN", presence.FailureInfo!);
        }

        if (presence.Value! != AndroidPackagePresence.Installed)
        {
            return Fail(
                StepPackage,
                "Пакет Azur Lane Global/EN",
                PackageNotInstalled,
                "Пакет " + package.ToString() + " (" + AzurLaneProduct.DisplayName + ") не подтверждён "
                + "установленным: наблюдено «" + PresenceName(presence.Value!) + "».");
        }

        _report.Record(
            StepPackage,
            "Пакет Azur Lane Global/EN",
            true,
            "product " + AzurLaneProduct.DisplayName + "; package " + package.ToString()
            + "; наблюдено installed");

        // Шаг 6: начальное состояние игры — до единой mutation игры.
        ApplicationResult<AzurLaneGameObservation> initialObservation = _stateService.Observe(endpoint);
        if (initialObservation.IsFailure)
        {
            return Fail(StepInitialState, "Начальное состояние игры", initialObservation.FailureInfo!);
        }

        AzurLaneGameState initialState = initialObservation.Value!.State;
        if (initialState == AzurLaneGameState.Background)
        {
            return Fail(
                StepInitialState,
                "Начальное состояние игры",
                UnsupportedInitialState,
                "Начальное состояние игры — Background: восстановление поддерживается только для Stopped и "
                + "Foreground, а Background не считается эквивалентом Foreground; mutation игры не "
                + "выполнялась.");
        }

        if (initialState is not (AzurLaneGameState.Stopped or AzurLaneGameState.Foreground))
        {
            return Fail(
                StepInitialState,
                "Начальное состояние игры",
                StateNotProven,
                "Начальное состояние игры не доказано наблюдением («" + GameStateName(initialState)
                + "»); evidence: " + initialObservation.Value!.Evidence
                + "; mutation игры не выполнялась.");
        }

        context.InitialState = initialState;

        _report.Record(
            StepInitialState,
            "Начальное состояние игры",
            true,
            GameStateName(initialState) + "; evidence: " + initialObservation.Value!.Evidence);

        // Шаги 7–8: матрица переходов и postcondition каждого перехода.
        AcceptanceGameOperation[] sequence = initialState == AzurLaneGameState.Stopped
            ? [AcceptanceGameOperation.Stop, AcceptanceGameOperation.Start, AcceptanceGameOperation.Restart,
                AcceptanceGameOperation.Stop]
            : [AcceptanceGameOperation.Stop, AcceptanceGameOperation.Start, AcceptanceGameOperation.Restart];

        _report.Record(
            StepMutation,
            "Матрица переходов",
            true,
            "начальное состояние " + GameStateName(initialState) + "; последовательность: "
            + string.Join(" → ", sequence.Select(OperationName)));

        foreach (AcceptanceGameOperation operation in sequence)
        {
            AcceptanceResult? transition = await RunTransitionAsync(
                installation, endpoint, package, operation, cancellationToken).ConfigureAwait(false);

            if (transition is not null)
            {
                return transition;
            }
        }

        return AcceptanceResult.Proven();
    }

    /// <summary>Выполняет один переход матрицы и доказывает его postcondition независимым наблюдением.</summary>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="endpoint">Точный endpoint, над которым выполняется переход.</param>
    /// <param name="package">Идентификатор пакета игры.</param>
    /// <param name="operation">Операция перехода.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns><see langword="null"/>, если переход доказан; иначе описание отказа.</returns>
    private async Task<AcceptanceResult?> RunTransitionAsync(
        MuMuInstallation installation,
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        AcceptanceGameOperation operation,
        CancellationToken cancellationToken)
    {
        AzurLaneGameState expected = ExpectedState(operation);

        // Семантика перезапуска доказывается сменой процесса игры: наблюдение «на переднем плане» само по
        // себе не отличило бы перезапуск от отсутствия операции. Состояние перед операцией наблюдается
        // независимо: по нему видно, из какого состояния перезапуск действительно начался.
        IReadOnlyList<int>? processesBefore = null;
        AzurLaneGameState? stateBeforeRestart = null;

        if (operation == AcceptanceGameOperation.Restart)
        {
            ApplicationResult<AzurLaneGameObservation> observedBefore = _stateService.Observe(endpoint);
            if (observedBefore.IsFailure)
            {
                return Fail(StepPostcondition, "Семантика перезапуска", observedBefore.FailureInfo!);
            }

            stateBeforeRestart = observedBefore.Value!.State;

            ApplicationResult<AndroidProcessObservation> before =
                _androidHost.ObserveProcesses(endpoint, package);

            if (before.IsFailure)
            {
                return Fail(StepPostcondition, "Семантика перезапуска", before.FailureInfo!);
            }

            processesBefore = before.Value!.ProcessIds;
            if (processesBefore is null || processesBefore.Count == 0)
            {
                return Fail(
                    StepPostcondition,
                    "Семантика перезапуска",
                    RestartNotProven,
                    "Перед перезапуском процессы пакета не наблюдались: смена процесса не может быть "
                    + "доказана.");
            }
        }

        ApplicationResult<AzurLaneGameLifecycleOutcome> outcome =
            await RunOperationAsync(installation, endpoint, operation, cancellationToken).ConfigureAwait(false);

        if (outcome.IsFailure)
        {
            return Fail(StepMutation, "Mutation " + OperationName(operation), outcome.FailureInfo!);
        }

        AzurLaneGameLifecycleOutcome lifecycle = outcome.Value!;
        _report.Record(
            StepMutation,
            "Mutation " + OperationName(operation),
            true,
            OperationName(operation) + ": " + GameStateName(lifecycle.InitialState) + " → "
            + GameStateName(lifecycle.FinalState) + "; " + Milliseconds(lifecycle.Elapsed)
            + "; mutation: " + lifecycle.MutationEvidence
            + (lifecycle.Launcher is AndroidComponent launcher ? "; launcher " + launcher.Flattened : string.Empty)
            + "; evidence: " + lifecycle.Evidence);

        if (!string.Equals(lifecycle.Operation, OperationValue(operation), StringComparison.Ordinal)
            || lifecycle.FinalState != expected)
        {
            return Fail(
                StepPostcondition,
                "Postcondition после " + OperationName(operation),
                PostconditionNotMet,
                "Оркестрация сообщила операцию «" + lifecycle.Operation + "» с состоянием "
                + GameStateName(lifecycle.FinalState) + " вместо " + OperationName(operation) + " → "
                + GameStateName(expected) + ".");
        }

        ApplicationResult<AzurLaneGameObservation> observed = _stateService.Observe(endpoint);
        if (observed.IsFailure)
        {
            return Fail(
                StepPostcondition,
                "Postcondition после " + OperationName(operation),
                observed.FailureInfo!);
        }

        if (observed.Value!.State != expected)
        {
            return Fail(
                StepPostcondition,
                "Postcondition после " + OperationName(operation),
                PostconditionNotMet,
                "Независимое наблюдение показало " + GameStateName(observed.Value!.State) + " вместо "
                + GameStateName(expected) + "; evidence: " + observed.Value!.Evidence);
        }

        _report.Record(
            StepPostcondition,
            "Postcondition после " + OperationName(operation),
            true,
            "независимое наблюдение подтвердило " + GameStateName(expected)
            + "; evidence: " + observed.Value!.Evidence);

        if (operation != AcceptanceGameOperation.Restart)
        {
            return null;
        }

        ApplicationResult<AndroidProcessObservation> after = _androidHost.ObserveProcesses(endpoint, package);
        if (after.IsFailure)
        {
            return Fail(StepPostcondition, "Семантика перезапуска", after.FailureInfo!);
        }

        IReadOnlyList<int>? processesAfter = after.Value!.ProcessIds;
        if (processesAfter is null || processesAfter.Count == 0)
        {
            return Fail(
                StepPostcondition,
                "Семантика перезапуска",
                RestartNotProven,
                "После перезапуска процессы пакета не наблюдались: доказательство перезапуска отсутствует.");
        }

        if (new HashSet<int>(processesBefore!).SetEquals(processesAfter))
        {
            return Fail(
                StepPostcondition,
                "Семантика перезапуска",
                RestartNotProven,
                "Набор процессов пакета не изменился: перезапуск не подтверждён наблюдением.");
        }

        _report.Record(
            StepPostcondition,
            "Семантика перезапуска",
            true,
            "до операции независимо наблюдено " + GameStateName(stateBeforeRestart!.Value)
            + "; процессы пакета пересозданы (было " + Count(processesBefore!.Count) + ", стало "
            + Count(processesAfter.Count) + "); после операции независимо наблюдено "
            + GameStateName(expected));

        return null;
    }

    /// <summary>Восстанавливает начальное состояние игры независимо от исхода матрицы.</summary>
    /// <remarks>
    /// <para>
    /// Восстановление выполняется без отмены: прерывание приёмки не должно оставлять игру в состоянии,
    /// которого оператор не выбирал. Ограничивает восстановление deadline самой lifecycle-операции, а не
    /// отдельный таймаут инструмента: собственного владельца времени у восстановления нет.
    /// </para>
    /// <para>
    /// Одной mutation в сторону начального состояния недостаточно: игра может остаться в промежуточном
    /// состоянии, из которого запуск не подтверждается. Поэтому, если наблюдённое состояние не равно
    /// начальному, выполняется очищающая композиция из тех же production-примитивов: при начальном
    /// Foreground — остановка с подтверждением и запуск с подтверждением; при начальном Stopped —
    /// остановка с подтверждением. Остановка первой сбрасывает промежуточное состояние, а не добавляет
    /// лишний переход: на уже остановленной игре она не меняет состояние.
    /// </para>
    /// </remarks>
    /// <param name="context">Состояние прогона.</param>
    /// <returns>Исход восстановления начального состояния.</returns>
    private async Task<AcceptanceResult> RestoreInitialStateAsync(AcceptanceContext context)
    {
        const string StepName = "Восстановление начального состояния";

        if (context.Installation is not MuMuInstallation installation
            || context.Endpoint is not AndroidEndpoint endpoint
            || context.InitialState is not AzurLaneGameState initialState)
        {
            _report.Record(
                StepRestoration,
                StepName,
                true,
                "не требуется: состояние игры не наблюдалось, mutation игры не выполнялась");
            return AcceptanceResult.Proven();
        }

        ApplicationResult<AzurLaneGameObservation> observation = _stateService.Observe(endpoint);

        string reason;
        if (observation.IsFailure)
        {
            reason = "наблюдение состояния отказало (" + observation.FailureInfo!.Code
                + "), восстановление запрошено без подтверждённого состояния";
        }
        else if (observation.Value!.State == initialState)
        {
            _report.Record(
                StepRestoration,
                StepName,
                true,
                GameStateName(initialState) + ": состояние уже соответствует начальному, mutation не "
                + "потребовалась");
            return AcceptanceResult.Proven();
        }
        else
        {
            reason = "наблюдённое состояние " + GameStateName(observation.Value!.State)
                + " отличается от начального " + GameStateName(initialState);
        }

        AcceptanceGameOperation[] composition = initialState == AzurLaneGameState.Foreground
            ? [AcceptanceGameOperation.Stop, AcceptanceGameOperation.Start]
            : [AcceptanceGameOperation.Stop];

        foreach (AcceptanceGameOperation step in composition)
        {
            AcceptanceResult confirmed = await ConfirmLifecycleStepAsync(
                installation, endpoint, step, ExpectedState(step)).ConfigureAwait(false);

            if (!confirmed.IsProven)
            {
                return FailRestoration(endpoint, reason + "; " + confirmed.FailureMessage);
            }
        }

        ApplicationResult<AzurLaneGameObservation> finalObservation = _stateService.Observe(endpoint);
        if (finalObservation.IsFailure)
        {
            return FailRestoration(
                endpoint,
                reason + "; итоговое наблюдение отказало: " + finalObservation.FailureInfo!.Code);
        }

        if (finalObservation.Value!.State != initialState)
        {
            return FailRestoration(
                endpoint,
                reason + "; после композиции наблюдено " + GameStateName(finalObservation.Value!.State)
                + " вместо " + GameStateName(initialState));
        }

        _report.Record(
            StepRestoration,
            StepName,
            true,
            reason + "; композиция довела игру до " + GameStateName(initialState)
            + " и состояние подтверждено независимым наблюдением");

        return AcceptanceResult.Proven();
    }

    /// <summary>Выполняет один шаг восстанавливающей композиции и подтверждает его наблюдением.</summary>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="endpoint">Точный endpoint, над которым выполняется шаг.</param>
    /// <param name="operation">Операция шага.</param>
    /// <param name="expected">Состояние, которое обязано быть доказано после шага.</param>
    /// <returns>Успех шага либо отказ восстановления.</returns>
    private async Task<AcceptanceResult> ConfirmLifecycleStepAsync(
        MuMuInstallation installation,
        AndroidEndpoint endpoint,
        AcceptanceGameOperation operation,
        AzurLaneGameState expected)
    {
        ApplicationResult<AzurLaneGameLifecycleOutcome> outcome =
            await RunOperationAsync(installation, endpoint, operation, CancellationToken.None)
                .ConfigureAwait(false);

        if (outcome.IsFailure)
        {
            return AcceptanceResult.NotProven(
                RestorationNotProven,
                "шаг " + OperationName(operation) + " отказал: " + outcome.FailureInfo!.Code + " ("
                + outcome.FailureInfo!.Message + ")");
        }

        ApplicationResult<AzurLaneGameObservation> observed = _stateService.Observe(endpoint);
        if (observed.IsFailure)
        {
            return AcceptanceResult.NotProven(
                RestorationNotProven,
                "наблюдение после шага " + OperationName(operation) + " отказало: "
                + observed.FailureInfo!.Code);
        }

        return observed.Value!.State == expected
            ? AcceptanceResult.Proven()
            : AcceptanceResult.NotProven(
                RestorationNotProven,
                "после шага " + OperationName(operation) + " наблюдено "
                + GameStateName(observed.Value!.State) + " вместо " + GameStateName(expected));
    }

    /// <summary>Проверяет аудит выполненных запусков и фиксирует bounded evidence прогона.</summary>
    /// <param name="options">Проверенные аргументы прогона.</param>
    /// <param name="context">Состояние прогона.</param>
    /// <returns>Успех аудита либо отказ с описанием нарушения.</returns>
    private AcceptanceResult VerifyCommandAudit(AcceptanceOptions options, AcceptanceContext context)
    {
        string summary = _audit.Describe(context.AdbExecutablePath);

        if (context.Installation is not MuMuInstallation installation
            || context.AdbExecutablePath is not string adbExecutablePath)
        {
            _report.Record(
                StepAudit,
                "Аудит выполненных команд",
                true,
                summary + "; установка или bundled ADB не обнаружены, запусков установки не было");
            return AcceptanceResult.Proven();
        }

        string? violation = _audit.FindViolation(
            installation.ControlExecutablePath,
            adbExecutablePath,
            options.InstanceId,
            context.Endpoint,
            AzurLaneGameStateService.Package);

        if (violation is not null)
        {
            return Fail(StepAudit, "Аудит выполненных команд", ForbiddenCommand, violation + "; " + summary);
        }

        _report.Record(
            StepAudit,
            "Аудит выполненных команд",
            true,
            summary + "; запускались только bundled ADB и control surface обнаруженной установки и только "
            + "команды production-контракта: запрещённые действия (kill-server, install/uninstall, clear "
            + "data/cache, permissions reset, настройки эмулятора, input, screenshot, другой package, "
            + "другой ADB target) не обнаружены");

        _report.Record(StepAudit, "Формы выполненных команд", true, _audit.DescribeForms(adbExecutablePath));

        _report.Record(
            StepAudit,
            "Bounded sanitized evidence и revision",
            true,
            "отчёт в stdout, structured log в stderr (записей: "
            + Count(StderrLoggerProvider.WrittenRecordCount)
            + ", предупреждений: " + Count(StderrLoggerProvider.WarningRecordCount)
            + ", ошибок: " + Count(StderrLoggerProvider.ErrorRecordCount)
            + "); bounded текст ограничен " + Count(BoundedDiagnosticText.MaxLength)
            + " символами; machine-specific значения заменены плейсхолдерами; screenshot и input-проверок в "
            + "инструменте нет");

        return AcceptanceResult.Proven();
    }

    /// <summary>Выполняет одну lifecycle-операцию игры production-сервисом.</summary>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="endpoint">Точный endpoint, над которым выполняется операция.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог операции либо ожидаемый отказ.</returns>
    private Task<ApplicationResult<AzurLaneGameLifecycleOutcome>> RunOperationAsync(
        MuMuInstallation installation,
        AndroidEndpoint endpoint,
        AcceptanceGameOperation operation,
        CancellationToken cancellationToken)
        => operation switch
        {
            AcceptanceGameOperation.Start => _lifecycle.StartAsync(installation, endpoint, cancellationToken),
            AcceptanceGameOperation.Stop => _lifecycle.StopAsync(installation, endpoint, cancellationToken),
            AcceptanceGameOperation.Restart => _lifecycle.RestartAsync(installation, endpoint, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation), operation, "Lifecycle-операция игры не определена."),
        };

    /// <summary>Возвращает доказанное состояние игры, которым обязана закончиться операция.</summary>
    /// <param name="operation">Операция перехода.</param>
    /// <returns>Ожидаемое состояние игры.</returns>
    private static AzurLaneGameState ExpectedState(AcceptanceGameOperation operation) => operation switch
    {
        AcceptanceGameOperation.Start => AzurLaneGameState.Foreground,
        AcceptanceGameOperation.Stop => AzurLaneGameState.Stopped,
        AcceptanceGameOperation.Restart => AzurLaneGameState.Foreground,
        _ => AzurLaneGameState.Unknown,
    };

    /// <summary>Возвращает имя операции в контракте lifecycle игры.</summary>
    /// <param name="operation">Операция перехода.</param>
    /// <returns>Значение <c>start</c>, <c>stop</c> или <c>restart</c>.</returns>
    private static string OperationValue(AcceptanceGameOperation operation) => operation switch
    {
        AcceptanceGameOperation.Start => StartOperationName,
        AcceptanceGameOperation.Stop => StopOperationName,
        AcceptanceGameOperation.Restart => RestartOperationName,
        _ => string.Empty,
    };

    /// <summary>Возвращает имя операции для отчёта.</summary>
    /// <param name="operation">Операция перехода.</param>
    /// <returns>Имя операции в форме контракта.</returns>
    private static string OperationName(AcceptanceGameOperation operation) => OperationValue(operation);

    /// <summary>Фиксирует отказ восстановления и описывает фактически оставленное состояние.</summary>
    /// <param name="endpoint">Точный endpoint, состояние которого описывается.</param>
    /// <param name="message">Описание отказа восстановления.</param>
    /// <returns>Исход с отказом восстановления.</returns>
    private AcceptanceResult FailRestoration(AndroidEndpoint endpoint, string message)
    {
        ApplicationResult<AzurLaneGameObservation> left = _stateService.Observe(endpoint);
        string leftState = left.IsSuccess
            ? GameStateName(left.Value!.State) + " [" + left.Value!.Evidence + "]"
            : "наблюдение отказало (" + left.FailureInfo!.Code + ")";

        _report.Record(
            StepRestoration,
            "Фактически оставленное на машине состояние",
            false,
            "оставлено на машине: " + leftState);

        return Fail(StepRestoration, "Восстановление начального состояния", RestorationNotProven, message);
    }

    /// <summary>Записывает отказ production-кода как исход приёмки.</summary>
    /// <param name="step">Номер шага, на котором получен отказ.</param>
    /// <param name="stepName">Название шага.</param>
    /// <param name="failure">Ожидаемый отказ production-кода.</param>
    /// <returns>Исход приёмки с кодом и сообщением отказа.</returns>
    private AcceptanceResult Fail(int step, string stepName, ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        _report.Record(step, stepName, false, failure.Code + ": " + failure.Message);
        return AcceptanceResult.NotProven(failure.Code, failure.Code + ": " + failure.Message);
    }

    /// <summary>Записывает локальный отказ приёмки как исход.</summary>
    /// <param name="step">Номер шага, на котором получен отказ.</param>
    /// <param name="stepName">Название шага.</param>
    /// <param name="code">Локальный код отказа приёмки.</param>
    /// <param name="message">Описание отказа.</param>
    /// <returns>Исход приёмки с кодом и сообщением отказа.</returns>
    private AcceptanceResult Fail(int step, string stepName, string code, string message)
    {
        _report.Record(step, stepName, false, message);
        return AcceptanceResult.NotProven(code, code + ": " + message);
    }

    /// <summary>Объединяет исходы цепочки, восстановления и аудита: приёмка доказана только целиком.</summary>
    /// <param name="chain">Исход цепочки или <see langword="null"/>, если цепочка не выполнялась.</param>
    /// <param name="restoration">Исход восстановления начального состояния.</param>
    /// <param name="audit">Исход аудита выполненных запусков.</param>
    /// <returns>Первый недостигнутый исход либо доказанный исход.</returns>
    private static AcceptanceResult Combine(
        AcceptanceResult? chain,
        AcceptanceResult restoration,
        AcceptanceResult audit)
    {
        if (chain is not AcceptanceResult chainResult)
        {
            return AcceptanceResult.NotProven(
                NotExecuted,
                "acceptance_not_executed: цепочка приёмки не выполнялась.");
        }

        if (!chainResult.IsProven)
        {
            return chainResult;
        }

        return restoration.IsProven ? audit : restoration;
    }

    /// <summary>Возвращает текстовое имя состояния экземпляра MuMu для отчёта.</summary>
    /// <param name="state">Наблюдённое состояние.</param>
    /// <returns>Описание состояния на русском языке.</returns>
    private static string MuMuStateName(MuMuLifecycleState state) => state switch
    {
        MuMuLifecycleState.Running => "запущен",
        MuMuLifecycleState.Stopped => "остановлен",
        MuMuLifecycleState.Unknown => "не доказано",
        _ => "не доказано",
    };

    /// <summary>Возвращает текстовое имя состояния игры для отчёта.</summary>
    /// <param name="state">Наблюдённое состояние игры.</param>
    /// <returns>Описание состояния на русском языке.</returns>
    private static string GameStateName(AzurLaneGameState state) => state switch
    {
        AzurLaneGameState.NotInstalled => "не установлена",
        AzurLaneGameState.Stopped => "остановлена",
        AzurLaneGameState.Background => "в фоне",
        AzurLaneGameState.Foreground => "на переднем плане",
        AzurLaneGameState.Unknown => "не доказано",
        _ => "не доказано",
    };

    /// <summary>Возвращает текстовое имя наблюдённого присутствия пакета для отчёта.</summary>
    /// <param name="presence">Наблюдённое присутствие пакета.</param>
    /// <returns>Описание присутствия на русском языке.</returns>
    private static string PresenceName(AndroidPackagePresence presence) => presence switch
    {
        AndroidPackagePresence.Installed => "установлен",
        AndroidPackagePresence.Absent => "отсутствует",
        AndroidPackagePresence.QueryFailed => "не доказано",
        _ => "не доказано",
    };

    /// <summary>Возвращает bounded длительность операции в миллисекундах.</summary>
    /// <param name="elapsed">Длительность операции.</param>
    /// <returns>Строка вида <c>1234 мс</c>.</returns>
    private static string Milliseconds(TimeSpan elapsed)
        => ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " мс";

    /// <summary>Возвращает наблюдённое числовое значение или признак его отсутствия.</summary>
    /// <param name="value">Наблюдённое значение либо <see langword="null"/>.</param>
    /// <returns>Текстовая форма значения.</returns>
    private static string Observed(int? value)
        => value is int observed ? Count(observed) : "не наблюдалось";

    /// <summary>Возвращает значение счётчика в инвариантной культуре.</summary>
    /// <param name="value">Числовое значение.</param>
    /// <returns>Текстовая форма значения.</returns>
    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Состояние прогона: обнаруженная установка, выбранный экземпляр, точный endpoint и начальное
    /// состояние игры.
    /// </summary>
    /// <remarks>
    /// Начальное состояние заполняется только после его доказанного наблюдения и до единой mutation игры:
    /// по нему решается, нужна ли восстанавливающая композиция.
    /// </remarks>
    private sealed class AcceptanceContext
    {
        /// <summary>Обнаруженная установка MuMuPlayer.</summary>
        internal MuMuInstallation? Installation { get; set; }

        /// <summary>Разрешённый exact instance.</summary>
        internal MuMuInstance? Instance { get; set; }

        /// <summary>Путь bundled ADB обнаруженной установки.</summary>
        internal string? AdbExecutablePath { get; set; }

        /// <summary>Разрешённый exact ADB endpoint.</summary>
        internal AndroidEndpoint? Endpoint { get; set; }

        /// <summary>Доказанное начальное состояние игры.</summary>
        internal AzurLaneGameState? InitialState { get; set; }
    }
}
