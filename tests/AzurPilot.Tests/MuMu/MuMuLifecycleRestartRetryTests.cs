using System.Globalization;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.Logging;
using Xunit;
using static AzurPilot.Tests.MuMu.MuMuLifecycleTestContext;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства bounded окна эффекта запуска и единственного повтора launch в едином переходе к
/// <see cref="MuMuLifecycleState.Running"/>.
/// </summary>
/// <remarks>
/// Провайдерская гонка воспроизводится управляемым host-ом: launch с указанным номером молча не меняет
/// состояние, поэтому no-op выглядит как неизменное Stopped, а сработавший launch проявляет признак
/// начала запуска. Время идёт через управляемый <see cref="MuMuTestTimeProvider"/>, поэтому окно эффекта
/// и deadline детерминированы, а фиксированных sleep нет. Единый переход применяется одинаково к обычному
/// start и к обоим вариантам restart, поэтому тот же сценарий доказывает одинаковую семантику для всех
/// трёх путей.
/// </remarks>
public sealed class MuMuLifecycleRestartRetryTests
{
    [Fact(DisplayName = "Признак начала запуска в окне эффекта отменяет повтор launch")]
    public async Task ProvenLaunchEffectDoesNotRetry()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");

        MuMuLifecycleState current = MuMuLifecycleState.Running;
        bool launchSent = false;
        int observationsAfterLaunch = 0;

