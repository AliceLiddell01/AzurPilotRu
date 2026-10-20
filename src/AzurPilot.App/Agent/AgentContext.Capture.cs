using System.Globalization;
using AzurPilot.App.Capture;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows.Capture;

namespace AzurPilot.App.Agent;

internal sealed partial class AgentContext
{
    private static readonly TimeSpan ProbeShutdownReserve = TimeSpan.FromSeconds(3);

    /// <summary>Значения собственных аргументов команды после проверки типов.</summary>
    internal IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Общий срок вызова.</summary>
    internal int TimeoutMilliseconds { get; init; } = AgentRequest.DefaultTimeoutMilliseconds;

    /// <summary>Явный --config вызова; worker-ы получают тот же источник.</summary>
    internal string? ConfigurationPath { get; init; }

    internal async Task<AgentPayload> CaptureMethodsAsync(CancellationToken token)
    {
        ApplicationResult<CaptureMeasurementTarget> target = Service.ResolveCaptureTarget(Target!.Value);
        if (target.IsFailure)
        {
            return new(null, target.FailureInfo);
        }

        ScreenshotCaptureRouter? router = Service.ScreenshotRouter;
        ScreenshotConfiguration settings = Service.Configuration.Configuration.Screenshot;
        object? calibration = null;
        string? fingerprintFailure = null;
        if (router is not null)
        {
            ApplicationResult<string> fingerprint = await router.FingerprintAsync(target.Value!.InstanceId,
                target.Value.Endpoint, token).ConfigureAwait(false);
            if (fingerprint.IsSuccess)
            {
                calibration = router.Calibration.Find(fingerprint.Value!);
            }
            else
            {
                fingerprintFailure = fingerprint.FailureInfo!.Code;
            }
        }

        IReadOnlyList<CaptureDependencyInfo> dependencies = CaptureDependencies.Describe();
        return new(new
        {
            setting = new
            {
                method = settings.Method,
                normalIntervalMs = settings.NormalIntervalMilliseconds,
                combatIntervalMs = settings.CombatIntervalMilliseconds,
                allowed = ScreenshotMethodNames.DescribeAllowedSettings(),
            },
            environment = CaptureBenchmark.Environment(target.Value!),
            calibration,
            fingerprintFailure,
            methods = CaptureCompatibility.All.Select(m => new
            {
                method = ScreenshotMethodNames.ToName(m.Method),
                m.Delivery,
                m.Format,
                m.Requirements,
                m.Limitations,
                dependencies = m.Dependencies.Select(id => dependencies.FirstOrDefault(d => d.Id == id)
                    ?? new CaptureDependencyInfo(id, string.Empty, string.Empty, string.Empty, string.Empty, "dependency_not_declared")),
            }),
        });
    }

    internal async Task<AgentPayload> CaptureProbeAsync(CancellationToken token)
    {
        Stage = CaptureWorker.ProbeCommand;
        if (!Options.TryGetValue(AgentCaptureArguments.Method.Name, out string? value)
            || !ScreenshotMethodNames.TryParse(value, out ScreenshotMethod method))
        {
            return new(null, MissingArgument(AgentCaptureArguments.Method.Name));
        }

        ApplicationResult<CaptureMeasurementTarget> target = Service.ResolveCaptureTarget(Target!.Value);
        if (target.IsFailure || Service.ScreenshotRouter is not ScreenshotCaptureRouter router)
        {
            return new(null, target.FailureInfo ?? CaptureRouterUnavailable());
        }

        int samples = Options.TryGetValue(AgentCaptureArguments.Samples.Name, out string? count)
            ? int.Parse(count, NumberStyles.None, CultureInfo.InvariantCulture) : CaptureBenchmark.ProfileSamples["short"];
        TimeSpan budget = TimeSpan.FromMilliseconds(TimeoutMilliseconds) - ProbeShutdownReserve;
        ApplicationResult<CaptureProbeResult> result = await Service.WithCaptureLeaseAsync(Target!.Value, async () =>
            ApplicationResult<CaptureProbeResult>.Success(await CaptureProbe.RunAsync(router, target.Value!.InstanceId,
                target.Value.Endpoint, method, samples, CaptureProbe.DefaultWarmup, budget, token).ConfigureAwait(false)),
            token).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return new(null, result.FailureInfo);
        }

