using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства lifecycle игры Azur Lane: идемпотентность, package-scoped mutation, доказанный
/// postcondition, композиция перезапуска, deadline и отмена.
/// </summary>
/// <remarks>
/// <para>
/// Проверки идут через настоящий <see cref="AzurLaneGameLifecycleService"/> на подменяемой host-границе:
/// подменяются ответы устройства, а не orchestration. Postcondition доказывается наблюдением, поэтому
/// код выхода команды ADB ни в одном сценарии не подменяет доказательство.
/// </para>
/// <para>
/// Ни установленная MuMu, ни ADB, ни установленная игра не требуются; время идёт через управляемый
/// источник, а числа времени берутся у их владельца <see cref="AndroidLifecycleTimings"/>.
/// </para>
/// </remarks>
[Trait("Category", "Android")]
public sealed class AzurLaneGameLifecycleTests
{
    // --- Запуск ---

    [Fact(DisplayName = "Запуск доказанно запущенной игры успешен без mutation")]
    public async Task StartWhenForegroundDoesNotMutate()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetForeground();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        AzurLaneGameLifecycleOutcome outcome = result.Value!;

        Assert.Equal("start", outcome.Operation);
        Assert.Equal(AzurLaneGameState.Foreground, outcome.InitialState);
        Assert.Equal(AzurLaneGameState.Foreground, outcome.FinalState);
        Assert.Equal("none", outcome.MutationEvidence);
        Assert.Null(outcome.Launcher);