        context.Host.MutationHandler = request =>
        {
            if (request.Mutation == MuMuLifecycleMutation.Stop)
            {
                current = MuMuLifecycleState.Stopped;
            }
            else
            {
                // Launch начался: провайдер сообщает промежуточное состояние starting_*, которое host-side
                // правило отображает в Unknown, а не в Running.
                launchSent = true;
                observationsAfterLaunch = 0;
                current = MuMuLifecycleState.Unknown;
            }

            return MuMuLifecycleTestContext.Command(0);
        };
        context.Host.ObservationHandler = _ =>
        {
            if (launchSent)
            {
                observationsAfterLaunch++;
                if (observationsAfterLaunch > 3)
                {
                    current = MuMuLifecycleState.Running;
                }
            }

            return MuMuLifecycleTestContext.Observed(current);
        };

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.RestartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Running, result.Value!.FinalState);

        // Повтора нет: ровно одна mutation запуска.
        Assert.Equal(
            [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));
        Assert.Contains("mutations=stop(exit=0),start(exit=0)", result.Value!.Evidence, StringComparison.Ordinal);

        // Окно закончилось на признаке начала запуска, а не на своей границе.
        Assert.True(
            context.Host.ObservationCount < EffectWindowObservations(context),
            "Окно эффекта должно заканчиваться на признаке начала запуска.");

        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Warning));
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Error));
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Молча не сработавший launch даёт ровно один повтор и доказанный Running")]
    public async Task MissingEffectRetriesExactlyOnce()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");

        MuMuLifecycleState current = MuMuLifecycleState.Running;
        int launches = 0;
        List<MuMuLifecycleState> statesAtMutation = [];
        List<int> observationsAtMutation = [];

        context.Host.MutationHandler = request =>
        {
            statesAtMutation.Add(current);
            observationsAtMutation.Add(context.Host.ObservationCount);

            if (request.Mutation == MuMuLifecycleMutation.Stop)
            {
                current = MuMuLifecycleState.Stopped;
            }
            else
            {
                launches++;
                if (launches > 1)
                {
                    // Первый launch молча не сработал, повторный поднял экземпляр.
                    current = MuMuLifecycleState.Running;
                }
            }

            return MuMuLifecycleTestContext.Command(0);
        };
        context.Host.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(current);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.RestartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Running, result.Value!.FinalState);

        // Ровно два launch: первый и единственный повтор. Третьего launch нет.
        Assert.Equal(
            [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));
        Assert.Contains(
            "mutations=stop(exit=0),start(exit=0),start(exit=0)",
            result.Value!.Evidence,
            StringComparison.Ordinal);

        // Launch и повтор отправлены только при подтверждённом Stopped.
        Assert.Equal(
            [MuMuLifecycleState.Running, MuMuLifecycleState.Stopped, MuMuLifecycleState.Stopped],
            statesAtMutation);

        // Перед повтором состояние подтверждено заново: между launch и повтором ровно наблюдения окна
        // эффекта плюс одно подтверждающее наблюдение.
        Assert.Equal(
            EffectWindowObservations(context) + 1,
            observationsAtMutation[2] - observationsAtMutation[1]);

        // Окно исчерпано, а повтор уложился в общий deadline.
        Assert.True(result.Value!.Elapsed >= context.Timings.LaunchEffectWindow);
        Assert.True(result.Value!.Elapsed < context.Timings.RestartDeadline);

        Assert.Equal(2, context.Logger.Records.Count(record => record.Level == LogLevel.Warning));
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Error));
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Экземпляр уже не Stopped — повторный launch не отправляется")]
    public async Task LateEffectDoesNotRetry()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");

        int windowObservations = EffectWindowObservations(context);
        MuMuLifecycleState current = MuMuLifecycleState.Running;
        bool launchSent = false;
        int observationsAfterLaunch = 0;

        context.Host.MutationHandler = request =>
        {
            if (request.Mutation == MuMuLifecycleMutation.Stop)
            {
                current = MuMuLifecycleState.Stopped;
            }
            else
            {
                launchSent = true;
                observationsAfterLaunch = 0;
            }

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

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.RestartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Running, result.Value!.FinalState);
        Assert.Equal(
            [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));

        // Окно эффекта исчерпано (одно предупреждение), но повтора нет.
        Assert.Equal(1, context.Logger.Records.Count(record => record.Level == LogLevel.Warning));
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Error));
    }

    [Fact(DisplayName = "Второго повтора нет: исчерпанный повтор даёт честный timeout внутри того же deadline")]
    public async Task SingleRetryExhaustionGivesTimeoutWithinSameDeadline()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");

        MuMuLifecycleState current = MuMuLifecycleState.Running;
        context.Host.MutationHandler = request =>
        {
            if (request.Mutation == MuMuLifecycleMutation.Stop)
            {
                current = MuMuLifecycleState.Stopped;
            }

            // Оба launch молча не срабатывают: состояние остаётся Stopped.
            return MuMuLifecycleTestContext.Command(0);
        };
        context.Host.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(current);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.RestartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, failure.Code);
        Assert.Equal("restart", failure.Details!["operation"]);
        Assert.Equal("running", failure.Details!["target_state"]);
        Assert.Equal("stopped", failure.Details!["state"]);
        Assert.Equal("0", failure.Details!["exit_code"]);

        // Ровно один повтор: третьего launch нет, ложного успеха нет.
        Assert.Equal(
            [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));

        // Deadline достигнут и не сброшен повтором: elapsed лежит в пределах одного deadline.
        long elapsedMilliseconds = long.Parse(failure.Details!["elapsed_ms"], CultureInfo.InvariantCulture);
        Assert.True(elapsedMilliseconds >= (long)context.Timings.RestartDeadline.TotalMilliseconds);
        Assert.True(
            elapsedMilliseconds
                <= (long)(context.Timings.RestartDeadline + context.Timings.PollInterval).TotalMilliseconds);

        // Наблюдений не больше, чем вмещает один deadline вместе с окном эффекта и подтверждением: повтор
        // не удваивает бюджет опроса.
        int singleDeadlineObservations = (int)(context.Timings.RestartDeadline / context.Timings.PollInterval);
        Assert.True(
            context.Host.ObservationCount <= singleDeadlineObservations + EffectWindowObservations(context) + 2,
            $"Наблюдений {context.Host.ObservationCount}: повтор не должен удваивать бюджет опроса.");

        // Diagnostics различимы: launch без эффекта и факт повтора наблюдаемы отдельно от окончательного
        // отказа с его стабильным кодом.
        IReadOnlyList<MuMuLogRecord> records = context.Logger.Records;
        Assert.Equal(1, records.Count(record => record.EventId == LaunchEffectMissingEventId));
        Assert.Equal(1, records.Count(record => record.EventId == LaunchRetriedEventId));

        MuMuLogRecord failureRecord = records.Single(record => record.Level == LogLevel.Error);
        Assert.Contains(ApplicationFailure.MuMuLifecycleTimeout, failureRecord.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Gate.TrackedInstanceCount);
    }

    [Fact(DisplayName = "Признак начала запуска без доказанного Running даёт timeout без повтора")]
    public async Task EffectWithoutRunningTimesOutWithoutRetry()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");

        MuMuLifecycleState current = MuMuLifecycleState.Running;
        context.Host.MutationHandler = request =>
        {
            current = request.Mutation == MuMuLifecycleMutation.Stop
                ? MuMuLifecycleState.Stopped
                : MuMuLifecycleState.Unknown;
            return MuMuLifecycleTestContext.Command(0);
        };
        context.Host.ObservationHandler = _ => MuMuLifecycleTestContext.Observed(current);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.RestartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, result.FailureInfo!.Code);
        Assert.Equal("unknown", result.FailureInfo!.Details!["state"]);

        // Признак начала запуска есть, поэтому повтора нет, но успех без наблюдённого Running невозможен.
        Assert.Equal(
            [MuMuLifecycleMutation.Stop, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Warning));
        Assert.Equal(1, context.Logger.Records.Count(record => record.Level == LogLevel.Error));
    }

    [Fact(DisplayName = "Start применяет тот же единый переход: единственный повтор вместо прежнего отказа")]
    public async Task StartAppliesSameTransitionAsRestart()
    {
        // До единого контракта start не защищался от провайдерской гонки: launch после доказанного
        // Stopped молча не срабатывал, и операция держалась весь deadline. Теперь start выражается через
        // тот же переход, что и restart, поэтому получает ровно один повтор.
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Stopped);
        context.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(0);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StartAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, result.FailureInfo!.Code);

        // Ровно два launch: первый и единственный повтор. Третьего нет.
        Assert.Equal(
            [MuMuLifecycleMutation.Start, MuMuLifecycleMutation.Start],
            context.Host.MutationRequests.Select(request => request.Mutation));

        // Diagnostics те же, что и у restart: mutation без эффекта и факт повтора различимы отдельно.
        Assert.Equal(1, context.Logger.Records.Count(record => record.EventId == LaunchEffectMissingEventId));
        Assert.Equal(1, context.Logger.Records.Count(record => record.EventId == LaunchRetriedEventId));
    }

    [Fact(DisplayName = "Stop не получил ни окна эффекта, ни повтора: поведение не изменилось")]
    public async Task StopKeepsSingleMutationWithoutRetry()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("1");
        context.Host.ObservationResult = MuMuLifecycleTestContext.Observed(MuMuLifecycleState.Running);
        context.Host.MutationHandler = _ => MuMuLifecycleTestContext.Command(0);

        ApplicationResult<MuMuLifecycleOutcome> result = await context.Service.StopAsync(
            MuMuLifecycleTestContext.Installation,
            instance,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, result.FailureInfo!.Code);
        Assert.Equal(1, context.Host.MutationCount);
        Assert.Equal(0, context.Logger.Records.Count(record => record.Level == LogLevel.Warning));
    }
}
