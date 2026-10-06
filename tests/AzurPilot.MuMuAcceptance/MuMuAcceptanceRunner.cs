using System.Globalization;
using System.Text.Json;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;

namespace AzurPilot.MuMuAcceptance;

/// <summary>
/// Прогон приёмки MuMu: десять шагов контракта, безопасная матрица и восстановление начального состояния.
/// </summary>
/// <remarks>
/// <para>
/// Приёмка выполняется production-кодом: обнаружение установки, перечисление экземпляров и наблюдение
/// состояния — через <see cref="IMuMuHost"/>, выбор экземпляра и переходы — через
/// <see cref="MuMuLifecycleService"/>, форма control surface — через <see cref="MuMuControlSurfaceProbe"/>.
/// Собственной копии логики у инструмента нет: он только компонует production-типы и проверяет
/// наблюдаемые postconditions.
/// </para>
/// <para>
/// Безопасная матрица зависит от наблюдённого начального состояния: для запущенного экземпляра это
/// остановка → запуск → перезапуск, для остановленного — запуск → перезапуск → остановка. В обоих
/// случаях матрица заканчивается тем же состоянием, с которого началась, и это состояние проверяется
/// наблюдением, а не предполагается.
/// </para>
/// </remarks>
internal sealed class MuMuAcceptanceRunner
{
    private const int StepInstallation = 1;
    private const int StepControlSurface = 2;
    private const int StepInstance = 3;
    private const int StepInitialState = 4;
    private const int StepCurrentStatus = 5;
    private const int StepMutation = 6;
    private const int StepPostcondition = 7;
    private const int StepRestartSemantics = 8;
    private const int StepRestoration = 9;
    private const int StepEvidence = 10;

    private const string StateNotProvenWord = "не доказано";
    private const string StateStoppedWord = "остановлен";
    private const string StateRunningWord = "запущен";

    /// <summary>Локальный код отказа приёмки: начальное состояние не восстановлено и не подтверждено.</summary>
    private const string RestorationNotProven = "acceptance_restoration_not_proven";

    /// <summary>Локальный код отказа приёмки: условие сценария на входе не выполнено.</summary>
    private const string ScenarioPreconditionNotProven = "acceptance_scenario_precondition_not_met";

    /// <summary>Текст отсутствующего provider-поля в снимке состояния.</summary>
    private const string MissingField = "нет";

    /// <summary>Текстовое представление нулевого кода ошибки запуска провайдера.</summary>
    private const string ZeroCode = "0";

    private readonly IMuMuHost _host;
    private readonly MuMuLifecycleService _lifecycle;
    private readonly IMuMuProcessRunner _processRunner;
    private readonly IMuMuFileSystemProbe _fileSystemProbe;
    private readonly MuMuCommandAudit _audit;
    private readonly AcceptanceReport _report;

    /// <summary>Создаёт прогон приёмки поверх production-сервисов MuMu.</summary>
    /// <param name="host">Production host-side поверхность MuMu.</param>
    /// <param name="lifecycle">Production orchestration lifecycle MuMu.</param>
    /// <param name="processRunner">Граница запуска процесса control surface.</param>
    /// <param name="fileSystemProbe">Граница файловой системы для проверки обнаруженной установки.</param>
    /// <param name="audit">Аудит выполненных запусков процессов.</param>
    /// <param name="report">Отчёт приёмки.</param>
    internal MuMuAcceptanceRunner(
        IMuMuHost host,
        MuMuLifecycleService lifecycle,
        IMuMuProcessRunner processRunner,
        IMuMuFileSystemProbe fileSystemProbe,
        MuMuCommandAudit audit,
        AcceptanceReport report)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(fileSystemProbe);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(report);