        Assert.Empty(context.Host.MutationRequests);
        Assert.Empty(context.Host.LauncherRequests);
        Assert.Equal(0, context.Gate.TrackedTargetCount);
    }

    [Fact(DisplayName = "Запуск игры из фона выполняет ровно одну package-scoped mutation и доказывает foreground")]
    public async Task StartFromBackgroundMutatesOnceAndProvesForeground()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetBackground(4242);

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        AzurLaneGameLifecycleOutcome outcome = result.Value!;

        Assert.Equal(AzurLaneGameState.Background, outcome.InitialState);
        Assert.Equal(AzurLaneGameState.Foreground, outcome.FinalState);

        // Перечень состоит ровно из выполненной mutation запуска: заглушка «mutation не выполнялось»
        // префиксом не становится.
        Assert.Equal("start(exit=0)", outcome.MutationEvidence);
        Assert.DoesNotContain("none", outcome.MutationEvidence, StringComparison.Ordinal);
        Assert.Equal(device.Launcher.Component, outcome.Launcher);

        AndroidMutationRequest mutation = Assert.Single(context.Host.MutationRequests);

        Assert.Equal(AndroidGameMutation.Start, mutation.Mutation);
        Assert.Equal(AndroidTestContext.Package, mutation.Package);
        Assert.Equal(AndroidTestContext.Endpoint, mutation.Endpoint);
        Assert.Equal(0, context.Gate.TrackedTargetCount);
    }

    [Fact(DisplayName = "Запуск остановленной игры выполняет mutation и доказывает foreground")]
    public async Task StartFromStoppedMutatesAndProvesForeground()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AzurLaneGameState.Stopped, result.Value!.InitialState);
        Assert.Equal(AzurLaneGameState.Foreground, result.Value!.FinalState);

        // Одиночный запуск сообщает ровно выполненную mutation запуска, без заглушки-префикса.
        Assert.Equal("start(exit=0)", result.Value!.MutationEvidence);
        Assert.Equal(AndroidGameMutation.Start, Assert.Single(context.Host.MutationRequests).Mutation);
    }

    [Fact(DisplayName = "Неудачный код выхода mutation не отменяет доказанный foreground")]
    public async Task NonZeroMutationExitDoesNotOverrideProvenPostcondition()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();
        device.StartExitCode = 1;

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AzurLaneGameState.Foreground, result.Value!.FinalState);

        // Код выхода сообщается как факт, а перечень остаётся перечнем выполненных mutation.
        Assert.Equal("start(exit=1)", result.Value!.MutationEvidence);
    }

    [Fact(DisplayName = "Отсутствие пакета игры — fail-closed отказ до всякой mutation")]
    public async Task StartWhenPackageMissingDoesNotMutate()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetNotInstalled();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AzurLanePackageMissing, result.FailureInfo!.Code);
        Assert.Equal(
            AndroidDetailValues.Absent,
            result.FailureInfo!.Details![AndroidDetailKeys.State]);
        Assert.Empty(context.Host.MutationRequests);
        Assert.Empty(context.Host.LauncherRequests);
    }

    [Fact(DisplayName = "Недоказанный launcher-компонент не адресует mutation")]
    public async Task UnprovenLauncherIsNotAddressed()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();
        device.Launcher = AndroidTestContext.UnprovenLauncher();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AzurLaneLauncherUnresolved, result.FailureInfo!.Code);

        // Недоказанный запрос — не «компонента нет»: причина сохраняется в details.
        Assert.Equal(
            AndroidDetailValues.QueryFailed,
            result.FailureInfo!.Details![AndroidDetailKeys.State]);
        Assert.Empty(context.Host.MutationRequests);
    }

    [Fact(DisplayName = "Отсутствующий launcher-компонент не адресует mutation")]
    public async Task MissingLauncherIsNotAddressed()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();
        device.Launcher = AndroidTestContext.MissingLauncher();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AzurLaneLauncherUnresolved, result.FailureInfo!.Code);
        Assert.Equal(
            AndroidDetailValues.Missing,
            result.FailureInfo!.Details![AndroidDetailKeys.State]);
        Assert.Empty(context.Host.MutationRequests);
    }

    [Fact(DisplayName = "Неоднозначный launcher-компонент не адресует mutation")]
    public async Task AmbiguousLauncherIsNotAddressed()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();
        device.Launcher = AndroidTestContext.AmbiguousLauncher(3);

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AzurLaneLauncherAmbiguous, result.FailureInfo!.Code);
        Assert.Equal(
            "3",
            result.FailureInfo!.Details![AndroidDetailKeys.MatchingComponents]);
        Assert.Empty(context.Host.MutationRequests);
    }

    [Fact(DisplayName = "Запуск без доказанного foreground ограничен deadline")]
    public async Task StartTimeoutIsReported()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();
        device.StartReachesForeground = false;

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.AzurLaneLifecycleTimeout, failure.Code);
        Assert.Equal(
            AndroidDetailValues.StateForeground,
            failure.Details![AndroidDetailKeys.TargetState]);
        Assert.Equal(AndroidDetailValues.StateStopped, failure.Details[AndroidDetailKeys.State]);
        Assert.True(failure.IsRetryable);

        AssertBoundedElapsed(failure, context.Timings.GameStartDeadline, context.Timings.PollInterval);
        _ = Assert.Single(context.Host.MutationRequests);
    }

    [Fact(DisplayName = "Mutation с ненулевым кодом выхода без postcondition — отказ, а не ожидание deadline")]
    public async Task FailedMutationWithoutPostconditionIsReported()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();
        device.StartReachesForeground = false;
        device.StartExitCode = 2;

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ApplicationFailure.AzurLaneLifecyclePostconditionNotMet,
            result.FailureInfo!.Code);
        Assert.Equal("2", result.FailureInfo!.Details![AndroidDetailKeys.ExitCode]);
        Assert.False(result.FailureInfo!.IsRetryable);

        // Причина не во времени: ожидание deadline не выполняется.
        _ = Assert.Single(context.Host.MutationRequests);
    }

    // --- Остановка ---

    [Fact(DisplayName = "Остановка игры на переднем плане доказывает отсутствие процесса и foreground")]
    public async Task StopFromForegroundProvesStopped()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetForeground();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StopAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        AzurLaneGameLifecycleOutcome outcome = result.Value!;

        Assert.Equal(AzurLaneGameState.Foreground, outcome.InitialState);
        Assert.Equal(AzurLaneGameState.Stopped, outcome.FinalState);

        // Одиночная остановка сообщает ровно выполненную mutation остановки, без заглушки-префикса.
        Assert.Equal("force_stop(exit=0)", outcome.MutationEvidence);

        AndroidMutationRequest mutation = Assert.Single(context.Host.MutationRequests);

        Assert.Equal(AndroidGameMutation.ForceStop, mutation.Mutation);
        Assert.Equal(AndroidTestContext.Package, mutation.Package);
        Assert.Equal(AndroidTestContext.Endpoint, mutation.Endpoint);
        Assert.Equal(0, context.Gate.TrackedTargetCount);
    }

    [Fact(DisplayName = "Остановка игры в фоне выполняет force-stop и доказывает остановку")]
    public async Task StopFromBackgroundProvesStopped()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetBackground(4242, 4243);

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StopAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AzurLaneGameState.Background, result.Value!.InitialState);
        Assert.Equal(AzurLaneGameState.Stopped, result.Value!.FinalState);
        Assert.Equal(AndroidGameMutation.ForceStop, Assert.Single(context.Host.MutationRequests).Mutation);
    }

    [Fact(DisplayName = "Остановка уже остановленной игры успешна без mutation")]
    public async Task StopWhenAlreadyStoppedDoesNotMutate()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StopAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AzurLaneGameState.Stopped, result.Value!.FinalState);
        Assert.Equal("none", result.Value!.MutationEvidence);
        Assert.Empty(context.Host.MutationRequests);
    }

    [Fact(DisplayName = "Остановка отсутствующей игры — fail-closed отказ до всякой mutation")]
    public async Task StopWhenPackageMissingDoesNotMutate()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetNotInstalled();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StopAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AzurLanePackageMissing, result.FailureInfo!.Code);
        Assert.Empty(context.Host.MutationRequests);
    }

    [Fact(DisplayName = "Остановка без доказанного отсутствия процессов ограничена deadline")]
    public async Task StopTimeoutIsReported()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetBackground(4242);
        device.ForceStopReachesStopped = false;

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StopAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.AzurLaneLifecycleTimeout, failure.Code);
        Assert.Equal(
            AndroidDetailValues.StateStopped,
            failure.Details![AndroidDetailKeys.TargetState]);
        Assert.Equal(AndroidDetailValues.StateBackground, failure.Details[AndroidDetailKeys.State]);
        Assert.True(failure.IsRetryable);

        AssertBoundedElapsed(failure, context.Timings.GameStopDeadline, context.Timings.PollInterval);

        // Наблюдение повторяется в пределах бюджета фазы, а не бесконечно: первое наблюдение — начальное
        // состояние операции, остальные — bounded polling фазы остановки.
        int boundedPolls = (int)(context.Timings.GameStopDeadline / context.Timings.PollInterval) + 2;

        Assert.InRange(context.Host.ProcessRequests.Count, 2, boundedPolls);
    }

    [Fact(DisplayName = "Force-stop с ненулевым кодом выхода без доказанной остановки — отказ по postcondition")]
    public async Task FailedForceStopWithoutProvenStopIsReported()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetBackground(4242);
        device.ForceStopReachesStopped = false;
        device.ForceStopExitCode = 1;

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StopAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ApplicationFailure.AzurLaneLifecyclePostconditionNotMet,
            result.FailureInfo!.Code);
        Assert.Equal("1", result.FailureInfo!.Details![AndroidDetailKeys.ExitCode]);
        _ = Assert.Single(context.Host.MutationRequests);
    }

    // --- Перезапуск ---

    [Fact(DisplayName = "Перезапуск запущенной игры — композиция остановки и запуска")]
    public async Task RestartComposesStopAndStart()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetForeground();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.RestartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        AzurLaneGameLifecycleOutcome outcome = result.Value!;

        // Итог описывает операцию целиком: состояние на входе операции (игра была на переднем плане),
        // а не вход фазы запуска, и доказанный foreground в конце.
        Assert.Equal(AzurLaneGameState.Foreground, outcome.InitialState);
        Assert.Equal(AzurLaneGameState.Foreground, outcome.FinalState);

        // Композиция доказана: остановка, затем запуск.
        AndroidGameMutation[] performed = [.. context.Host.MutationRequests.Select(request => request.Mutation)];

        Assert.Contains(AndroidGameMutation.ForceStop, performed);
        Assert.Contains(AndroidGameMutation.Start, performed);

        // Итог перечисляет все выполненные mutation в порядке выполнения, включая force_stop фазы
        // остановки: иначе итог сообщал бы о перезапуске как о запуске без остановки.
        Assert.Equal("force_stop(exit=0),start(exit=0)", outcome.MutationEvidence);
        Assert.DoesNotContain("none", outcome.MutationEvidence, StringComparison.Ordinal);

        Assert.NotNull(outcome.Launcher);
        Assert.Equal(device.Launcher.Component, outcome.Launcher);
        Assert.Equal(0, context.Gate.TrackedTargetCount);
    }

    [Fact(DisplayName = "Перезапуск остановленной игры определён как запуск без mutation остановки")]
    public async Task RestartOfStoppedGameIsStart()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.RestartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        AndroidGameMutation[] expectedMutations = [AndroidGameMutation.Start];

        AndroidGameMutation[] actualMutations = [.. context.Host.MutationRequests.Select(request => request.Mutation)];

        // Остановленной игре останавливать нечего: перезапуск определён как запуск, и состояние на входе
        // операции — stopped.
        Assert.Equal(expectedMutations, actualMutations);
        Assert.Equal(AzurLaneGameState.Stopped, result.Value!.InitialState);
        Assert.Equal(AzurLaneGameState.Foreground, result.Value!.FinalState);

        // Mutation остановки не выполнялась, поэтому в перечне нет ни force_stop, ни заглушки.
        Assert.Equal("start(exit=0)", result.Value!.MutationEvidence);
    }

    [Fact(DisplayName = "Перезапуск игры в фоне выполняет force-stop и запуск")]
    public async Task RestartOfBackgroundGameStopsAndStarts()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetBackground(4242);

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.RestartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        AzurLaneGameLifecycleOutcome outcome = result.Value!;

        // Итог описывает состояние на входе операции, а не вход фазы запуска.
        Assert.Equal(AzurLaneGameState.Background, outcome.InitialState);
        Assert.Equal(AzurLaneGameState.Foreground, outcome.FinalState);

        AndroidGameMutation[] performed = [.. context.Host.MutationRequests.Select(request => request.Mutation)];

        Assert.Contains(AndroidGameMutation.ForceStop, performed);
        Assert.Contains(AndroidGameMutation.Start, performed);

        // Порядок перечня — порядок выполнения: force_stop фазы остановки идёт перед mutation запуска.
        Assert.Equal("force_stop(exit=0),start(exit=0)", outcome.MutationEvidence);
    }

    [Fact(DisplayName = "Неразрешимый launcher останавливает перезапуск до mutation остановки")]
    public async Task RestartWithUnresolvedLauncherDoesNotMutate()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetForeground();
        device.Launcher = AndroidTestContext.MissingLauncher();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.RestartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AzurLaneLauncherUnresolved, result.FailureInfo!.Code);

        // Состояние игры не изменено: mutation остановки не выполнялась.
        Assert.Empty(context.Host.MutationRequests);
        Assert.Equal(AndroidForegroundStatus.Foreground, device.Foreground.Status);
    }

    [Fact(DisplayName = "Перезапуск не продлевает бюджет: фаза запуска получает остаток")]
    public async Task RestartDoesNotExtendBudget()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetForeground();
        device.StartReachesForeground = false;

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.RestartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.AzurLaneLifecycleTimeout, result.FailureInfo!.Code);
        Assert.Equal(
            AndroidDetailValues.StateForeground,
            result.FailureInfo!.Details![AndroidDetailKeys.TargetState]);

        AssertBoundedElapsed(
            result.FailureInfo!,
            context.Timings.GameRestartDeadline,
            context.Timings.PollInterval);

        AndroidGameMutation[] expectedMutations = [AndroidGameMutation.ForceStop, AndroidGameMutation.Start];

        AndroidGameMutation[] actualMutations = [.. context.Host.MutationRequests.Select(request => request.Mutation)];

        Assert.Equal(expectedMutations, actualMutations);
    }

    // --- Отмена ---

    [Fact(DisplayName = "Отмена до получения координации не выполняет ни наблюдения, ни mutation")]
    public async Task CancellationBeforeGateDoesNotObserveOrMutate()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            source.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
        Assert.Equal(AndroidDetailValues.GatePhase, result.FailureInfo!.Details![AndroidDetailKeys.Phase]);

        Assert.Empty(context.Host.PackageRequests);
        Assert.Empty(context.Host.MutationRequests);
        Assert.Equal(0, context.Gate.TrackedTargetCount);
    }

    [Fact(DisplayName = "Отмена во время ожидания postcondition сообщает фазу polling")]
    public async Task CancellationDuringPollingIsReported()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetForeground();
        device.ForceStopReachesStopped = false;
        using CancellationTokenSource source = new();
        device.MutationApplied = _ => source.Cancel();

        ApplicationResult<AzurLaneGameLifecycleOutcome> result = await context.Lifecycle.StopAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            source.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
        Assert.Equal(AndroidDetailValues.PollingPhase, result.FailureInfo!.Details![AndroidDetailKeys.Phase]);
        _ = Assert.Single(context.Host.MutationRequests);
        Assert.Equal(0, context.Gate.TrackedTargetCount);
    }

    [Fact(DisplayName = "Evidence доказанного postcondition ограничен и однострочен")]
    public async Task ProvenEvidenceIsBounded()
    {
        AndroidTestContext context = new();
        TestAndroidDevice device = new(context.Host);
        device.SetStopped();

        AzurLaneGameLifecycleOutcome outcome = (await context.Lifecycle.StartAsync(
            AndroidTestContext.Installation,
            AndroidTestContext.Endpoint,
            CancellationToken.None)).Value!;

        Assert.InRange(outcome.Evidence.Length, 1, BoundedDiagnosticText.MaxLength);
        Assert.DoesNotContain("\n", outcome.Evidence, StringComparison.Ordinal);
    }

    private static void AssertBoundedElapsed(ApplicationFailure failure, TimeSpan deadline, TimeSpan pollInterval)
    {
        long elapsed = long.Parse(
            failure.Details![AndroidDetailKeys.ElapsedMilliseconds],
            CultureInfo.InvariantCulture);

        Assert.InRange(elapsed, 0L, (long)(deadline + pollInterval).TotalMilliseconds);
    }
}
