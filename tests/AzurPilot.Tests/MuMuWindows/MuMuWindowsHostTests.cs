using System.Reflection;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.MuMu;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Доказательства production-реализации <see cref="IMuMuHost"/> на Windows: обнаружение установки,
/// перечисление экземпляров, авторитетное наблюдение состояния конкретного экземпляра и ровно одна
/// mutation над точной identity.
/// </summary>
/// <remarks>
/// <para>
/// Проверки собирают host на всех четырёх подменяемых внешних границах (реестр, install metadata,
/// файловая система, запуск процесса), поэтому через них проходит настоящий production-код: discovery,
/// построение аргументов, разбор ответа, правило состояния и проекция отказов. Реальная MuMu не
/// требуется и не используется.
/// </para>
/// <para>
/// Payload-ы ответов сняты с реальной установки MuMuPlayer; синтетические варианты отличаются от них
/// ровно одним признаком формы и помечены в комментариях.
/// </para>
/// </remarks>
[Trait("Category", "MuMuWindows")]
public sealed class MuMuWindowsHostTests
{
    private static readonly MuMuInstanceId FirstInstance = MuMuInstanceId.FromIndex("1");
    private static readonly MuMuInstanceId SecondInstance = MuMuInstanceId.FromIndex("2");

    /// <summary>Форма ответа во время перехода запуска, наблюдённая на реальной установке.</summary>
    private const string TransitionalInstanceResponse = """
        {
          "android_version": "15.0",
          "error_code": 0,
          "index": "1",
          "is_android_started": false,
          "is_process_started": true,
          "name": "Azur lane",
          "player_state": "starting_vm"
        }
        """;

    /// <summary>Синтетический отказ провайдера с кодом, смысл которого не доказан.</summary>
    private const string UnknownProviderCodeResponse = """
        {
          "errcode": -7,
          "errmsg": "synthetic refusal"
        }
        """;

    /// <summary>Синтетический ответ перечисления, форма которого не распознана.</summary>
    private const string UnknownShapeResponse = """
        {
          "instances": []
        }
        """;

    /// <summary>Синтетический ответ перечисления без отображаемого имени экземпляра.</summary>
    private const string NoDisplayNameEnumerationResponse = """
        {
          "1": {
            "android_version": "15.0",
            "index": "1",
            "is_android_started": false,
            "is_process_started": false
          }
        }
        """;

    // --- Обнаружение установки ---

    [Fact(DisplayName = "Обнаруженная установка отдаётся с версией, корнем и точкой входа control surface")]
    public void DiscoveryReturnsSingleInstallation()
    {
        HostFixture fixture = new();
        string installRoot = fixture.AddInstallation("single");

        ApplicationResult<MuMuInstallation> result = fixture.Host.DiscoverInstallation();

        Assert.True(result.IsSuccess);

        MuMuInstallation installation = result.Value!;

        Assert.Equal(MuMuTestInstallation.Version, installation.Version);
        Assert.Equal(installRoot, installation.InstallRoot);
        Assert.Equal(MuMuTestInstallation.ControlExecutablePath(installRoot), installation.ControlExecutablePath);
    }

    [Fact(DisplayName = "Отсутствие установки даёт mumu_installation_not_found")]
    public void MissingInstallationGivesInstallationNotFound()
    {
        HostFixture fixture = new();

        ApplicationResult<MuMuInstallation> result = fixture.Host.DiscoverInstallation();

        AssertFailure(result, ApplicationFailure.MuMuInstallationNotFound);
        Assert.Equal("0", result.FailureInfo!.Details![MuMuFailureDetailKeys.RejectedCandidates]);
        Assert.False(result.FailureInfo!.Details!.ContainsKey(MuMuFailureDetailKeys.RejectionReasons));
    }

    [Fact(DisplayName = "Установка без поддерживаемой control surface даёт mumu_control_surface_unsupported")]
    public void InstallationWithoutControlSurfaceGivesUnsupported()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("broken", withControlExecutable: false);

        ApplicationResult<MuMuInstallation> result = fixture.Host.DiscoverInstallation();

