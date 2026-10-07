using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.Logging;
using Xunit;
using static AzurPilot.Tests.MuMu.MuMuLifecycleTestContext;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства единого внутреннего контракта перехода Stopped → Running.
/// </summary>
/// <remarks>
/// <para>
/// Один и тот же сценарий провайдерской гонки прогоняется по всем трём путям запуска: обычный start из
/// Stopped, restart исходно Stopped и restart исходно Running после доказанного stop → Stopped. Если
/// семантика для путей одинакова, одинаковыми должны быть и наблюдаемые факты: число launch, evidence,
/// диагностика и момент отправки повтора.
/// </para>
/// <para>
/// Управляемый host молча не выполняет launch до заданного номера, а управляемый
/// <see cref="MuMuTestTimeProvider"/> делает окно эффекта и deadline детерминированными: фиксированных
/// sleep нет.
/// </para>
/// </remarks>
public sealed class MuMuLifecycleLaunchTransitionTests
{
    [Theory(DisplayName = "Единый переход: один сценарий гонки одинаково работает для всех трёх путей")]
    [InlineData(MuMuLifecycleOperation.Start, MuMuLifecycleState.Stopped)]
    [InlineData(MuMuLifecycleOperation.Restart, MuMuLifecycleState.Stopped)]
    [InlineData(MuMuLifecycleOperation.Restart, MuMuLifecycleState.Running)]
    public async Task SameSemanticsForEveryLaunchPath(
        MuMuLifecycleOperation operation,
        MuMuLifecycleState initialState)
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        LaunchRace race = new(context, initialState);

        ApplicationResult<MuMuLifecycleOutcome> result = await RunOperation(context, operation, instance);

        Assert.True(result.IsSuccess);
        MuMuLifecycleOutcome outcome = result.Value!;
        Assert.Equal(initialState, outcome.InitialState);
        Assert.Equal(MuMuLifecycleState.Running, outcome.FinalState);

        // Первый launch формально принят и молча не сработал, единственный повтор поднял экземпляр.
        Assert.Equal(ExpectedMutations(initialState), Mutations(context));
        Assert.Contains(ExpectedEvidence(initialState), outcome.Evidence, StringComparison.Ordinal);

        // Перед повтором Stopped подтверждён заново отдельным наблюдением: между launch и повтором ровно
        // наблюдения окна эффекта плюс одно подтверждающее наблюдение.
        Assert.Equal(
            EffectWindowObservations(context) + 1,
            race.ObservationsAtMutation[^1] - race.ObservationsAtMutation[^2]);
        Assert.Equal(MuMuLifecycleState.Stopped, race.StatesAtMutation[^1]);