        _host = host;
        _lifecycle = lifecycle;
        _processRunner = processRunner;
        _fileSystemProbe = fileSystemProbe;
        _audit = audit;
        _report = report;
    }

    /// <summary>Выполняет приёмку exact instance и возвращает её исход.</summary>
    /// <param name="options">Проверенные аргументы прогона: instance, сценарий и число повторений.</param>
    /// <param name="cancellationToken">Запрос отмены матрицы.</param>
    /// <returns>Исход приёмки: доказана или нет, и почему.</returns>
    internal async Task<AcceptanceResult> RunAsync(
        AcceptanceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        AcceptanceContext context = new();
        AcceptanceResult? matrix = null;

        try
        {
            matrix = options.Scenario == AcceptanceScenario.Matrix
                ? await RunMatrixAsync(options.InstanceId, context, cancellationToken).ConfigureAwait(false)
                : await RunScenarioAsync(options, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Восстановление, итоговый снимок и аудит выполняются при любом исходе. Отмена или отказ
            // матрицы не должны оставлять экземпляр в промежуточном состоянии, а запуск посторонней
            // программы обязан быть замечен даже тогда, когда прогон не дошёл до конца.
            AcceptanceResult restoration = await RestoreInitialStateAsync(context).ConfigureAwait(false);
            AcceptanceResult finalState = await VerifyFinalStateAsync(context).ConfigureAwait(false);
            RecordScenarioSummary(context);
            AcceptanceResult audit = VerifyCommandAudit(options.InstanceId, context);
            matrix = Combine(matrix, restoration, finalState, audit);
        }

        return matrix ?? AcceptanceResult.NotProven(
            "acceptance_not_executed",
            "Матрица приёмки не выполнялась.");
    }

    /// <summary>Выполняет шаги 1–3 контракта: установка, control surface и exact instance.</summary>
    /// <remarks>
    /// Подготовка общая для всех режимов: и матрица, и сценарий обязаны сначала доказать обнаруженную
    /// установку, форму control surface и выбранную identity, а не предполагать их.
    /// </remarks>
    /// <param name="requested">Exact instance, выбранный оператором.</param>
    /// <param name="context">Состояние прогона, в которое записывается подготовка.</param>
    /// <param name="cancellationToken">Запрос отмены прогона.</param>
    /// <returns>Отказ подготовки или <see langword="null"/>, если прогон можно продолжать.</returns>
    private async Task<AcceptanceResult?> PrepareRunAsync(
        MuMuInstanceId requested,
        AcceptanceContext context,
        CancellationToken cancellationToken)
    {
        // Шаг 1: обнаружение установки и версии.
        ApplicationResult<MuMuInstallation> discovery = _host.DiscoverInstallation();
        if (discovery.IsFailure)
        {
            return Fail(StepInstallation, "Обнаружение установки и версии", discovery.FailureInfo!);
        }

        MuMuInstallation installation = discovery.Value!;
        context.Installation = installation;
        _report.Sanitizer.Protect(installation.InstallRoot, AcceptanceSanitizer.InstallRootPlaceholder);
        _report.Sanitizer.Protect(
            installation.ControlExecutablePath,
            AcceptanceSanitizer.ControlSurfacePlaceholder);
        _report.Record(
            StepInstallation,
            "Обнаружение установки и версии",
            true,
            "версия " + installation.Version
            + "; каталог установки " + AcceptanceSanitizer.InstallRootPlaceholder
            + "; control surface " + AcceptanceSanitizer.ControlSurfacePlaceholder);

        // Шаг 2: доказательство support/control surface.
        MuMuManagerClient client = new(
            _processRunner,
            new MuMuControlSurface { ExecutablePath = installation.ControlExecutablePath });
        context.Client = client;

        ApplicationResult<MuMuCapabilityReport> capability =
            await new MuMuControlSurfaceProbe(client).ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (capability.IsFailure)
        {
            return Fail(StepControlSurface, "Support/control surface", capability.FailureInfo!);
        }

        MuMuCapabilityReport capabilityReport = capability.Value!;
        if (!capabilityReport.IsControlSurfaceSupported)
        {
            ApplicationFailure unsupported = capabilityReport.UnsupportedReason
                ?? LocalFailure("acceptance_control_surface_unsupported", "Форма control surface не подтверждена.");
            return Fail(StepControlSurface, "Support/control surface", unsupported);
        }

        if (!_fileSystemProbe.FileExists(installation.ControlExecutablePath))
        {
            return Fail(
                StepControlSurface,
                "Support/control surface",
                "acceptance_control_surface_missing",
                "Обнаруженный путь control surface не существует как файл.");
        }

        _report.Record(
            StepControlSurface,
            "Support/control surface",
            true,
            "форма подтверждена чтением: version распознан, перечисление распознано, экземпляров "
            + capabilityReport.EnumeratedInstanceCount.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)
            + "; сообщённая control surface версия " + capabilityReport.ReportedVersion
            + "; обнаруженная установка версии " + installation.Version);

        // Шаг 3: обнаружение exact instance.
        ApplicationResult<IReadOnlyList<MuMuInstance>> enumerated = _host.EnumerateInstances(installation);
        if (enumerated.IsFailure)
        {
            return Fail(StepInstance, "Обнаружение exact instance", enumerated.FailureInfo!);
        }

        IReadOnlyList<MuMuInstance> instances = enumerated.Value!;
        MuMuInstance? found = null;
        foreach (MuMuInstance candidate in instances)
        {
            if (candidate.Id == requested)
            {
                found = candidate;
            }
        }

        if (found is null)
        {
            return Fail(
                StepInstance,
                "Обнаружение exact instance",
                "acceptance_instance_not_found",
                "Exact instance " + requested
                + " не найден среди перечисленных экземпляров (всего "
                + instances.Count.ToString(CultureInfo.InvariantCulture) + ").");
        }

        // Выбор выполняется production-семантикой: явный выбор по stable identity, не по имени и не по
        // позиции в перечислении.
        MuMuInstanceResolution resolution =
            _lifecycle.Resolve(installation, MuMuInstanceSelection.Explicit(requested));
        if (resolution.IsFailed)
        {
            return Fail(StepInstance, "Обнаружение exact instance", resolution.Failure!);
        }

        MuMuInstance instance = resolution.Instance!;
        if (instance.Id != requested)
        {
            return Fail(
                StepInstance,
                "Обнаружение exact instance",
                "acceptance_identity_mismatch",
                "Разрешение выбора вернуло другую identity вместо запрошенной.");
        }

        context.Instance = instance;
        _report.Record(
            StepInstance,
            "Обнаружение exact instance",
            true,
            "identity " + instance.Id.ToString()
            + "; отображаемое имя «" + instance.DisplayName + "»"
            + "; Android " + instance.AndroidVersion
            + "; всего экземпляров в установке: " + instances.Count.ToString(CultureInfo.InvariantCulture));

        return null;
    }

    /// <summary>Выполняет шаги 1–8 контракта приёмки.</summary>
    /// <param name="requested">Exact instance, выбранный оператором.</param>
    /// <param name="context">Состояние прогона, доступное восстановлению.</param>
    /// <param name="cancellationToken">Запрос отмены матрицы.</param>
    /// <returns>Исход матрицы без восстановления и аудита.</returns>
    private async Task<AcceptanceResult> RunMatrixAsync(
        MuMuInstanceId requested,
        AcceptanceContext context,
        CancellationToken cancellationToken)
    {
        if (await PrepareRunAsync(requested, context, cancellationToken).ConfigureAwait(false)
            is AcceptanceResult preparationFailure)
        {
            return preparationFailure;
        }

        MuMuInstallation installation = context.Installation!;
        MuMuInstance instance = context.Instance!;
        MuMuManagerClient client = context.Client!;

        // Шаг 4: начальное lifecycle state.
        ApplicationResult<MuMuInstanceState> initialObservation =
            _host.ObserveInstanceState(installation, requested);
        if (initialObservation.IsFailure)
        {
            return Fail(StepInitialState, "Начальное lifecycle state", initialObservation.FailureInfo!);
        }

        MuMuLifecycleState initial = initialObservation.Value!.State;
        if (initial == MuMuLifecycleState.Unknown)
        {
            // Деградированная комбинация (например, процесс запущен, Android не запущен,
            // player_state=start_finished с ошибкой запуска) отображается как Unknown, а не как Running:
            // состояние не выводится из факта существования процесса. Матрица в этом случае не
            // начинается, потому что безопасная последовательность определена только для доказанного
            // Running или Stopped.
            return Fail(
                StepInitialState,
                "Начальное lifecycle state",
                "acceptance_state_not_proven",
                "Состояние экземпляра не доказано, поэтому безопасная матрица не определена; evidence: "
                + initialObservation.Value!.Evidence);
        }

        context.InitialState = initial;
        _report.Record(
            StepInitialState,
            "Начальное lifecycle state",
            true,
            Name(initial) + "; evidence: " + initialObservation.Value!.Evidence);

        // Шаг 5: текущий статус.
        ApplicationResult<MuMuInstanceState> currentObservation =
            _host.ObserveInstanceState(installation, requested);
        if (currentObservation.IsFailure)
        {
            return Fail(StepCurrentStatus, "Текущий статус", currentObservation.FailureInfo!);
        }

        MuMuLifecycleState current = currentObservation.Value!.State;
        if (current != initial)
        {
            return Fail(
                StepCurrentStatus,
                "Текущий статус",
                "acceptance_state_changed",
                "Состояние изменилось между наблюдениями: " + Name(initial) + " → " + Name(current) + ".");
        }

        _report.Record(
            StepCurrentStatus,
            "Текущий статус",
            true,
            Name(current) + "; повторное наблюдение совпало с начальным");

        // Шаги 6–8: безопасная матрица, postcondition после каждой mutation и семантика restart.
        MuMuLifecycleOperation[] sequence = initial == MuMuLifecycleState.Running
            ? [MuMuLifecycleOperation.Stop, MuMuLifecycleOperation.Start, MuMuLifecycleOperation.Restart]
            : [MuMuLifecycleOperation.Start, MuMuLifecycleOperation.Restart, MuMuLifecycleOperation.Stop];

        _report.Record(
            StepMutation,
            "Mutation sequence",
            true,
            "начальное состояние " + Name(initial)
            + "; последовательность: " + string.Join(" → ", sequence.Select(Name)));

        foreach (MuMuLifecycleOperation operation in sequence)
        {
            MuMuInstanceInfo? beforeRestart = null;
            if (operation == MuMuLifecycleOperation.Restart)
            {
                ApplicationResult<MuMuInstanceInfo?> before = await QueryInstanceInfoAsync(
                    client, requested, cancellationToken).ConfigureAwait(false);
                if (before.IsFailure)
                {
                    return Fail(StepRestartSemantics, "Семантика restart", before.FailureInfo!);
                }

                beforeRestart = before.Value;
                if (beforeRestart is null)
                {
                    return Fail(
                        StepRestartSemantics,
                        "Семантика restart",
                        "acceptance_restart_not_proven",
                        "Провайдер не сообщил сведения об экземпляре перед перезапуском.");
                }
            }

            ApplicationResult<MuMuLifecycleOutcome> outcome = await RunOperationAsync(
                installation, instance, operation, cancellationToken).ConfigureAwait(false);
            if (outcome.IsFailure)
            {
                return Fail(StepMutation, "Mutation " + Name(operation), outcome.FailureInfo!);
            }

            MuMuLifecycleOutcome lifecycle = outcome.Value!;
            _report.Record(
                StepMutation,
                "Mutation " + Name(operation),
                true,
                Name(lifecycle.InitialState) + " → " + Name(lifecycle.FinalState)
                + "; " + Milliseconds(lifecycle.Elapsed) + " мс"
                + "; evidence: " + lifecycle.Evidence);

            MuMuLifecycleState expected = ExpectedState(operation);
            if (lifecycle.Operation != operation || lifecycle.FinalState != expected)
            {
                return Fail(
                    StepPostcondition,
                    "Terminal postcondition после " + Name(operation),
                    "acceptance_postcondition_not_met",
                    "Оркестрация доказала " + Name(lifecycle.FinalState)
                    + " вместо " + Name(expected) + ".");
            }

            // Postcondition доказывается независимым наблюдением, а не итогом операции.
            ApplicationResult<MuMuInstanceState> observed = _host.ObserveInstanceState(installation, requested);
            if (observed.IsFailure)
            {
                return Fail(
                    StepPostcondition,
                    "Terminal postcondition после " + Name(operation),
                    observed.FailureInfo!);
            }

            if (observed.Value!.State != expected)
            {
                return Fail(
                    StepPostcondition,
                    "Terminal postcondition после " + Name(operation),
                    "acceptance_postcondition_not_met",
                    "Независимое наблюдение показало " + Name(observed.Value!.State)
                    + " вместо " + Name(expected) + ".");
            }

            _report.Record(
                StepPostcondition,
                "Terminal postcondition после " + Name(operation),
                true,
                "независимое наблюдение подтвердило " + Name(expected)
                + "; evidence: " + observed.Value!.Evidence);

            if (operation == MuMuLifecycleOperation.Restart)
            {
                AcceptanceResult restart = await VerifyRestartSemanticsAsync(
                    requested, installation, client, beforeRestart, cancellationToken).ConfigureAwait(false);
                if (!restart.IsProven)
                {
                    return restart;
                }
            }
        }

        return AcceptanceResult.Proven();
    }

    /// <summary>Возвращает план сценария: требуемое состояние, подготовку и проверяемый переход.</summary>
    /// <param name="scenario">Выбранный сценарий.</param>
    /// <returns>План выполнения сценария.</returns>
    private static ScenarioPlan Plan(AcceptanceScenario scenario) => scenario switch
    {
        AcceptanceScenario.ColdStart => new ScenarioPlan(
            MuMuLifecycleState.Stopped,
            ScenarioPreparation.RequireExisting,
            MuMuLifecycleOperation.Start,
            MuMuLifecycleState.Running),
        AcceptanceScenario.StartAfterStop => new ScenarioPlan(
            MuMuLifecycleState.Stopped,
            ScenarioPreparation.Always,
            MuMuLifecycleOperation.Start,
            MuMuLifecycleState.Running),
        AcceptanceScenario.RestartFromStopped => new ScenarioPlan(
            MuMuLifecycleState.Stopped,
            ScenarioPreparation.Ensure,
            MuMuLifecycleOperation.Restart,
            MuMuLifecycleState.Running),
        AcceptanceScenario.RestartFromRunning => new ScenarioPlan(
            MuMuLifecycleState.Running,
            ScenarioPreparation.Ensure,
            MuMuLifecycleOperation.Restart,
            MuMuLifecycleState.Running),
        AcceptanceScenario.Matrix => throw NotATransitionScenario(scenario),
        _ => throw NotATransitionScenario(scenario),
    };

    /// <summary>Создаёт исключение для сценария, который не является одним переходом.</summary>
    /// <param name="scenario">Неподходящий сценарий.</param>
    /// <returns>Исключение с описанием причины.</returns>
    private static ArgumentOutOfRangeException NotATransitionScenario(AcceptanceScenario scenario)
        => new(
            nameof(scenario),
            scenario,
            "Полная матрица выполняется отдельным режимом, а не как сценарий одного перехода.");

    /// <summary>Выполняет один сценарий приёмки заданное число раз.</summary>
    /// <remarks>
    /// <para>
    /// Сценарий проверяет ровно один переход <c>Stopped → Running</c>. Провайдерская гонка относится к
    /// launch, поэтому каждый путь проверяется отдельно: обычный запуск из Stopped, запуск сразу после
    /// остановки, перезапуск запущенного экземпляра и перезапуск остановленного. Ни один путь не
    /// считается доказанным по другому.
    /// </para>
    /// <para>
    /// Состояние, из которого выполняется переход, доводится production-примитивами и подтверждается
    /// наблюдением. У холодного запуска подготовка запрещена: если экземпляр уже запущен, сценарий
    /// отклоняется без mutation, иначе он перестал бы отличаться от запуска после недавней остановки.
    /// </para>
    /// <para>
    /// Факт повтора launch берётся из диагностики production-кода: из числа реально отправленных команд
    /// <c>launch</c> (production-контракт перехода отправляет ровно один launch, повтор добавляет ровно
    /// один) и из записей уровня Warning, а не из длительности операции.
    /// </para>
    /// </remarks>
    /// <param name="options">Проверенные аргументы прогона.</param>
    /// <param name="context">Состояние прогона, доступное восстановлению.</param>
    /// <param name="cancellationToken">Запрос отмены прогона.</param>
    /// <returns>Исход сценария без восстановления и аудита.</returns>
    private async Task<AcceptanceResult> RunScenarioAsync(
        AcceptanceOptions options,
        AcceptanceContext context,
        CancellationToken cancellationToken)
    {
        if (await PrepareRunAsync(options.InstanceId, context, cancellationToken).ConfigureAwait(false)
            is AcceptanceResult preparationFailure)
        {
            return preparationFailure;
        }

        MuMuInstallation installation = context.Installation!;
        MuMuInstance instance = context.Instance!;
        int cycles = options.Cycles!.Value;
        ScenarioPlan plan = Plan(options.Scenario);
        context.Summary = new ScenarioSummary();

        // Шаг 4: начальное состояние до первой mutation прогона.
        ApplicationResult<MuMuInstanceState> initialObservation =
            _host.ObserveInstanceState(installation, instance.Id);
        if (initialObservation.IsFailure)
        {
            return Fail(StepInitialState, "Начальное состояние", initialObservation.FailureInfo!);
        }

        MuMuLifecycleState initial = initialObservation.Value!.State;
        if (initial == MuMuLifecycleState.Unknown)
        {
            return Fail(
                StepInitialState,
                "Начальное состояние",
                "acceptance_state_not_proven",
                "Состояние экземпляра не доказано, поэтому сценарий не определён; evidence: "
                + initialObservation.Value!.Evidence);
        }

        context.InitialState = initial;
        InstanceSnapshot initialSnapshot =
            await DescribeInstanceStateAsync(installation, instance.Id).ConfigureAwait(false);
        context.InitialSnapshot = initialSnapshot;
        _report.Record(
            StepInitialState,
            "Начальное состояние",
            true,
            Name(initial) + "; снимок до прогона: " + initialSnapshot.Describe());

        // Шаг 5: сценарий и явное число повторений.
        if (plan.Preparation == ScenarioPreparation.RequireExisting && initial != plan.RequiredState)
        {
            return Fail(
                StepCurrentStatus,
                "Сценарий и число повторений",
                ScenarioPreconditionNotProven,
                "Сценарий " + AcceptanceScenarioNames.Value(options.Scenario) + " требует доказанного "
                + Name(plan.RequiredState) + " на входе, а наблюдено " + Name(initial)
                + ": холодный запуск не подменяется остановкой внутри прогона, mutation не выполнялась.");
        }

        _report.Record(
            StepCurrentStatus,
            "Сценарий и число повторений",
            true,
            "сценарий " + AcceptanceScenarioNames.Value(options.Scenario) + " — "
            + AcceptanceScenarioNames.Describe(options.Scenario)
            + "; повторений " + cycles.ToString(CultureInfo.InvariantCulture) + " (задано явно)"
            + "; требуется на входе " + Name(plan.RequiredState)
            + "; проверяемый переход " + Name(plan.Operation) + " → " + Name(plan.TargetState));

        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            AcceptanceResult? failure = await RunScenarioCycleAsync(
                plan, cycle, cycles, installation, instance, context, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }
        }

        // Шаг 8: identity экземпляра не изменилась за все переходы.
        return await VerifyScenarioIdentityAsync(installation, instance, context).ConfigureAwait(false);
    }

    /// <summary>Выполняет один цикл сценария: подготовку состояния и проверяемый переход.</summary>
    /// <param name="plan">План сценария.</param>
    /// <param name="cycle">Номер цикла, начиная с единицы.</param>
    /// <param name="cycles">Общее число циклов сценария.</param>
    /// <param name="installation">Обнаруженная установка.</param>
    /// <param name="instance">Экземпляр, над которым выполняется переход.</param>
    /// <param name="context">Состояние прогона с накопленным итогом.</param>
    /// <param name="cancellationToken">Запрос отмены прогона.</param>
    /// <returns>Отказ цикла или <see langword="null"/>, если цикл доказан.</returns>
    private async Task<AcceptanceResult?> RunScenarioCycleAsync(
        ScenarioPlan plan,
        int cycle,
        int cycles,
        MuMuInstallation installation,
        MuMuInstance instance,
        AcceptanceContext context,
        CancellationToken cancellationToken)
    {
        string label = "Цикл " + cycle.ToString(CultureInfo.InvariantCulture) + "/"
            + cycles.ToString(CultureInfo.InvariantCulture);
        ScenarioSummary summary = context.Summary!;
        summary.Cycles = cycle;

        string preparation = "подготовка не потребовалась";
        int preparationLaunches = 0;
        int preparationWarnings = 0;

        ApplicationResult<MuMuInstanceState> observed = _host.ObserveInstanceState(installation, instance.Id);
        if (observed.IsFailure)
        {
            return Fail(StepMutation, label + ": наблюдение перед циклом", observed.FailureInfo!);
        }

        bool needsPreparation = plan.Preparation == ScenarioPreparation.Always
            || observed.Value!.State != plan.RequiredState;
        if (needsPreparation)
        {
            MuMuLifecycleOperation preparationOperation = plan.RequiredState == MuMuLifecycleState.Stopped
                ? MuMuLifecycleOperation.Stop
                : MuMuLifecycleOperation.Start;

            int launchesBefore = _audit.CountControlOperation(MuMuManagerCommandBuilder.LaunchOperation);
            AcceptanceLogDiagnostics diagnosticsBefore = StderrLoggerProvider.DiagnosticsSnapshot();

            ApplicationResult<MuMuLifecycleOutcome> prepared = await RunOperationAsync(
                installation, instance, preparationOperation, cancellationToken).ConfigureAwait(false);

            preparationLaunches =
                _audit.CountControlOperation(MuMuManagerCommandBuilder.LaunchOperation) - launchesBefore;
            preparationWarnings =
                StderrLoggerProvider.DiagnosticsSnapshot().Warnings - diagnosticsBefore.Warnings;

            if (prepared.IsFailure)
            {
                return Fail(
                    StepMutation,
                    label + ": подготовка " + Name(preparationOperation),
                    prepared.FailureInfo!);
            }

            ApplicationResult<MuMuInstanceState> confirmedPreparation =
                _host.ObserveInstanceState(installation, instance.Id);
            if (confirmedPreparation.IsFailure
                || confirmedPreparation.Value!.State != plan.RequiredState)
            {
                string detail = confirmedPreparation.IsFailure
                    ? "наблюдение отказало: " + confirmedPreparation.FailureInfo!.Code
                    : "наблюдено " + Name(confirmedPreparation.Value!.State);
                return Fail(
                    StepPostcondition,
                    label + ": подготовка",
                    ScenarioPreconditionNotProven,
                    "Подготовка состояния сценария не подтверждена: " + detail
                    + " вместо " + Name(plan.RequiredState) + ".");
            }

            preparation = "подготовка: " + Name(preparationOperation) + " "
                + Milliseconds(prepared.Value!.Elapsed) + " мс";
        }

        int operationLaunchesBefore = _audit.CountControlOperation(MuMuManagerCommandBuilder.LaunchOperation);
        AcceptanceLogDiagnostics operationDiagnostics = StderrLoggerProvider.DiagnosticsSnapshot();

        ApplicationResult<MuMuLifecycleOutcome> outcome = await RunOperationAsync(
            installation, instance, plan.Operation, cancellationToken).ConfigureAwait(false);

        int launches =
            _audit.CountControlOperation(MuMuManagerCommandBuilder.LaunchOperation) - operationLaunchesBefore;
        int warnings =
            StderrLoggerProvider.DiagnosticsSnapshot().Warnings - operationDiagnostics.Warnings;
        IReadOnlyList<string> messages =
            StderrLoggerProvider.DiagnosticMessagesAfter(operationDiagnostics.Sequence);

        int cycleLaunches = preparationLaunches + launches;
        bool retried = preparationLaunches > 1 || launches > 1;
        summary.LaunchCommands += cycleLaunches;
        summary.ProductionWarnings += preparationWarnings + warnings;
        if (retried)
        {
            summary.RetriedCycles++;
        }

        if (preparationLaunches > 2 || launches > 2)
        {
            return Fail(
                StepPostcondition,
                label + ": postcondition",
                "acceptance_retry_not_single",
                "Одна фаза перехода отправила больше двух команд launch (подготовка: "
                + preparationLaunches.ToString(CultureInfo.InvariantCulture) + ", переход: "
                + launches.ToString(CultureInfo.InvariantCulture)
                + "): production-контракт допускает ровно один повтор.");
        }

        if (outcome.IsFailure)
        {
            return Fail(StepMutation, label + ": " + Name(plan.Operation), outcome.FailureInfo!);
        }

        MuMuLifecycleOutcome lifecycle = outcome.Value!;
        _report.Record(
            StepMutation,
            label + ": " + Name(plan.Operation),
            true,
            preparation + "; " + Name(lifecycle.InitialState) + " → " + Name(lifecycle.FinalState)
            + "; " + Milliseconds(lifecycle.Elapsed) + " мс"
            + "; launch-команд " + cycleLaunches.ToString(CultureInfo.InvariantCulture)
            + "; повтор launch: " + (retried ? "да" : "нет")
            + "; предупреждений production "
            + (preparationWarnings + warnings).ToString(CultureInfo.InvariantCulture));

        if (lifecycle.Operation != plan.Operation || lifecycle.FinalState != plan.TargetState)
        {
            return Fail(
                StepPostcondition,
                label + ": postcondition",
                "acceptance_postcondition_not_met",
                "Оркестрация доказала " + Name(lifecycle.FinalState)
                + " вместо " + Name(plan.TargetState) + ".");
        }

        if (launches == 0)
        {
            // Переход завершился без единой команды launch: значит, проверялся не launch-путь, и выдавать
            // такой цикл за доказательство гонки нельзя.
            return Fail(
                StepPostcondition,
                label + ": postcondition",
                "acceptance_scenario_no_launch_sent",
                "Переход " + Name(plan.Operation)
                + " завершился без единой команды launch: launch-путь не проверялся.");
        }

        ApplicationResult<MuMuInstanceState> confirmed = _host.ObserveInstanceState(installation, instance.Id);
        if (confirmed.IsFailure || confirmed.Value!.State != plan.TargetState)
        {
            string detail = confirmed.IsFailure
                ? "наблюдение отказало: " + confirmed.FailureInfo!.Code
                : "наблюдено " + Name(confirmed.Value!.State);
            return Fail(
                StepPostcondition,
                label + ": postcondition",
                "acceptance_postcondition_not_met",
                "Независимое наблюдение показало " + detail
                + " вместо " + Name(plan.TargetState) + ".");
        }

        _report.Record(
            StepPostcondition,
            label + ": postcondition",
            true,
            "независимое наблюдение подтвердило " + Name(plan.TargetState)
            + "; evidence: " + lifecycle.Evidence
            + (messages.Count == 0
                ? string.Empty
                : "; диагностика production: " + string.Join(" | ", messages)));

        summary.Successful++;
        return null;
    }

    /// <summary>Проверяет, что переходы сценария не изменили identity экземпляра.</summary>
    /// <param name="installation">Обнаруженная установка.</param>
    /// <param name="instance">Экземпляр, над которым выполнялись переходы.</param>
    /// <param name="context">Состояние прогона со снимком до переходов.</param>
    /// <returns>Исход проверки identity.</returns>
    private async Task<AcceptanceResult> VerifyScenarioIdentityAsync(
        MuMuInstallation installation,
        MuMuInstance instance,
        AcceptanceContext context)
    {
        ApplicationResult<IReadOnlyList<MuMuInstance>> reEnumerated = _host.EnumerateInstances(installation);
        if (reEnumerated.IsFailure)
        {
            return Fail(StepRestartSemantics, "Identity экземпляра", reEnumerated.FailureInfo!);
        }

        bool present = false;
        foreach (MuMuInstance candidate in reEnumerated.Value!)
        {
            if (candidate.Id == instance.Id)
            {
                present = true;
            }
        }

        if (!present)
        {
            return Fail(
                StepRestartSemantics,
                "Identity экземпляра",
                "acceptance_identity_changed",
                "После переходов запрошенная identity не найдена в перечислении экземпляров.");
        }

        InstanceSnapshot final = await DescribeInstanceStateAsync(installation, instance.Id).ConfigureAwait(false);
        if (context.InitialSnapshot is InstanceSnapshot before)
        {
            if (!string.Equals(before.CreatedTimestamp, final.CreatedTimestamp, StringComparison.Ordinal))
            {
                return Fail(
                    StepRestartSemantics,
                    "Identity экземпляра",
                    "acceptance_identity_changed",
                    "Метка создания экземпляра изменилась: created_timestamp "
                    + before.CreatedTimestamp + " → " + final.CreatedTimestamp + ".");
            }

            if (!string.Equals(before.DisplayName, final.DisplayName, StringComparison.Ordinal))
            {
                return Fail(
                    StepRestartSemantics,
                    "Identity экземпляра",
                    "acceptance_identity_changed",
                    "Имя экземпляра изменилось: «" + before.DisplayName + "» → «" + final.DisplayName + "».");
            }
        }

        _report.Record(
            StepRestartSemantics,
            "Identity экземпляра",
            true,
            "identity " + instance.Id.ToString() + " осталась в перечислении, created_timestamp "
            + final.CreatedTimestamp + " и имя «" + final.DisplayName + "» сохранены; снимок после циклов: "
            + final.Describe());

        return AcceptanceResult.Proven();
    }

    /// <summary>Проверяет семантику перезапуска: композиция mutation, смена процесса и стабильная identity.</summary>
    /// <param name="requested">Exact instance приёмки.</param>
    /// <param name="installation">Обнаруженная установка.</param>
    /// <param name="client">Клиент control surface.</param>
    /// <param name="before">Сведения об экземпляре перед перезапуском.</param>
    /// <param name="cancellationToken">Запрос отмены матрицы.</param>
    /// <returns>Исход проверки семантики restart.</returns>
    private async Task<AcceptanceResult> VerifyRestartSemanticsAsync(
        MuMuInstanceId requested,
        MuMuInstallation installation,
        MuMuManagerClient client,
        MuMuInstanceInfo? before,
        CancellationToken cancellationToken)
    {
        if (before is null)
        {
            return Fail(
                StepRestartSemantics,
                "Семантика restart",
                "acceptance_restart_not_proven",
                "Сведения об экземпляре перед перезапуском не получены.");
        }

        ApplicationResult<MuMuInstanceInfo?> afterQuery = await QueryInstanceInfoAsync(
            client, requested, cancellationToken).ConfigureAwait(false);
        if (afterQuery.IsFailure)
        {
            return Fail(StepRestartSemantics, "Семантика restart", afterQuery.FailureInfo!);
        }

        MuMuInstanceInfo? after = afterQuery.Value;
        if (after is null)
        {
            return Fail(
                StepRestartSemantics,
                "Семантика restart",
                "acceptance_restart_not_proven",
                "Провайдер не сообщил сведения об экземпляре после перезапуска.");
        }

        if (after.State != MuMuLifecycleState.Running)
        {
            return Fail(
                StepRestartSemantics,
                "Семантика restart",
                "acceptance_restart_not_proven",
                "После перезапуска состояние " + Name(after.State) + " вместо " + Name(MuMuLifecycleState.Running) + ".");
        }

        if (before.CreatedTimestamp is long createdBefore
            && after.CreatedTimestamp is long createdAfter
            && createdBefore != createdAfter)
        {
            return Fail(
                StepRestartSemantics,
                "Семантика restart",
                "acceptance_restart_not_proven",
                "Метка создания экземпляра изменилась: перезапуск пересоздал экземпляр вместо перехода.");
        }

        if (before.ProcessId is not int processBefore || after.ProcessId is not int processAfter)
        {
            return Fail(
                StepRestartSemantics,
                "Семантика restart",
                "acceptance_restart_not_proven",
                "Провайдер не сообщил идентификатор процесса, поэтому смена процесса не доказана.");
        }

        if (processBefore == processAfter)
        {
            return Fail(
                StepRestartSemantics,
                "Семантика restart",
                "acceptance_restart_not_proven",
                "Идентификатор процесса не изменился: перезапуск не подтверждён наблюдением.");
        }

        // Identity обязана остаться в перечислении: перезапуск не создаёт новый экземпляр.
        ApplicationResult<IReadOnlyList<MuMuInstance>> reEnumerated = _host.EnumerateInstances(installation);
        if (reEnumerated.IsFailure)
        {
            return Fail(StepRestartSemantics, "Семантика restart", reEnumerated.FailureInfo!);
        }

        bool identityPresent = false;
        foreach (MuMuInstance candidate in reEnumerated.Value!)
        {
            if (candidate.Id == requested)
            {
                identityPresent = true;
            }
        }

        if (!identityPresent)
        {
            return Fail(
                StepRestartSemantics,
                "Семантика restart",
                "acceptance_restart_not_proven",
                "После перезапуска запрошенная identity не найдена в перечислении экземпляров.");
        }

        _report.Record(
            StepRestartSemantics,
            "Семантика restart",
            true,
            "процесс экземпляра сменился, метка создания сохранена, identity "
            + requested.ToString() + " осталась в перечислении; состояние "
            + Name(after.State) + "; evidence: " + (after.RawPlayerState ?? StateNotProvenWord));

        return AcceptanceResult.Proven();
    }

    /// <summary>Выполняет одну lifecycle-операцию production-оркестрацией.</summary>
    /// <param name="installation">Обнаруженная установка.</param>
    /// <param name="instance">Экземпляр, над которым выполняется операция.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Итог операции либо ожидаемый отказ.</returns>
    private Task<ApplicationResult<MuMuLifecycleOutcome>> RunOperationAsync(
        MuMuInstallation installation,
        MuMuInstance instance,
        MuMuLifecycleOperation operation,
        CancellationToken cancellationToken)
        => operation switch
        {
            MuMuLifecycleOperation.Start => _lifecycle.StartAsync(installation, instance, cancellationToken),
            MuMuLifecycleOperation.Stop => _lifecycle.StopAsync(installation, instance, cancellationToken),
            MuMuLifecycleOperation.Restart => _lifecycle.RestartAsync(installation, instance, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Lifecycle-операция MuMu не определена."),
        };

    /// <summary>Запрашивает сведения об экземпляре у control surface обнаруженной установки.</summary>
    /// <param name="client">Клиент control surface.</param>
    /// <param name="requested">Identity экземпляра.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Сведения об экземпляре, <see langword="null"/> при доказанном отсутствии либо отказ.</returns>
    private static async Task<ApplicationResult<MuMuInstanceInfo?>> QueryInstanceInfoAsync(
        MuMuManagerClient client,
        MuMuInstanceId requested,
        CancellationToken cancellationToken)
    {
        ApplicationResult<MuMuInstanceQueryResult> query =
            await client.QueryInstanceAsync(requested, cancellationToken).ConfigureAwait(false);
        if (query.IsFailure)
        {
            return ApplicationResult<MuMuInstanceInfo?>.Failure(query.FailureInfo!);
        }

        return ApplicationResult<MuMuInstanceInfo?>.Success(query.Value!.Instance);
    }

    /// <summary>Восстанавливает начальное состояние экземпляра независимо от исхода матрицы.</summary>
    /// <remarks>
    /// <para>
    /// Восстановление выполняется без отмены: прерывание приёмки не должно оставлять экземпляр в
    /// состоянии, которого оператор не выбирал. Ограничивает восстановление deadline самой
    /// lifecycle-операции, а не отдельный таймаут инструмента: собственного владельца времени у
    /// восстановления нет.
    /// </para>
    /// <para>
    /// Одной mutation в сторону начального состояния недостаточно: экземпляр может остаться в подвисшем
    /// (недоказанном) состоянии, из которого запуск не подтверждается. Поэтому, если наблюдённое
    /// состояние не равно начальному, выполняется очищающая композиция из тех же production-примитивов:
    /// при начальном <see cref="MuMuLifecycleState.Running"/> — остановка, подтверждённая остановка,
    /// запуск, подтверждённый запуск; при начальном <see cref="MuMuLifecycleState.Stopped"/> —
    /// остановка, подтверждённая остановка. Остановка первой сбрасывает подвисшее состояние, а не
    /// добавляет лишний переход: на уже остановленном экземпляре она не меняет состояние.
    /// </para>
    /// <para>
    /// Каждый шаг композиции — одна mutation через <see cref="MuMuLifecycleService"/> и одно независимое
    /// наблюдение, подтверждающее целевое состояние; запись идёт в тот же шаг отчёта, что и раньше, без
    /// нового шага. Если состояние не подтверждено и после композиции, приёмка сообщает отказ
    /// <c>acceptance_restoration_not_proven</c>, ненулевой код выхода и описывает фактически оставленное
    /// на машине состояние.
    /// </para>
    /// </remarks>
    /// <param name="context">Состояние прогона.</param>
    /// <returns>Исход восстановления начального состояния.</returns>
    private async Task<AcceptanceResult> RestoreInitialStateAsync(AcceptanceContext context)
    {
        const string StepName = "Восстановление начального состояния";

        if (context.Installation is not MuMuInstallation installation
            || context.Instance is not MuMuInstance instance
            || context.InitialState is not MuMuLifecycleState initial)
        {
            _report.Record(
                StepRestoration,
                StepName,
                true,
                "не требуется: mutation не выполнялась, отказ произошёл до матрицы");
            return AcceptanceResult.Proven();
        }

        ApplicationResult<MuMuInstanceState> observation =
            _host.ObserveInstanceState(installation, instance.Id);

        string reason;
        if (observation.IsFailure)
        {
            reason = "наблюдение состояния отказало (" + observation.FailureInfo!.Code
                + "), восстановление запрошено без подтверждённого состояния";
        }
        else if (observation.Value!.State == initial)
        {
            _report.Record(
                StepRestoration,
                StepName,
                true,
                Name(initial) + ": состояние уже соответствует начальному, mutation не потребовалась");
            return AcceptanceResult.Proven();
        }
        else
        {
            reason = "наблюдённое состояние " + Name(observation.Value!.State)
                + " отличается от начального " + Name(initial);
        }

        // Очищающая композиция: у каждого шага ровно одна mutation и одно подтверждающее наблюдение.
        MuMuLifecycleOperation[] composition = initial == MuMuLifecycleState.Running
            ? [MuMuLifecycleOperation.Stop, MuMuLifecycleOperation.Start]
            : [MuMuLifecycleOperation.Stop];

        foreach (MuMuLifecycleOperation step in composition)
        {
            AcceptanceResult confirmed = await ConfirmLifecycleStepAsync(
                installation, instance, step, ExpectedState(step)).ConfigureAwait(false);
            if (!confirmed.IsProven)
            {
                return await FailRestorationAsync(
                    installation,
                    instance.Id,
                    reason + "; " + confirmed.FailureMessage).ConfigureAwait(false);
            }
        }

        // Композиция довела экземпляр до целевого состояния своих шагов; итог обязан совпасть с
        // начальным состоянием, иначе восстановление не доказано.
        ApplicationResult<MuMuInstanceState> final = _host.ObserveInstanceState(installation, instance.Id);
        if (final.IsFailure || final.Value!.State != initial)
        {
            string detail = final.IsFailure
                ? "итоговое наблюдение отказало: " + final.FailureInfo!.Code
                : "после композиции наблюдено " + Name(final.Value!.State) + " вместо " + Name(initial);
            return await FailRestorationAsync(
                installation,
                instance.Id,
                reason + "; " + detail).ConfigureAwait(false);
        }

        _report.Record(
            StepRestoration,
            StepName,
            true,
            reason + "; очищающая композиция довела экземпляр до " + Name(initial)
            + " и состояние подтверждено независимым наблюдением");

        return AcceptanceResult.Proven();
    }

    /// <summary>Выполняет один шаг восстановления: одну mutation и подтверждение целевого состояния.</summary>
    /// <param name="installation">Обнаруженная установка.</param>
    /// <param name="instance">Экземпляр, состояние которого восстанавливается.</param>
    /// <param name="operation">Lifecycle-операция шага.</param>
    /// <param name="expected">Состояние, которое шаг обязан доказать.</param>
    /// <returns>Исход шага: подтверждённое состояние или причина неподтверждения.</returns>
    private async Task<AcceptanceResult> ConfirmLifecycleStepAsync(
        MuMuInstallation installation,
        MuMuInstance instance,
        MuMuLifecycleOperation operation,
        MuMuLifecycleState expected)
    {
        ApplicationResult<MuMuLifecycleOutcome> outcome = await RunOperationAsync(
            installation, instance, operation, CancellationToken.None).ConfigureAwait(false);
        if (outcome.IsFailure)
        {
            return AcceptanceResult.NotProven(
                RestorationNotProven,
                "шаг " + Name(operation) + " отказал: " + outcome.FailureInfo!.Code + ": "
                + outcome.FailureInfo!.Message);
        }

        ApplicationResult<MuMuInstanceState> observed = _host.ObserveInstanceState(installation, instance.Id);
        if (observed.IsFailure)
        {
            return AcceptanceResult.NotProven(
                RestorationNotProven,
                "наблюдение после шага " + Name(operation) + " отказало: " + observed.FailureInfo!.Code);
        }

        if (observed.Value!.State != expected)
        {
            return AcceptanceResult.NotProven(
                RestorationNotProven,
                "после шага " + Name(operation) + " наблюдено " + Name(observed.Value!.State)
                + " вместо " + Name(expected));
        }

        return AcceptanceResult.Proven();
    }

    /// <summary>Снимок фактического состояния экземпляра: host-side состояние и provider-поля.</summary>
    /// <remarks>
    /// Снимок нужен приёмке в двух ролях: как evidence оставленного на машине состояния при отказе
    /// восстановления и как «до/после» для проверки, что identity и здоровье экземпляра не изменились.
    /// </remarks>
    /// <param name="State">Доказанное host-side состояние.</param>
    /// <param name="Evidence">Bounded evidence наблюдения production-поверхности.</param>
    /// <param name="DisplayName">Имя экземпляра из ответа control surface.</param>
    /// <param name="ProcessId">Идентификатор процесса из ответа control surface.</param>
    /// <param name="CreatedTimestamp">Метка создания экземпляра из ответа control surface.</param>
    /// <param name="LaunchErrorCode">Код ошибки запуска из ответа control surface.</param>
    private sealed record InstanceSnapshot(
        MuMuLifecycleState State,
        string Evidence,
        string DisplayName,
        string ProcessId,
        string CreatedTimestamp,
        string LaunchErrorCode)
    {
        /// <summary>Описывает снимок одной bounded строкой для отчёта.</summary>
        /// <returns>Описание состояния, имени, pid, метки создания и кода ошибки запуска.</returns>
        internal string Describe()
            => Name(State) + " [" + Evidence + "]"
                + "; имя «" + DisplayName + "»"
                + "; pid " + ProcessId
                + "; created_timestamp " + CreatedTimestamp
                + "; launch_err_code " + LaunchErrorCode;

        /// <summary>Проверяет, что провайдер не сообщил об ошибке запуска.</summary>
        /// <returns><see langword="true"/>, если код ошибки отсутствует или равен нулю.</returns>
        internal bool HasNoLaunchError()
            => string.Equals(LaunchErrorCode, MissingField, StringComparison.Ordinal)
                || string.Equals(LaunchErrorCode, ZeroCode, StringComparison.Ordinal);
    }

    /// <summary>Сообщает, что начальное состояние не восстановлено, и описывает оставленное состояние.</summary>
    /// <remarks>
    /// Фактически оставленное на машине состояние записывается отдельной строкой того же шага отчёта:
    /// причина отказа и provider-поля не должны вытеснять друг друга в bounded-строке.
    /// </remarks>
    /// <param name="installation">Обнаруженная установка.</param>
    /// <param name="id">Identity экземпляра.</param>
    /// <param name="message">Причина отказа восстановления.</param>
    /// <returns>Исход приёмки с отказом восстановления.</returns>
    private async Task<AcceptanceResult> FailRestorationAsync(
        MuMuInstallation installation,
        MuMuInstanceId id,
        string message)
    {
        InstanceSnapshot leftState = await DescribeInstanceStateAsync(installation, id).ConfigureAwait(false);
        _report.Record(
            StepRestoration,
            "Фактически оставленное на машине состояние",
            false,
            "оставлено на машине: " + leftState.Describe());

        return Fail(
            StepRestoration,
            "Восстановление начального состояния",
            RestorationNotProven,
            message);
    }

    /// <summary>Описывает фактическое состояние экземпляра для отчёта.</summary>
    /// <remarks>
    /// <para>
    /// Состояние наблюдения и bounded evidence (в том числе <c>player_state</c> и оба признака запуска)
    /// приходят от production-поверхности MuMu.
    /// </para>
    /// <para>
    /// <c>pid</c>, <c>created_timestamp</c>, <c>name</c> и <c>launch_err_code</c> читаются из ответа
    /// control surface узко, потому что production-проекция сообщает <c>launch_err_code</c> только
    /// признаком «провайдер сообщил об ошибке», а приёмке нужны сами значения: код ошибки отличает
    /// деградированный экземпляр от здорового, а метка создания и имя — identity экземпляра. Форма
    /// команды, дедлайн и имена полей при этом берутся у своих владельцев
    /// (<see cref="MuMuManagerCommandBuilder"/>, <see cref="MuMuManagerClient.DefaultCommandTimeout"/>,
    /// <see cref="MuMuManagerJsonNames"/>), а не повторяются здесь.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка.</param>
    /// <param name="id">Identity экземпляра.</param>
    /// <returns>Bounded снимок состояния экземпляра.</returns>
    private async Task<InstanceSnapshot> DescribeInstanceStateAsync(
        MuMuInstallation installation,
        MuMuInstanceId id)
    {
        ApplicationResult<MuMuInstanceState> observed = _host.ObserveInstanceState(installation, id);
        string evidence = observed.IsSuccess
            ? observed.Value!.Evidence
            : "наблюдение отказало (" + observed.FailureInfo!.Code + ")";
        MuMuLifecycleState state = observed.IsSuccess
            ? observed.Value!.State
            : MuMuLifecycleState.Unknown;

        ApplicationResult<MuMuProcessOutcome> document = await _processRunner.RunAsync(
            new MuMuProcessRequest
            {
                ExecutablePath = installation.ControlExecutablePath,
                Arguments = MuMuManagerCommandBuilder.BuildInstanceInfoArguments(id),
                Timeout = MuMuManagerClient.DefaultCommandTimeout,
            },
            CancellationToken.None).ConfigureAwait(false);

        if (document.IsFailure)
        {
            return new InstanceSnapshot(
                state,
                evidence + "; provider-поля недоступны (" + document.FailureInfo!.Code + ")",
                MissingField,
                MissingField,
                MissingField,
                MissingField);
        }

        using JsonDocument? parsed = TryParseDocument(document.Value!.StandardOutput);
        if (parsed is null)
        {
            return new InstanceSnapshot(
                state,
                evidence + "; provider-ответ не разобран как JSON",
                MissingField,
                MissingField,
                MissingField,
                MissingField);
        }

        return new InstanceSnapshot(
            state,
            evidence,
            ReadProviderField(parsed.RootElement, MuMuManagerJsonNames.Name),
            ReadProviderField(parsed.RootElement, MuMuManagerJsonNames.ProcessId),
            ReadProviderField(parsed.RootElement, MuMuManagerJsonNames.CreatedTimestamp),
            ReadProviderField(parsed.RootElement, MuMuManagerJsonNames.LaunchErrorCode));
    }

    /// <summary>Разбирает ответ control surface как JSON-документ.</summary>
    /// <param name="text">Захваченный stdout control surface.</param>
    /// <returns>Документ или <see langword="null"/>, если ответ не является JSON-документом.</returns>
    private static JsonDocument? TryParseDocument(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Читает поле ответа control surface как текст.</summary>
    /// <param name="document">Разобранный ответ control surface.</param>
    /// <param name="propertyName">Имя поля у его владельца.</param>
    /// <returns>Значение поля или <c>нет</c>, если поля в ответе нет.</returns>
    private static string ReadProviderField(JsonElement document, string propertyName)
        => document.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? property.ToString()
                : MissingField;

    /// <summary>Фиксирует состояние экземпляра после прогона и проверяет его здоровье.</summary>
    /// <remarks>
    /// Проверка нужна, чтобы «состояние восстановлено» было фактом наблюдения, а не выводом из
    /// доказанного postcondition: снимок берётся после восстановления и содержит provider-поля, включая
    /// код ошибки запуска. Ненулевой код ошибки означает, что экземпляр оставлен деградированным, и
    /// приёмка не может считаться доказанной, даже если все переходы прошли.
    /// </remarks>
    /// <param name="context">Состояние прогона.</param>
    /// <returns>Исход проверки состояния после прогона.</returns>
    private async Task<AcceptanceResult> VerifyFinalStateAsync(AcceptanceContext context)
    {
        if (context.Installation is not MuMuInstallation installation
            || context.Instance is not MuMuInstance instance)
        {
            _report.Record(
                StepRestoration,
                "Состояние после прогона",
                true,
                "не проверялось: установка или экземпляр не обнаружены");
            return AcceptanceResult.Proven();
        }

        InstanceSnapshot final = await DescribeInstanceStateAsync(installation, instance.Id).ConfigureAwait(false);
        _report.Record(
            StepRestoration,
            "Состояние после прогона",
            true,
            "снимок после прогона: " + final.Describe());

        if (!final.HasNoLaunchError())
        {
            return Fail(
                StepRestoration,
                "Состояние после прогона",
                "acceptance_state_degraded",
                "Провайдер сообщил код ошибки запуска " + final.LaunchErrorCode
                + ": экземпляр оставлен деградированным.");
        }

        return AcceptanceResult.Proven();
    }

    /// <summary>Записывает итог сценария: число циклов, успешных и потребовавших повтора launch.</summary>
    /// <remarks>
    /// Итог печатается и тогда, когда часть циклов не доказана: число выполненных циклов, число
    /// доказанных и число повторов — факты прогона, а не вывод из его успеха.
    /// </remarks>
    /// <param name="context">Состояние прогона с накопленным итогом.</param>
    private void RecordScenarioSummary(AcceptanceContext context)
    {
        if (context.Summary is not ScenarioSummary summary)
        {
            return;
        }

        string retries = summary.RetriedCycles == 0
            ? "повтор launch не потребовался ни в одном из "
                + summary.Cycles.ToString(CultureInfo.InvariantCulture) + " циклов"
            : "повтор launch задействован в "
                + summary.RetriedCycles.ToString(CultureInfo.InvariantCulture) + " циклах";

        _report.Record(
            StepEvidence,
            "Итог сценария",
            summary.Successful == summary.Cycles,
            "циклов " + summary.Cycles.ToString(CultureInfo.InvariantCulture)
            + ", успешных " + summary.Successful.ToString(CultureInfo.InvariantCulture)
            + ", с повтором launch " + summary.RetriedCycles.ToString(CultureInfo.InvariantCulture)
            + "; launch-команд всего " + summary.LaunchCommands.ToString(CultureInfo.InvariantCulture)
            + "; предупреждений production "
            + summary.ProductionWarnings.ToString(CultureInfo.InvariantCulture)
            + " — " + retries);
    }

    /// <summary>Проверяет по аудиту, что приёмка не выполняла запрещённых действий.</summary>
    /// <param name="requested">Exact instance приёмки.</param>
    /// <param name="context">Состояние прогона.</param>
    /// <returns>Исход проверки запретов.</returns>
    private AcceptanceResult VerifyCommandAudit(MuMuInstanceId requested, AcceptanceContext context)
    {
        string summary = _audit.Describe();

        if (context.Installation is not MuMuInstallation installation)
        {
            _report.Record(
                StepEvidence,
                "Аудит выполненных команд",
                true,
                summary + "; control surface не обнаружена, запусков установки не было");
            return AcceptanceResult.Proven();
        }

        string? violation = _audit.FindViolation(installation.ControlExecutablePath, requested);
        if (violation is not null)
        {
            return Fail(
                StepEvidence,
                "Аудит выполненных команд",
                "acceptance_forbidden_command",
                violation + "; " + summary);
        }

        _report.Record(
            StepEvidence,
            "Аудит выполненных команд",
            true,
            summary + "; запускалась только control surface обнаруженной установки и только команды "
            + "production-контракта: запрещённые действия не обнаружены");

        _report.Record(
            StepEvidence,
            "Bounded sanitized evidence",
            true,
            "отчёт в stdout, structured log в stderr (записей: "
            + StderrLoggerProvider.WrittenRecordCount.ToString(CultureInfo.InvariantCulture)
            + "); bounded текст ограничен " + MuMuBoundedText.MaxLength.ToString(CultureInfo.InvariantCulture)
            + " символами; каталог установки заменён на " + AcceptanceSanitizer.InstallRootPlaceholder);

        return AcceptanceResult.Proven();
    }

    /// <summary>Объединяет исходы частей прогона в один исход приёмки.</summary>
    /// <param name="primary">Исход матрицы; отсутствует, если матрица не выполнялась.</param>
    /// <param name="additional">Исходы восстановления и аудита.</param>
    /// <returns>Исход приёмки: доказан только при доказанности всех частей.</returns>
    private static AcceptanceResult Combine(
        AcceptanceResult? primary,
        params AcceptanceResult[] additional)
    {
        List<AcceptanceResult> results = [];
        if (primary is not null)
        {
            results.Add(primary);
        }

        results.AddRange(additional);

        List<string> failures = [];
        foreach (AcceptanceResult result in results)
        {
            if (!result.IsProven)
            {
                failures.Add(result.FailureMessage ?? string.Empty);
            }
        }

        if (failures.Count == 0)
        {
            return AcceptanceResult.Proven();
        }

        AcceptanceResult firstFailure = results.First(result => !result.IsProven);
        return AcceptanceResult.NotProven(firstFailure.FailureCode!, string.Join(" ", failures));
    }

    /// <summary>Записывает отказ шага и возвращает исход приёмки.</summary>
    /// <param name="number">Номер шага контракта.</param>
    /// <param name="name">Название шага.</param>
    /// <param name="failure">Отказ, полученный от production-кода.</param>
    /// <returns>Исход приёмки с отказом.</returns>
    private AcceptanceResult Fail(int number, string name, ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        string message = failure.Code + ": " + failure.Message;
        _report.Record(number, name, false, "отказ " + message);
        return AcceptanceResult.NotProven(failure.Code, message);
    }

    /// <summary>Записывает локальный отказ приёмки и возвращает исход.</summary>
    /// <param name="number">Номер шага контракта.</param>
    /// <param name="name">Название шага.</param>
    /// <param name="code">Локальный код отказа приёмки.</param>
    /// <param name="message">Описание отказа.</param>
    /// <returns>Исход приёмки с отказом.</returns>
    private AcceptanceResult Fail(int number, string name, string code, string message)
    {
        string description = code + ": " + message;
        _report.Record(number, name, false, "отказ " + description);
        return AcceptanceResult.NotProven(code, description);
    }

    /// <summary>Создаёт локальный отказ приёмки для сообщения, полученного от production-кода.</summary>
    /// <param name="code">Локальный код отказа приёмки.</param>
    /// <param name="message">Описание отказа.</param>
    /// <returns>Отказ приёмки.</returns>
    private static ApplicationFailure LocalFailure(string code, string message)
        => new() { Code = code, Message = message };

    /// <summary>Возвращает русское имя host-side состояния для отчёта.</summary>
    /// <param name="state">Host-side состояние экземпляра.</param>
    /// <returns>Имя состояния для оператора.</returns>
    private static string Name(MuMuLifecycleState state) => state switch
    {
        MuMuLifecycleState.Unknown => StateNotProvenWord,
        MuMuLifecycleState.Stopped => StateStoppedWord,
        MuMuLifecycleState.Running => StateRunningWord,
        _ => StateNotProvenWord,
    };

    /// <summary>Возвращает русское имя lifecycle-операции для отчёта.</summary>
    /// <param name="operation">Lifecycle-операция.</param>
    /// <returns>Имя операции для оператора.</returns>
    private static string Name(MuMuLifecycleOperation operation) => operation switch
    {
        MuMuLifecycleOperation.Start => "запуск",
        MuMuLifecycleOperation.Stop => "остановка",
        MuMuLifecycleOperation.Restart => "перезапуск",
        _ => "неизвестная операция",
    };

    /// <summary>Возвращает состояние, которое обязано быть доказано после операции.</summary>
    /// <param name="operation">Выполненная операция.</param>
    /// <returns>Целевое host-side состояние.</returns>
    private static MuMuLifecycleState ExpectedState(MuMuLifecycleOperation operation)
        => operation == MuMuLifecycleOperation.Stop
            ? MuMuLifecycleState.Stopped
            : MuMuLifecycleState.Running;

    /// <summary>Форматирует длительность операции для отчёта.</summary>
    /// <param name="elapsed">Длительность операции.</param>
    /// <returns>Длительность в миллисекундах как строка инвариантной культуры.</returns>
    private static string Milliseconds(TimeSpan elapsed)
        => ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>Состояние прогона, доступное восстановлению и аудиту после отказа матрицы.</summary>
    private sealed class AcceptanceContext
    {
        /// <summary>Обнаруженная установка.</summary>
        internal MuMuInstallation? Installation { get; set; }

        /// <summary>Разрешённый exact instance.</summary>
        internal MuMuInstance? Instance { get; set; }

        /// <summary>Клиент control surface обнаруженной установки.</summary>
        internal MuMuManagerClient? Client { get; set; }

        /// <summary>Наблюдённое начальное состояние.</summary>
        internal MuMuLifecycleState? InitialState { get; set; }

        /// <summary>Снимок состояния до первой mutation прогона.</summary>
        internal InstanceSnapshot? InitialSnapshot { get; set; }

        /// <summary>Накопленный итог сценария; отсутствует у матрицы.</summary>
        internal ScenarioSummary? Summary { get; set; }
    }

    /// <summary>Как сценарий получает своё начальное состояние.</summary>
    private enum ScenarioPreparation
    {
        /// <summary>Состояние обязано быть таким уже на входе; подготовка запрещена.</summary>
        RequireExisting,

        /// <summary>Состояние доводится до требуемого, если оно иное.</summary>
        Ensure,

        /// <summary>Состояние доводится до требуемого всегда: сценарий проверяет свежую остановку.</summary>
        Always,
    }

    /// <summary>План сценария: требуемое начальное состояние, подготовка и проверяемый переход.</summary>
    /// <param name="RequiredState">Состояние, из которого выполняется переход.</param>
    /// <param name="Preparation">Способ получения требуемого состояния.</param>
    /// <param name="Operation">Lifecycle-операция, доказывающая переход.</param>
    /// <param name="TargetState">Состояние, которое обязано быть доказано после операции.</param>
    private sealed record ScenarioPlan(
        MuMuLifecycleState RequiredState,
        ScenarioPreparation Preparation,
        MuMuLifecycleOperation Operation,
        MuMuLifecycleState TargetState);

    /// <summary>Накопленный итог сценария для отчёта.</summary>
    private sealed class ScenarioSummary
    {
        /// <summary>Число начатых циклов.</summary>
        internal int Cycles { get; set; }

        /// <summary>Число циклов, закончившихся доказанным postcondition.</summary>
        internal int Successful { get; set; }

        /// <summary>Число циклов, в которых launch был повторён production-кодом.</summary>
        internal int RetriedCycles { get; set; }

        /// <summary>Число отправленных команд <c>launch</c> за все циклы.</summary>
        internal int LaunchCommands { get; set; }

        /// <summary>Число диагностических предупреждений production-кода за все циклы.</summary>
        internal int ProductionWarnings { get; set; }
    }
}