        CaptureProbeResult probe = result.Value!;
        Effect = method == ScreenshotMethod.Adb ? "none" : "mayHaveChanged";
        ApplicationFailure? failure = probe.Verdict == CaptureVerdictNames.ToName(CaptureVerdict.Pass) ? null : new ApplicationFailure
        {
            Code = probe.Verdict == CaptureVerdictNames.ToName(CaptureVerdict.Unsupported)
                ? ApplicationFailure.ScreenshotMethodUnsupported : ApplicationFailure.AndroidScreenshotCaptureFailed,
            Message = "Серия транспорта не доказала корректность и стабильность.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = probe.Reason ?? "unknown",
                ["method"] = probe.Method,
                ["verdict"] = probe.Verdict,
            },
        };
        return new(probe, failure, Effect);
    }

    internal async Task<AgentPayload> CaptureBenchmarkAsync(CancellationToken token)
    {
        Stage = CaptureBenchmark.Topic;
        ApplicationResult<CaptureMeasurementTarget> target = Service.ResolveCaptureTarget(Target!.Value);
        if (target.IsFailure)
        {
            return new(null, target.FailureInfo);
        }

        string profile = Options.GetValueOrDefault(AgentCaptureArguments.Profile.Name) ?? "short";
        int samples = Options.TryGetValue(AgentCaptureArguments.Samples.Name, out string? count)
            ? int.Parse(count, NumberStyles.None, CultureInfo.InvariantCulture) : CaptureBenchmark.ProfileSamples[profile];
        CaptureBenchmarkRequest request = new(
            AgentCaptureArguments.ParseMethods(Options.GetValueOrDefault(AgentCaptureArguments.Methods.Name)),
            profile, samples,
            Options.GetValueOrDefault(AgentCaptureArguments.Calibrate.Name) != "false",
            Options.GetValueOrDefault(AgentCaptureArguments.DebugOverhead.Name) == "true",
            TimeSpan.FromMilliseconds(TimeoutMilliseconds), ConfigurationPath);
        CaptureCommandOutcome outcome = await CaptureBenchmark.RunAsync(Service, target.Value!, request, token).ConfigureAwait(false);
        Effect = outcome.Effect;
        return new(outcome.Data, outcome.Failure, outcome.Effect);
    }

    internal async Task<AgentPayload> CaptureIntervalsAsync(CancellationToken token)
    {
        Stage = CaptureIntervals.Topic;
        if (!Options.TryGetValue(AgentCaptureArguments.Scene.Name, out string? scene))
        {
            return new(null, MissingArgument(AgentCaptureArguments.Scene.Name));
        }

        if (!Options.TryGetValue(AgentCaptureArguments.Session.Name, out string? session))
        {
            return new(null, MissingArgument(AgentCaptureArguments.Session.Name));
        }

        bool combat = scene == "combat";
        string? confirmation = Options.GetValueOrDefault(AgentCaptureArguments.Confirm.Name);
        if (combat != confirmation is not null)
        {
            // Код нужен только фазе combat и только ей; в normal он означал бы подтверждение до измерения.
            return new(null, MissingArgument(AgentCaptureArguments.Confirm.Name));
        }

        ApplicationResult<CaptureMeasurementTarget> target = Service.ResolveCaptureTarget(Target!.Value);
        if (target.IsFailure)
        {
            return new(null, target.FailureInfo);
        }

        ScreenshotMethod? method = Options.TryGetValue(AgentCaptureArguments.IntervalMethod.Name, out string? name)
            && ScreenshotMethodNames.TryParse(name, out ScreenshotMethod parsed) ? parsed : null;
        ScreenshotIntervalScene intervalScene = combat ? ScreenshotIntervalScene.Combat : ScreenshotIntervalScene.Normal;
        ScreenshotConfiguration settings = Service.Configuration.Configuration.Screenshot;
        IReadOnlyList<double>? grid = null;
        if (Options.TryGetValue(AgentCaptureArguments.Grid.Name, out string? values))
        {
            try
            {
                // Сетка проверяется до наблюдения и захвата: отказ аргумента не должен следовать за действиями.
                grid = ScreenshotIntervalAnalysis.NormalizeGrid(AgentCaptureArguments.ParseGrid(values), intervalScene,
                    combat ? settings.CombatIntervalMilliseconds : settings.NormalIntervalMilliseconds);
            }
            catch (ArgumentException)
            {
                return new(null, MissingArgument(AgentCaptureArguments.Grid.Name));
            }
        }

        CaptureIntervalRequest request = new(intervalScene, session, confirmation, grid, method,
            TimeSpan.FromMilliseconds(TimeoutMilliseconds), settings,
            CaptureConfigurationSource.For(ConfigurationPath).YamlPath, Service.Configuration.SourceVersion);
        CaptureCommandOutcome outcome = await CaptureIntervals.RunAsync(Service, target.Value!, request, token)
            .ConfigureAwait(false);
        Effect = outcome.Effect;
        return new(outcome.Data, outcome.Failure, outcome.Effect);
    }

    internal async Task<AgentPayload> CaptureProfileApplyAsync(CancellationToken token)
    {
        Stage = CaptureProfileApply.Command;
        if (!Options.TryGetValue(AgentCaptureArguments.ApplySession.Name, out string? session))
        {
            return new(null, MissingArgument(AgentCaptureArguments.ApplySession.Name));
        }

        if (!Options.TryGetValue(AgentCaptureArguments.IntervalProfile.Name, out string? profile))
        {
            return new(null, MissingArgument(AgentCaptureArguments.IntervalProfile.Name));
        }

        ApplicationResult<CaptureMeasurementTarget> target = Service.ResolveCaptureTarget(Target!.Value);
        if (target.IsFailure)
        {
            return new(null, target.FailureInfo);
        }

        CaptureCommandOutcome outcome = await CaptureProfileApply.RunAsync(Service, target.Value!,
            CaptureConfigurationSource.For(ConfigurationPath), session, profile,
            Options.GetValueOrDefault(AgentCaptureArguments.ApplyConfirm.Name), token).ConfigureAwait(false);
        Effect = outcome.Effect;
        return new(outcome.Data, outcome.Failure, outcome.Effect);
    }

    private static ApplicationFailure MissingArgument(string name) => new()
    {
        Code = ApplicationFailure.InvalidArgument,
        Message = "Недопустимый машинный запрос. Используйте agent discover для описания команд и аргументов.",
        Details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["argument"] = name,
            [ApplicationFailure.MutationAttemptedDetailKey] = "false",
        },
    };

    private static ApplicationFailure CaptureRouterUnavailable() => new()
    {
        Code = ApplicationFailure.AndroidScreenshotCaptureFailed,
        Message = "Production-маршрутизатор захвата недоступен.",
        Details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = "capture_router_unavailable",
            [ApplicationFailure.MutationAttemptedDetailKey] = "false",
        },
    };
}