        // Диагностика одинакова для всех трёх путей: mutation без эффекта и факт повтора различимы
        // отдельно от окончательного отказа.
        Assert.Equal(1, context.Logger.Records.Count(record => record.EventId == LaunchEffectMissingEventId));
        Assert.Equal(1, context.Logger.Records.Count(record => record.EventId == LaunchRetriedEventId));
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Error));

        // Окно и повтор уложились в общий deadline операции.
        Assert.True(outcome.Elapsed >= context.Timings.LaunchEffectWindow);
        Assert.True(outcome.Elapsed < context.Timings.DeadlineFor(operation));
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Theory(DisplayName = "Единый переход: сработавший первый launch не порождает дублирующий")]
    [InlineData(MuMuLifecycleOperation.Start, MuMuLifecycleState.Stopped)]
    [InlineData(MuMuLifecycleOperation.Restart, MuMuLifecycleState.Stopped)]
    [InlineData(MuMuLifecycleOperation.Restart, MuMuLifecycleState.Running)]
    public async Task EffectiveFirstLaunchIsNeverDuplicated(
        MuMuLifecycleOperation operation,
        MuMuLifecycleState initialState)
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        LaunchRace race = new(context, initialState)
        {
            // Первый launch действительно начал действовать: провайдер сообщает признак начала запуска
            // (starting_*, который host-side правило отображает в Unknown), а Running приходит позже.
            EffectiveLaunchNumber = 1,
            EffectState = MuMuLifecycleState.Unknown,
            RunningAfterObservations = 3,
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await RunOperation(context, operation, instance);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Running, result.Value!.FinalState);

        // Дублирующего launch нет: признак начала запуска закрывает окно эффекта досрочно.
        MuMuLifecycleMutation[] expected = initialState == MuMuLifecycleState.Running
            ? [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start]
            : [MuMuLifecycleMutation.Start];
        Assert.Equal(expected, Mutations(context));
        Assert.True(
            context.Host.ObservationCount < EffectWindowObservations(context),
            "Окно эффекта должно закончиться на признаке начала запуска, а не на своей границе.");

        Assert.Equal(0, context.Logger.Records.Count(record => record.EventId == LaunchRetriedEventId));
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Warning));
    }

    [Theory(DisplayName = "Единый переход: явный отказ команды launch не даёт повтора ни на одном пути")]
    [InlineData(MuMuLifecycleOperation.Start, MuMuLifecycleState.Stopped)]
    [InlineData(MuMuLifecycleOperation.Restart, MuMuLifecycleState.Stopped)]
    [InlineData(MuMuLifecycleOperation.Restart, MuMuLifecycleState.Running)]
    public async Task ExplicitCommandFailureNeverRetries(
        MuMuLifecycleOperation operation,
        MuMuLifecycleState initialState)
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        LaunchRace race = new(context, initialState)
        {
            // Ни один launch не поднимает экземпляр, а команда сообщает явный отказ своим кодом выхода.
            EffectiveLaunchNumber = null,
            ExitCode = 9,
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await RunOperation(context, operation, instance);

        // Отказ команды не маскируется как «отсутствие эффекта»: он остаётся прежним отказом postcondition.
        Assert.True(result.IsFailure);
        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(ApplicationFailure.MuMuLifecyclePostconditionNotMet, failure.Code);
        Assert.Equal("9", failure.Details!["exit_code"]);
        Assert.Equal("stopped", failure.Details!["state"]);
        Assert.Equal("postcondition_observation", failure.Details!["phase"]);

        // Повтора нет: окно эффекта входит только при формально принятом launch.
        MuMuLifecycleMutation[] expected = initialState == MuMuLifecycleState.Running
            ? [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start]
            : [MuMuLifecycleMutation.Start];
        Assert.Equal(expected, Mutations(context));
        Assert.Equal(0, context.Logger.Records.Count(record => record.EventId == LaunchRetriedEventId));
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Warning));
    }

    [Fact(DisplayName = "Единый переход: если экземпляр уже не Stopped, повтор не отправляется")]
    public async Task NotConfirmedStoppedNeverRetries()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        int windowObservations = EffectWindowObservations(context);
        MuMuLifecycleState current = MuMuLifecycleState.Stopped;
        bool launchSent = false;
        int observationsAfterLaunch = 0;

        context.Host.MutationHandler = _ =>
        {
            launchSent = true;
            observationsAfterLaunch = 0;
            return MuMuLifecycleTestContext.Command(0);
        };
        context.Host.ObservationHandler = _ =>
        {
            if (launchSent)
            {
                observationsAfterLaunch++;
                if (observationsAfterLaunch == windowObservations + 1)
                {
                    // Подтверждающее наблюдение показывает, что экземпляр уже не Stopped: повтор запрещён.
                    current = MuMuLifecycleState.Unknown;
                }
                else if (observationsAfterLaunch > windowObservations + 1)
                {
                    current = MuMuLifecycleState.Running;
                }
            }

            return MuMuLifecycleTestContext.Observed(current);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Running, result.Value!.FinalState);
        Assert.Equal([MuMuLifecycleMutation.Start], Mutations(context));

        // Окно эффекта исчерпано (одно предупреждение), но повтора нет.
        Assert.Equal(1, context.Logger.Records.Count(record => record.EventId == LaunchEffectMissingEventId));
        Assert.Equal(0, context.Logger.Records.Count(record => record.EventId == LaunchRetriedEventId));
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Error));
    }

    [Fact(DisplayName = "Единый переход: второго повтора нет, deadline start не превышен")]
    public async Task NoSecondRetryWithinStartDeadline()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        LaunchRace race = new(context, MuMuLifecycleState.Stopped) { EffectiveLaunchNumber = null };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, failure.Code);

        // Ровно два launch: первый и единственный повтор. Третьего нет, ложного успеха нет.
        Assert.Equal([MuMuLifecycleMutation.Start, MuMuLifecycleMutation.Start], Mutations(context));
        Assert.Equal(MuMuLifecycleState.Stopped, race.StatesAtMutation[^1]);

        // Deadline достигнут и не сброшен повтором: elapsed лежит в пределах одного deadline start.
        long elapsedMilliseconds = long.Parse(
            failure.Details!["elapsed_ms"],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(elapsedMilliseconds >= (long)context.Timings.StartDeadline.TotalMilliseconds);
        Assert.True(
            elapsedMilliseconds
                <= (long)(context.Timings.StartDeadline + context.Timings.PollInterval).TotalMilliseconds);

        // Наблюдений не больше, чем вмещает один deadline вместе с окном эффекта и подтверждением.
        int singleDeadlineObservations = (int)(context.Timings.StartDeadline / context.Timings.PollInterval);
        Assert.True(
            context.Host.ObservationCount <= singleDeadlineObservations + EffectWindowObservations(context) + 2,
            $"Наблюдений {context.Host.ObservationCount}: повтор не должен удваивать бюджет опроса.");

        Assert.Equal(1, context.Logger.Records.Count(record => record.EventId == LaunchEffectMissingEventId));
        Assert.Equal(1, context.Logger.Records.Count(record => record.EventId == LaunchRetriedEventId));
        Assert.Equal(1, context.Logger.Records.Count(record => record.Level == LogLevel.Error));
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Единый переход: признак начала запуска без Running успеха не даёт")]
    public async Task EffectWithoutRunningNeverSucceeds()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        LaunchRace race = new(context, MuMuLifecycleState.Stopped)
        {
            // Первый launch начал действовать, но до Running экземпляр не доходит никогда.
            EffectiveLaunchNumber = 1,
            EffectState = MuMuLifecycleState.Unknown,
            RunningAfterObservations = null,
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, result.FailureInfo!.Code);
        Assert.Equal("unknown", result.FailureInfo!.Details!["state"]);

        // Признак начала запуска есть, поэтому повтора нет, но успех без наблюдённого Running невозможен.
        Assert.Equal([MuMuLifecycleMutation.Start], Mutations(context));
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Warning));
        Assert.Equal(1, context.Logger.Records.Count(record => record.Level == LogLevel.Error));

        // Экземпляр так и не покинул признак начала запуска: Running не наблюдался ни разу.
        Assert.Equal(MuMuLifecycleState.Unknown, race.State);
    }

    [Fact(DisplayName = "Единый переход: отказ команды повтора не маскируется и не даёт третьего launch")]
    public async Task FailedRetryIsNotMasked()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        int launches = 0;

        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ =>
        {
            launches++;
            // Первый launch формально принят и молча не сработал, повтор сообщает явный отказ команды.
            return MuMuLifecycleTestContext.Command(launches == 1 ? 0 : 7);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(ApplicationFailure.MuMuLifecyclePostconditionNotMet, failure.Code);
        Assert.Equal("7", failure.Details!["exit_code"]);
        Assert.Equal("stopped", failure.Details!["state"]);

        // Третьего launch нет: отказ повтора виден честно, а не превращается в ещё одну попытку.
        Assert.Equal([MuMuLifecycleMutation.Start, MuMuLifecycleMutation.Start], Mutations(context));
        Assert.Equal(1, context.Logger.Records.Count(record => record.EventId == LaunchEffectMissingEventId));
        Assert.Equal(1, context.Logger.Records.Count(record => record.EventId == LaunchRetriedEventId));
    }

    /// <summary>Запускает операцию над экземпляром: start или restart.</summary>
    /// <param name="context">Собранный orchestration с управляемым host-ом.</param>
    /// <param name="operation">Запрошенная операция.</param>
    /// <param name="instance">Экземпляр, над которым выполняется операция.</param>
    /// <returns>Итог операции.</returns>
    private static Task<ApplicationResult<MuMuLifecycleOutcome>> RunOperation(
        MuMuLifecycleTestContext context,
        MuMuLifecycleOperation operation,
        MuMuInstance instance)
        => operation == MuMuLifecycleOperation.Start
            ? context.Service.StartAsync(MuMuLifecycleTestContext.Installation, instance, CancellationToken.None)
            : context.Service.RestartAsync(MuMuLifecycleTestContext.Installation, instance, CancellationToken.None);

    /// <summary>Возвращает последовательность выполненных mutation.</summary>
    /// <param name="context">Собранный orchestration с управляемым host-ом.</param>
    /// <returns>Mutation в порядке выполнения.</returns>
    private static MuMuLifecycleMutation[] Mutations(MuMuLifecycleTestContext context)
        => [.. context.Host.MutationRequests.Select(request => request.Mutation)];

    /// <summary>Ожидаемая последовательность mutation для пути запуска.</summary>
    /// <param name="initialState">Начальное состояние экземпляра.</param>
    /// <returns>Mutation в порядке выполнения при сработавшем повторе.</returns>
    private static MuMuLifecycleMutation[] ExpectedMutations(MuMuLifecycleState initialState)
        => initialState == MuMuLifecycleState.Running
            ? [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start, MuMuLifecycleMutation.Start]
            : [MuMuLifecycleMutation.Start, MuMuLifecycleMutation.Start];

    /// <summary>Ожидаемый evidence mutation для пути запуска.</summary>
    /// <param name="initialState">Начальное состояние экземпляра.</param>
    /// <returns>Фрагмент evidence с выполненными mutation.</returns>
    private static string ExpectedEvidence(MuMuLifecycleState initialState)
        => initialState == MuMuLifecycleState.Running
            ? "mutations=stop(exit=0),start(exit=0),start(exit=0)"
            : "mutations=start(exit=0),start(exit=0)";

    /// <summary>
    /// Скриптованный host провайдерской гонки: launch с заданным номером действительно поднимает экземпляр,
    /// все предыдущие молча ничего не делают, поэтому no-op виден как неизменное состояние.
    /// </summary>
    private sealed class LaunchRace
    {
        private int _launches;
        private int _observationsAfterEffectiveLaunch = -1;

        /// <summary>Создаёт сценарий гонки для заданного начального состояния экземпляра.</summary>
        /// <param name="context">Собранный orchestration с управляемым host-ом.</param>
        /// <param name="initialState">Состояние, наблюдаемое до сработавшего launch.</param>
        public LaunchRace(MuMuLifecycleTestContext context, MuMuLifecycleState initialState)
        {
            State = initialState;
            context.Host.ObservationHandler = _ =>
            {
                if (_observationsAfterEffectiveLaunch >= 0)
                {
                    _observationsAfterEffectiveLaunch++;
                    if (RunningAfterObservations is int after && _observationsAfterEffectiveLaunch > after)
                    {
                        State = MuMuLifecycleState.Running;
                    }
                }

                return MuMuLifecycleTestContext.Observed(State);
            };
            context.Host.MutationHandler = request =>
            {
                StatesAtMutation.Add(State);
                ObservationsAtMutation.Add(context.Host.ObservationCount);

                if (request.Mutation == MuMuLifecycleMutation.Stop)
                {
                    State = MuMuLifecycleState.Stopped;
                }
                else
                {
                    _launches++;
                    if (EffectiveLaunchNumber is int effective && _launches >= effective)
                    {
                        // Launch действительно начал действовать: сначала признак начала запуска, затем
                        // доказанный Running.
                        State = EffectState;
                        _observationsAfterEffectiveLaunch = 0;
                    }
                }

                return MuMuLifecycleTestContext.Command(ExitCode);
            };
        }

        /// <summary>Состояние экземпляра, которое наблюдает host.</summary>
        public MuMuLifecycleState State { get; private set; }

        /// <summary>Номер launch, который поднимает экземпляр; <see langword="null"/> — ни один.</summary>
        public int? EffectiveLaunchNumber { get; set; } = 2;

        /// <summary>Состояние сразу после сработавшего launch: <c>starting_*</c> отображается в Unknown.</summary>
        public MuMuLifecycleState EffectState { get; set; } = MuMuLifecycleState.Running;

        /// <summary>
        /// Через сколько наблюдений после сработавшего launch состояние доходит до Running;
        /// <see langword="null"/> — не доходит вовсе.
        /// </summary>
        public int? RunningAfterObservations { get; set; }

        /// <summary>Код выхода, который сообщает каждый launch.</summary>
        public int ExitCode { get; set; }

        /// <summary>Состояние, наблюдённое на момент каждой mutation.</summary>
        public List<MuMuLifecycleState> StatesAtMutation { get; } = [];

        /// <summary>Число наблюдений на момент каждой mutation.</summary>
        public List<int> ObservationsAtMutation { get; } = [];
    }
}