        AssertFailure(result, ApplicationFailure.MuMuControlSurfaceUnsupported);
        Assert.Equal(
            MuMuFailureReasons.ControlSurfaceMissing,
            result.FailureInfo!.Details![MuMuFailureDetailKeys.Reason]);
    }

    [Fact(DisplayName = "Устаревшая запись об установке даёт mumu_installation_not_found с причиной")]
    public void StaleInstallationEntryGivesInstallationNotFound()
    {
        HostFixture fixture = new();
        string installRoot = MuMuWindowsTestPaths.Create("stale");

        fixture.Registry.Add(MuMuTestInstallation.RegistryCandidate(installRoot, MuMuTestInstallation.Version));

        ApplicationResult<MuMuInstallation> result = fixture.Host.DiscoverInstallation();

        AssertFailure(result, ApplicationFailure.MuMuInstallationNotFound);
        Assert.Equal(
            MuMuInstallationRejectionReasons.InstallRootMissing,
            result.FailureInfo!.Details![MuMuFailureDetailKeys.RejectionReasons]);
    }

    [Fact(DisplayName = "Несколько установок не разрешаются молчаливым выбором первой")]
    public void AmbiguousInstallationIsNotResolved()
    {
        HostFixture fixture = new();
        string first = fixture.AddInstallation("first");
        _ = fixture.AddInstallation("second");

        ApplicationResult<MuMuInstallation> result = fixture.Host.DiscoverInstallation();

        AssertFailure(result, ApplicationFailure.MuMuInstallationAmbiguous);
        Assert.Equal(
            MuMuFailureReasons.InstallationAmbiguous,
            result.FailureInfo!.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal("2", result.FailureInfo!.Details![MuMuFailureDetailKeys.InstallationsFound]);

        // Первая установка не выбирается и в details не протекает: evidence bounded и без путей.
        Assert.DoesNotContain(result.FailureInfo!.Details!.Values, value => value.Contains('\\'));
        Assert.DoesNotContain(result.FailureInfo!.Details!.Values, value => value.Contains('/'));
        Assert.DoesNotContain(
            result.FailureInfo!.Details!.Values,
            value => value.Contains(first, StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "Неоднозначность установки и её отсутствие различимы по коду, а не по тексту сообщения")]
    public void AmbiguityAndAbsenceAreDistinctByCode()
    {
        HostFixture ambiguous = new();
        _ = ambiguous.AddInstallation("ambiguous-first");
        _ = ambiguous.AddInstallation("ambiguous-second");

        HostFixture absent = new();

        ApplicationResult<MuMuInstallation> ambiguousResult = ambiguous.Host.DiscoverInstallation();
        ApplicationResult<MuMuInstallation> absentResult = absent.Host.DiscoverInstallation();

        Assert.True(ambiguousResult.IsFailure);
        Assert.True(absentResult.IsFailure);

        // Потребитель, который смотрит только на код, различает эти два случая.
        Assert.Equal(ApplicationFailure.MuMuInstallationAmbiguous, ambiguousResult.FailureInfo!.Code);
        Assert.Equal(ApplicationFailure.MuMuInstallationNotFound, absentResult.FailureInfo!.Code);
        Assert.NotEqual(ApplicationFailure.MuMuInstallationNotFound, ambiguousResult.FailureInfo!.Code);
        Assert.NotEqual(ApplicationFailure.MuMuInstallationAmbiguous, absentResult.FailureInfo!.Code);
    }

    [Fact(DisplayName = "Отказ источника обнаружения пробрасывается без подмены")]
    public void DiscoverySourceFailureIsPropagatedUnchanged()
    {
        HostFixture fixture = new();
        fixture.Registry.Failure = new UnauthorizedAccessException("Доступ к разделу реестра запрещён.");

        // Отказ адаптера обнаружения — точный: host не пересоздаёт его и не подменяет своим кодом.
        MuMuInstallationDiscovery adapter = new(fixture.Registry, fixture.Metadata, fixture.FileSystem);
        ApplicationResult<MuMuInstallationDiscoveryResult> adapterResult = adapter.Discover();

        Assert.True(adapterResult.IsFailure);

        ApplicationResult<MuMuInstallation> result = fixture.Host.DiscoverInstallation();

        Assert.True(result.IsFailure);
        Assert.Equal(adapterResult.FailureInfo!, result.FailureInfo!);
        Assert.Equal(
            MuMuFailureReasons.RegistryAccessFailed,
            result.FailureInfo!.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal(
            nameof(UnauthorizedAccessException),
            result.FailureInfo!.Details![MuMuFailureDetailKeys.ExceptionType]);
    }

    [Fact(DisplayName = "Обнаружение установки пишется в существующий logging stack")]
    public void DiscoveryIsLogged()
    {
        RecordingMuMuHostLogger logger = new();
        HostFixture fixture = new(logger);
        _ = fixture.AddInstallation("logged");

        _ = fixture.Host.DiscoverInstallation();

        RecordedLogEntry entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(2101, entry.EventId);
        Assert.Contains(MuMuTestInstallation.Version, entry.Message, StringComparison.Ordinal);
    }

    // --- Перечисление экземпляров ---

    [Fact(DisplayName = "Перечисление отдаёт экземпляры установки по их stable identity")]
    public void EnumerationMapsInstances()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("enumeration");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, MuMuObservedPayloads.TwoInstancesEnumerationResponse);

        ApplicationResult<IReadOnlyList<MuMuInstance>> result = fixture.Host.EnumerateInstances(installation);

        Assert.True(result.IsSuccess);

        IReadOnlyList<MuMuInstance> instances = result.Value!;

        Assert.Equal(2, instances.Count);
        Assert.Equal(FirstInstance, instances[0].Id);
        Assert.Equal("Azur lane", instances[0].DisplayName);
        Assert.Equal("15.0", instances[0].AndroidVersion);
        Assert.Equal(SecondInstance, instances[1].Id);
        Assert.Equal("Второй экземпляр", instances[1].DisplayName);
        Assert.Equal("12.0", instances[1].AndroidVersion);

        AssertArguments(["info", "--vmindex", "all"], Assert.Single(fixture.Runner.Requests).Request.Arguments);
    }

    [Fact(DisplayName = "Записи-ошибки перечисления не становятся экземплярами")]
    public void EnumerationSkipsUnavailableEntries()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("partial");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, MuMuObservedPayloads.PartialEnumerationResponse);

        ApplicationResult<IReadOnlyList<MuMuInstance>> result = fixture.Host.EnumerateInstances(installation);

        Assert.True(result.IsSuccess);
        Assert.Equal(FirstInstance, Assert.Single(result.Value!).Id);
    }

    [Fact(DisplayName = "Отсутствующие сведения экземпляра не додумываются")]
    public void EnumerationKeepsMissingDetailsEmpty()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("no-name");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, NoDisplayNameEnumerationResponse);

        ApplicationResult<IReadOnlyList<MuMuInstance>> result = fixture.Host.EnumerateInstances(installation);

        MuMuInstance instance = Assert.Single(result.Value!);

        Assert.Equal(FirstInstance, instance.Id);
        Assert.Equal(string.Empty, instance.DisplayName);
        Assert.Equal("15.0", instance.AndroidVersion);
    }

    [Fact(DisplayName = "Пустое перечисление — успешный результат без экземпляров")]
    public void EnumerationOfEmptyInstallationIsSuccessful()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("empty");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, "{}");

        ApplicationResult<IReadOnlyList<MuMuInstance>> result = fixture.Host.EnumerateInstances(installation);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact(DisplayName = "Нераспознанная форма перечисления закрыта отказом control surface")]
    public void EnumerationFailsClosedOnUnknownShape()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("unknown-shape");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, UnknownShapeResponse);

        ApplicationResult<IReadOnlyList<MuMuInstance>> result = fixture.Host.EnumerateInstances(installation);

        AssertFailure(result, ApplicationFailure.MuMuControlSurfaceUnsupported);
        Assert.Equal(
            MuMuFailureReasons.ResponseUnrecognized,
            result.FailureInfo!.Details![MuMuFailureDetailKeys.Reason]);
    }

    [Fact(DisplayName = "Недостижимая control surface закрыта отказом при перечислении")]
    public void EnumerationPropagatesUnreachableControlSurface()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("unreachable");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueFailure(MuMuPlatformFailureMapper.ForProcessStartFailure(
            installation.ControlExecutablePath,
            new InvalidOperationException("Процесс не запущен.")));

        ApplicationResult<IReadOnlyList<MuMuInstance>> result = fixture.Host.EnumerateInstances(installation);

        AssertFailure(result, ApplicationFailure.MuMuControlSurfaceUnsupported);
        Assert.Equal(
            MuMuFailureReasons.ProcessStartFailed,
            result.FailureInfo!.Details![MuMuFailureDetailKeys.Reason]);
    }

    // --- Наблюдение состояния ---

    [Fact(DisplayName = "Запущенный экземпляр наблюдается как Running с bounded evidence")]
    public void ObserveRunningInstance()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("running");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, MuMuObservedPayloads.RunningInstanceResponse);

        ApplicationResult<MuMuInstanceState> result = fixture.Host.ObserveInstanceState(installation, FirstInstance);

        Assert.True(result.IsSuccess);

        MuMuInstanceState state = result.Value!;

        Assert.Equal(MuMuLifecycleState.Running, state.State);
        Assert.Contains("player_state=start_finished", state.Evidence, StringComparison.Ordinal);
        Assert.Contains("is_process_started=true", state.Evidence, StringComparison.Ordinal);
        Assert.Contains("is_android_started=true", state.Evidence, StringComparison.Ordinal);
        Assert.True(state.Evidence.Length <= BoundedDiagnosticText.MaxLength);
    }

    [Fact(DisplayName = "Остановленный экземпляр наблюдается как Stopped с bounded evidence")]
    public void ObserveStoppedInstance()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("stopped");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, MuMuObservedPayloads.StoppedInstanceResponse);

        ApplicationResult<MuMuInstanceState> result = fixture.Host.ObserveInstanceState(installation, FirstInstance);

        MuMuInstanceState state = result.Value!;

        Assert.Equal(MuMuLifecycleState.Stopped, state.State);
        Assert.Contains("player_state=absent", state.Evidence, StringComparison.Ordinal);
        Assert.Contains("is_process_started=false", state.Evidence, StringComparison.Ordinal);
        Assert.Contains("is_android_started=false", state.Evidence, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Наблюдение адресуется точной identity, а не всем экземплярам")]
    public void ObservationAddressesExactInstance()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("exact");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, MuMuObservedPayloads.RunningInstanceResponse);

        _ = fixture.Host.ObserveInstanceState(installation, FirstInstance);

        RecordedProcessRequest recorded = Assert.Single(fixture.Runner.Requests);

        AssertArguments(["info", "--vmindex", "1"], recorded.Request.Arguments);
        Assert.Equal(installation.ControlExecutablePath, recorded.Request.ExecutablePath);
    }

    [Fact(DisplayName = "Переходное состояние не выдаётся за запущенное")]
    public void TransitionalStateIsNotRunning()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("transitional");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, TransitionalInstanceResponse);

        ApplicationResult<MuMuInstanceState> result = fixture.Host.ObserveInstanceState(installation, FirstInstance);

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuLifecycleState.Unknown, result.Value!.State);
        Assert.NotEqual(MuMuLifecycleState.Running, result.Value!.State);
        Assert.Contains("player_state=starting_vm", result.Value!.Evidence, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Несуществующий экземпляр даёт mumu_instance_not_found")]
    public void ObserveMissingInstanceGivesInstanceNotFound()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("missing-instance");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(
            MuMuObservedPayloads.IndexNotFoundExitCode,
            MuMuObservedPayloads.IndexNotFoundResponse);

        ApplicationResult<MuMuInstanceState> result =
            fixture.Host.ObserveInstanceState(installation, MuMuInstanceId.FromIndex("5"));

        AssertFailure(result, ApplicationFailure.MuMuInstanceNotFound);
        Assert.Equal("mumu:5", result.FailureInfo!.Details![MuMuFailureDetailKeys.InstanceId]);
    }

    [Fact(DisplayName = "Недоказанный код отказа провайдера закрыт, а не истолкован как отсутствие экземпляра")]
    public void ObserveUnknownProviderCodeFailsClosed()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("unknown-code");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(-7, UnknownProviderCodeResponse);

        ApplicationResult<MuMuInstanceState> result = fixture.Host.ObserveInstanceState(installation, FirstInstance);

        AssertFailure(result, ApplicationFailure.MuMuControlSurfaceUnsupported);
        Assert.Equal(
            MuMuFailureReasons.ProviderErrorUnrecognized,
            result.FailureInfo!.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal("-7", result.FailureInfo!.Details![MuMuFailureDetailKeys.ProviderErrorCode]);
        Assert.Equal("mumu:1", result.FailureInfo!.Details![MuMuFailureDetailKeys.InstanceId]);
    }

    [Fact(DisplayName = "Нераспознанная форма наблюдения закрыта отказом control surface")]
    public void ObserveUnknownShapeFailsClosed()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("unknown-state-shape");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, UnknownShapeResponse);

        ApplicationResult<MuMuInstanceState> result = fixture.Host.ObserveInstanceState(installation, FirstInstance);

        AssertFailure(result, ApplicationFailure.MuMuControlSurfaceUnsupported);
        Assert.Equal(
            MuMuFailureReasons.ResponseUnrecognized,
            result.FailureInfo!.Details![MuMuFailureDetailKeys.Reason]);
    }

    [Fact(DisplayName = "Дедлайн наблюдения пробрасывается как mumu_lifecycle_timeout")]
    public void ObservePropagatesProcessTimeout()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("timeout");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueFailure(MuMuPlatformFailureMapper.ForProcessTimeout(
            installation.ControlExecutablePath,
            TimeSpan.FromSeconds(1),
            0,
            0));

        ApplicationResult<MuMuInstanceState> result = fixture.Host.ObserveInstanceState(installation, FirstInstance);

        AssertFailure(result, ApplicationFailure.MuMuLifecycleTimeout);
        Assert.True(result.FailureInfo!.IsRetryable);
    }

    // --- Mutation ---

    [Fact(DisplayName = "Запуск выполняется ровно одной командой над точной identity")]
    public void StartMutationUsesExactInstanceAndSingleCommand()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("start");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, MuMuObservedPayloads.AcceptedControlResponse);

        ApplicationResult<MuMuLifecycleCommandOutcome> result = fixture.Host.RequestMutation(
            installation,
            SecondInstance,
            MuMuLifecycleMutation.Start,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.ExitCode);

        RecordedProcessRequest recorded = Assert.Single(fixture.Runner.Requests);

        AssertArguments(["control", "--vmindex", "2", "launch"], recorded.Request.Arguments);
        Assert.Equal(installation.ControlExecutablePath, recorded.Request.ExecutablePath);

        // Ни один запрос не адресует все экземпляры: mutation выполняется только над запрошенной identity.
        Assert.DoesNotContain(
            fixture.Runner.Requests,
            request => request.Request.Arguments.Contains("all"));
    }

    [Fact(DisplayName = "Остановка выполняется ровно одной командой над точной identity")]
    public void StopMutationUsesExactInstanceAndSingleCommand()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("stop");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(0, MuMuObservedPayloads.AcceptedControlResponse);

        ApplicationResult<MuMuLifecycleCommandOutcome> result = fixture.Host.RequestMutation(
            installation,
            FirstInstance,
            MuMuLifecycleMutation.Stop,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        RecordedProcessRequest recorded = Assert.Single(fixture.Runner.Requests);

        AssertArguments(["control", "--vmindex", "1", "shutdown"], recorded.Request.Arguments);
    }

    [Fact(DisplayName = "Bounded output команды ограничен по длине")]
    public void MutationReturnsBoundedOutput()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("bounded");
        MuMuInstallation installation = fixture.Discover();

        string rawOutput = "{\"errcode\": 0, \"errmsg\": \"" + new string('x', 400) + "\"}";
        fixture.Runner.EnqueueOutcome(0, rawOutput);

        ApplicationResult<MuMuLifecycleCommandOutcome> result = fixture.Host.RequestMutation(
            installation,
            FirstInstance,
            MuMuLifecycleMutation.Start,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        MuMuLifecycleCommandOutcome outcome = result.Value!;

        Assert.True(rawOutput.Length > BoundedDiagnosticText.MaxLength);
        Assert.True(outcome.BoundedOutput.Length <= BoundedDiagnosticText.MaxLength);
        Assert.StartsWith("{\"errcode\": 0, \"errmsg\": \"", outcome.BoundedOutput, StringComparison.Ordinal);
        Assert.EndsWith("...", outcome.BoundedOutput, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Код выхода отказной mutation возвращается как evidence, а не как успех lifecycle")]
    public void MutationReturnsExitCodeAsEvidence()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("evidence");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(-7, UnknownProviderCodeResponse);

        ApplicationResult<MuMuLifecycleCommandOutcome> result = fixture.Host.RequestMutation(
            installation,
            FirstInstance,
            MuMuLifecycleMutation.Start,
            CancellationToken.None);

        // Host сообщает код выхода и bounded вывод; решение о terminal postcondition принимает Core.
        Assert.True(result.IsSuccess);
        Assert.Equal(-7, result.Value!.ExitCode);
        Assert.NotEmpty(result.Value!.BoundedOutput);
    }

    [Fact(DisplayName = "Mutation над несуществующим экземпляром даёт mumu_instance_not_found")]
    public void MutationOnMissingInstanceGivesInstanceNotFound()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("mutation-missing");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueOutcome(
            MuMuObservedPayloads.IndexNotFoundExitCode,
            MuMuObservedPayloads.IndexNotFoundResponse);

        ApplicationResult<MuMuLifecycleCommandOutcome> result = fixture.Host.RequestMutation(
            installation,
            MuMuInstanceId.FromIndex("5"),
            MuMuLifecycleMutation.Stop,
            CancellationToken.None);

        AssertFailure(result, ApplicationFailure.MuMuInstanceNotFound);
        Assert.Equal("mumu:5", result.FailureInfo!.Details![MuMuFailureDetailKeys.InstanceId]);
    }

    [Fact(DisplayName = "Дедлайн mutation пробрасывается как mumu_lifecycle_timeout")]
    public void MutationPropagatesProcessTimeout()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("mutation-timeout");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueFailure(MuMuPlatformFailureMapper.ForProcessTimeout(
            installation.ControlExecutablePath,
            TimeSpan.FromSeconds(1),
            0,
            0));

        ApplicationResult<MuMuLifecycleCommandOutcome> result = fixture.Host.RequestMutation(
            installation,
            FirstInstance,
            MuMuLifecycleMutation.Start,
            CancellationToken.None);

        AssertFailure(result, ApplicationFailure.MuMuLifecycleTimeout);
        Assert.True(result.FailureInfo!.IsRetryable);
    }

    [Fact(DisplayName = "Отмена mutation сообщается кодом отмены и доходит до границы процесса")]
    public void MutationPropagatesCancellation()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("mutation-cancelled");
        MuMuInstallation installation = fixture.Discover();
        fixture.Runner.EnqueueFailure(MuMuPlatformFailureMapper.ForCancellation());

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        ApplicationResult<MuMuLifecycleCommandOutcome> result = fixture.Host.RequestMutation(
            installation,
            FirstInstance,
            MuMuLifecycleMutation.Start,
            cancellation.Token);

        AssertFailure(result, ApplicationFailure.OperationCancelled);
        Assert.Equal(cancellation.Token, Assert.Single(fixture.Runner.Requests).CancellationToken);
    }

    [Fact(DisplayName = "Неизвестный host-примитив mutation — ошибка программирования")]
    public void UndefinedMutationIsProgrammingError()
    {
        HostFixture fixture = new();
        _ = fixture.AddInstallation("undefined-mutation");
        MuMuInstallation installation = fixture.Discover();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Host.RequestMutation(
            installation,
            FirstInstance,
            (MuMuLifecycleMutation)42,
            CancellationToken.None));

        // Ошибка программирования обнаружена до запуска процесса: mutation не выполнена.
        Assert.Empty(fixture.Runner.Requests);
    }

    // --- Контракт ---

    [Fact(DisplayName = "Host реализует IMuMuHost ровно в зафиксированной форме")]
    public void HostImplementsExactContract()
    {
        string[] interfaceMethods =
            [.. typeof(IMuMuHost).GetMethods().Select(static method => method.Name).OrderBy(static name => name, StringComparer.Ordinal)];

        Assert.Equal(4, interfaceMethods.Length);
        Assert.Equal("DiscoverInstallation", interfaceMethods[0]);
        Assert.Equal("EnumerateInstances", interfaceMethods[1]);
        Assert.Equal("ObserveInstanceState", interfaceMethods[2]);
        Assert.Equal("RequestMutation", interfaceMethods[3]);

        Assert.True(typeof(IMuMuHost).IsAssignableFrom(typeof(MuMuWindowsHost)));
    }

    [Fact(DisplayName = "Host не объявляет ветку перезапуска и capability-флаг нативного restart")]
    public void HostHasNoRestartBranchOrCapabilityFlag()
    {
        MemberInfo[] members = typeof(MuMuWindowsHost).GetMembers(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(
            members,
            member => member.Name.Contains("Restart", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            members,
            member => member.Name.Contains("Capabilit", StringComparison.OrdinalIgnoreCase));

        // Перечень host-примитивов закрыт запуском и остановкой: перезапуск выражается их композицией.
        MuMuLifecycleMutation[] mutations = [.. Enum.GetValues<MuMuLifecycleMutation>()];

        Assert.Equal(2, mutations.Length);
        Assert.Equal(MuMuLifecycleMutation.Start, mutations[0]);
        Assert.Equal(MuMuLifecycleMutation.Stop, mutations[1]);
    }

    [Fact(DisplayName = "Неположительный дедлайн команды — ошибка программирования")]
    public void NonPositiveCommandTimeoutIsProgrammingError()
        => _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MuMuWindowsHost(
            new FakeMuMuInstallationRegistrySource(),
            new FakeMuMuInstallMetadataSource(),
            new FakeMuMuFileSystemProbe(),
            new FakeWindowsProcessRunner(),
            TimeSpan.Zero,
            NullLogger<MuMuWindowsHost>.Instance));

    [Fact(DisplayName = "Обязательные зависимости и установка host-а не могут быть null")]
    public void NullArgumentsAreProgrammingErrors()
    {
        FakeMuMuInstallationRegistrySource registry = new();
        FakeMuMuInstallMetadataSource metadata = new();
        FakeMuMuFileSystemProbe fileSystem = new();
        FakeWindowsProcessRunner runner = new();
        NullLogger<MuMuWindowsHost> logger = NullLogger<MuMuWindowsHost>.Instance;

        _ = Assert.Throws<ArgumentNullException>(
            () => new MuMuWindowsHost(null!, metadata, fileSystem, runner, logger));
        _ = Assert.Throws<ArgumentNullException>(
            () => new MuMuWindowsHost(registry, null!, fileSystem, runner, logger));
        _ = Assert.Throws<ArgumentNullException>(
            () => new MuMuWindowsHost(registry, metadata, null!, runner, logger));
        _ = Assert.Throws<ArgumentNullException>(
            () => new MuMuWindowsHost(registry, metadata, fileSystem, null!, logger));
        _ = Assert.Throws<ArgumentNullException>(
            () => new MuMuWindowsHost(registry, metadata, fileSystem, runner, null!));

        HostFixture fixture = new();

        _ = Assert.Throws<ArgumentNullException>(() => fixture.Host.EnumerateInstances(null!));
        _ = Assert.Throws<ArgumentNullException>(() => fixture.Host.ObserveInstanceState(null!, FirstInstance));
        _ = Assert.Throws<ArgumentNullException>(
            () => fixture.Host.RequestMutation(null!, FirstInstance, MuMuLifecycleMutation.Start, CancellationToken.None));
    }

    private static void AssertArguments(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index], actual[index]);
        }
    }

    private static void AssertFailure<T>(ApplicationResult<T> result, string expectedCode)
    {
        Assert.True(result.IsFailure, "Ожидался ожидаемый отказ, но результат успешен.");

        // Details проверяются отдельно: отмена — ожидаемый исход без структурированных details.
        Assert.Equal(expectedCode, result.FailureInfo!.Code);
    }

    /// <summary>Host, собранный на подменяемых внешних границах.</summary>
    private sealed class HostFixture
    {
        internal HostFixture(ILogger<MuMuWindowsHost>? logger = null)
        {
            Host = new MuMuWindowsHost(
                Registry,
                Metadata,
                FileSystem,
                Runner,
                logger ?? NullLogger<MuMuWindowsHost>.Instance);
        }

        internal FakeMuMuInstallationRegistrySource Registry { get; } = new();

        internal FakeMuMuInstallMetadataSource Metadata { get; } = new();

        internal FakeMuMuFileSystemProbe FileSystem { get; } = new();

        internal FakeWindowsProcessRunner Runner { get; } = new();

        internal MuMuWindowsHost Host { get; }

        internal string AddInstallation(
            string leaf,
            string? registryVersion = MuMuTestInstallation.Version,
            bool withMetadata = true,
            bool withControlExecutable = true)
            => MuMuTestInstallation.Add(
                Registry,
                Metadata,
                FileSystem,
                leaf,
                registryVersion,
                withMetadata,
                withControlExecutable);

        internal MuMuInstallation Discover()
        {
            ApplicationResult<MuMuInstallation> result = Host.DiscoverInstallation();

            Assert.True(result.IsSuccess, "Ожидалось успешное обнаружение установки.");

            return result.Value!;
        }
    }
}
