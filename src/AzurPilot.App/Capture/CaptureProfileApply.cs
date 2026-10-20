using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurPilot.App.OperatorServices;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Capture;

namespace AzurPilot.App.Capture;

/// <summary>Наблюдаемые условия target и конфигурации на момент preview или apply.</summary>
internal sealed record CaptureProfileEnvironment(
    string InstanceId, string Endpoint, string Fingerprint, string MuMuVersion, int Width, int Height,
    string Method, string? FallbackFrom, string? CalibrationRunId, string ConfigurationPath,
    AzurPilotConfigurationFormat ConfigurationFormat, string? ConfigurationSha256, ScreenshotConfiguration Screenshot);

/// <summary>Загруженная сессия интервалов: состояние и результаты фаз с их итогами из отчётов.</summary>
internal sealed record CaptureProfileSession(CaptureIntervalSessionState State,
    IReadOnlyList<ScreenshotIntervalResult>? Normal, string? NormalReportVerdict,
    IReadOnlyList<ScreenshotIntervalResult>? Combat, string? CombatReportVerdict);

/// <summary>Значения, которые получит конфигурация.</summary>
/// <param name="Profile">Имя профиля.</param>
/// <param name="NormalMs">Новый screenshot.normalIntervalMs.</param>
/// <param name="CombatMs">Новый screenshot.combatIntervalMs либо null — значение не меняется.</param>
/// <param name="MeasuredNormalMs">Измеренный устойчивый интервал до округления до целых миллисекунд.</param>
/// <param name="MeasuredCombatMs">То же для боя.</param>
/// <param name="Combat">Решение по боевому интервалу.</param>
internal sealed record CaptureProfilePlan(string Profile, int NormalMs, int? CombatMs, double? MeasuredNormalMs,
    double? MeasuredCombatMs, string Combat);

/// <summary>Ожидающее подтверждения применение; один код — одна попытка.</summary>
internal sealed record CaptureProfilePreview(
    [property: JsonPropertyName("formatVersion")] int FormatVersion,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("configurationPath")] string ConfigurationPath,
    [property: JsonPropertyName("configurationSha256")] string? ConfigurationSha256,
    [property: JsonPropertyName("normalIntervalMs")] int NormalMs,
    [property: JsonPropertyName("combatIntervalMs")] int? CombatMs,
    [property: JsonPropertyName("issuedAtUtc")] DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("consumed")] bool Consumed);

/// <summary>
/// Применение профиля интервалов из завершённой сессии <c>capture-intervals</c>: без кода — preview с текущими и
/// новыми значениями и одноразовым кодом; с кодом — повторная проверка условий и точечная запись секции
/// <c>screenshot</c>. Код выдаётся пользователю, а передаётся агентом только после его явного согласия.
/// </summary>
internal static class CaptureProfileApply
{
    internal const string Command = "capture-profile-apply";
    internal const string PreviewFile = "apply.json";
    internal const string Current = "current";
    internal static readonly TimeSpan ResultsLifetime = TimeSpan.FromHours(24);
    internal static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    internal static readonly IReadOnlyList<string> Profiles = ["fast", "balanced", "low_load", Current];
    private const string PassVerdict = "PASS";
    private const string CombatVerdict = "UNVERIFIED_SCENE";

    internal static async Task<CaptureCommandOutcome> RunAsync(AzurPilotOperatorService service,
        CaptureMeasurementTarget target, string session, string profile, string? confirmation,
        CancellationToken cancellationToken)
    {
        CaptureReportWriter? writer = CaptureReportWriter.OpenExisting(CaptureIntervals.Topic, session, out string? missing);
        if (writer is null)
        {
            return Rejected(missing!);
        }

        ApplicationResult<CaptureCommandOutcome> outcome = await service.WithCaptureLeaseAsync(target.InstanceId, async () =>
        {
            ApplicationResult<CaptureProfileEnvironment> observed = await ObserveAsync(service, target, cancellationToken)
                .ConfigureAwait(false);
            if (observed.IsFailure)
            {
                return ApplicationResult<CaptureCommandOutcome>.Success(new(new { status = "BLOCKED" }, observed.FailureInfo, "none"));
            }

            CaptureProfileEnvironment environment = observed.Value!;
            string effect = environment.Method == ScreenshotMethodNames.ToName(ScreenshotMethod.Adb) ? "none" : "mayHaveChanged";
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CaptureProfileSession? loaded = Load(writer.Directory);
            (CaptureProfilePlan? plan, string? rejection) = loaded is null ? (null, "session_results_invalid")
                : Evaluate(loaded, environment, profile, now);
            if (confirmation is null)
            {
                if (plan is null)
                {
                    return ApplicationResult<CaptureCommandOutcome>.Success(Rejected(rejection!) with { Effect = effect });
                }

                CaptureProfilePreview preview = Issue(writer.Directory, plan, environment, now);
                return ApplicationResult<CaptureCommandOutcome>.Success(new(Describe("PREVIEW", session, writer, loaded!, plan,
                    environment, preview), null, effect));
            }

            string? redeemed = Redeem(writer.Directory, confirmation, profile, environment, plan, rejection, now);
            if (redeemed is not null)
            {
                return ApplicationResult<CaptureCommandOutcome>.Success(Rejected(redeemed) with { Effect = effect });
            }

            return ApplicationResult<CaptureCommandOutcome>.Success(Write(service, session, writer, loaded!, plan!, environment));
        }, cancellationToken).ConfigureAwait(false);
        return outcome.IsSuccess ? outcome.Value! : new(new { status = "BLOCKED" }, outcome.FailureInfo, "none");
    }

    /// <summary>Проверяет привязку сессии к наблюдаемым условиям и строит план профиля.</summary>
    internal static (CaptureProfilePlan? Plan, string? Rejection) Evaluate(CaptureProfileSession session,
        CaptureProfileEnvironment environment, string profile, DateTimeOffset now)
    {
        string? binding = CheckBinding(session.State, environment, now);
        return binding is not null ? (null, binding) : Plan(session, profile);
    }

    internal static string? CheckBinding(CaptureIntervalSessionState state, CaptureProfileEnvironment environment,
        DateTimeOffset now)
    {
        if (state.FormatVersion < CaptureIntervals.StateFormatVersion || state.Binding is not CaptureIntervalBinding bound)
        {
            // Сессия записана до появления привязки к окружению: применимость результатов не доказать.
            return "session_binding_missing";
        }

        return now < state.IssuedAtUtc || now - state.IssuedAtUtc > ResultsLifetime ? "session_expired"
            : state.InstanceId != environment.InstanceId || state.Endpoint != environment.Endpoint ? "target_mismatch"
            : bound.MuMuVersion != environment.MuMuVersion ? "emulator_version_changed"
            : bound.Fingerprint != environment.Fingerprint ? "environment_changed"
            : bound.Width != environment.Width || bound.Height != environment.Height ? "resolution_changed"
            : state.Method != environment.Method || environment.FallbackFrom is not null ? "method_changed"
            : bound.CalibrationRunId is not null && bound.CalibrationRunId != environment.CalibrationRunId ? "calibration_changed"
            : !string.Equals(bound.ConfigurationPath, environment.ConfigurationPath, StringComparison.OrdinalIgnoreCase)
                ? "configuration_path_mismatch"
            : environment.ConfigurationFormat == AzurPilotConfigurationFormat.Json ? "configuration_legacy_json"
            : bound.Screenshot != environment.Screenshot ? "configuration_changed"
            : null;
    }

    /// <summary>
    /// fast/balanced/low_load берутся только из повторно проверенных устойчивых точек; <c>current</c> восстанавливает
    /// значения, действовавшие при измерении. Неизмеренный или незавершённый бой оставляет боевой интервал как есть.
    /// </summary>
    internal static (CaptureProfilePlan? Plan, string? Rejection) Plan(CaptureProfileSession session, string profile)
    {
        CaptureIntervalSessionState state = session.State;
        CaptureIntervalBinding bound = state.Binding!;
        if (state.NormalVerdict != PassVerdict || session.NormalReportVerdict != PassVerdict)
        {
            return (null, "normal_phase_incomplete");
        }

        if (session.Normal is not { Count: > 0 } normal || normal.Any(result => result.Stable && !IsProvenStable(result)))
        {
            return (null, "session_results_invalid");
        }

        bool combatUsable = state.CombatCompleted && state.CombatVerdict == CombatVerdict
            && session.CombatReportVerdict == CombatVerdict && session.Combat is { Count: > 0 };
        if (combatUsable && session.Combat!.Any(result => result.Stable && !IsProvenStable(result)))
        {
            return (null, "session_results_invalid");
        }

        string combatDecision = !state.CombatCompleted ? "not_measured" : !combatUsable ? "phase_incomplete" : "measured";
        if (profile == Current)
        {
            return (new(profile, bound.Screenshot.NormalIntervalMilliseconds, bound.Screenshot.CombatIntervalMilliseconds,
                null, null, "restore"), null);
        }

        ScreenshotIntervalProfile? recommended = ScreenshotIntervalAnalysis.RecommendProfiles(normal,
                combatUsable ? session.Combat : null, bound.Screenshot.NormalIntervalMilliseconds,
                bound.Screenshot.CombatIntervalMilliseconds)
            .FirstOrDefault(candidate => candidate.Name == profile);
        if (recommended?.NormalMilliseconds is not double normalMs)
        {
            return (null, "profile_unavailable");
        }

        if (!IsStablePoint(normal, normalMs))
        {
            return (null, "session_results_invalid");
        }

        double? combatMs = combatUsable ? recommended.CombatMilliseconds : null;
        if (combatMs is double measuredCombat && !IsStablePoint(session.Combat!, measuredCombat))
        {
            return (null, "session_results_invalid");
        }

        int normalValue = Milliseconds(normalMs);
        int? combatValue = combatMs is double value ? Milliseconds(value) : null;
        if (normalValue is < ScreenshotConfiguration.MinimumIntervalMilliseconds or > ScreenshotConfiguration.MaximumNormalIntervalMilliseconds
            || combatValue is < ScreenshotConfiguration.MinimumIntervalMilliseconds or > ScreenshotConfiguration.MaximumCombatIntervalMilliseconds)
        {
            return (null, "session_results_invalid");
        }

        return (new(profile, normalValue, combatValue, normalMs, combatMs,
            combatUsable && combatMs is null ? "no_stable_interval" : combatDecision), null);
    }

    /// <summary>Повторная проверка порога устойчивости по сводке: отчёт не принимается на веру.</summary>
    internal static bool IsProvenStable(ScreenshotIntervalResult result)
    {
        double interval = result.RequestedIntervalMilliseconds;
        return result.Error is null && double.IsFinite(interval) && interval > 0
            && result.Frames >= ScreenshotIntervalAnalysis.MinimumFrames
            && result.TargetAchievementRatio >= 0.90 && result.DeadlineMissRatio <= 0.10
            && result.StartToStart.P95 <= interval + ScreenshotIntervalAnalysis.ToleranceMilliseconds(interval);
    }

    internal static CaptureProfileSession? Load(string directory)
    {
        CaptureIntervalSessionState? state = CaptureIntervals.ReadState(Path.Combine(directory, CaptureIntervals.StateFile));
        if (state is null)
        {
            return null;
        }

        string combatPath = Path.Combine(directory, CaptureIntervals.CombatFile);
        return new(state, CaptureIntervals.ReadResults(Path.Combine(directory, CaptureIntervals.NormalFile)),
            ReadVerdict(Path.Combine(directory, CaptureIntervals.NormalFile)),
            File.Exists(combatPath) ? CaptureIntervals.ReadResults(combatPath) : null, ReadVerdict(combatPath));
    }

    /// <summary>Выдаёт новый код; прежний ожидающий код этой сессии перестаёт действовать.</summary>
    internal static CaptureProfilePreview Issue(string directory, CaptureProfilePlan plan,
        CaptureProfileEnvironment environment, DateTimeOffset now)
    {
        CaptureProfilePreview preview = new(1, RandomNumberGenerator.GetString(CaptureIntervals.CodeAlphabet, 8), plan.Profile,
            environment.ConfigurationPath, environment.ConfigurationSha256, plan.NormalMs, plan.CombatMs, now,
            now + PreviewLifetime, false);
        string path = Path.Combine(directory, PreviewFile);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(preview));
        File.Move(temporary, path, overwrite: true);
        return preview;
    }

    /// <summary>
    /// Погашает код под исключительной блокировкой файла: любая предъявленная попытка, успешная или нет, делает код
    /// недействительным. Возвращает причину отказа либо null, если запись разрешена.
    /// </summary>
    internal static string? Redeem(string directory, string code, string profile, CaptureProfileEnvironment environment,
        CaptureProfilePlan? plan, string? rejection, DateTimeOffset now)
    {
        string path = Path.Combine(directory, PreviewFile);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            CaptureProfilePreview? preview;
            try
            {
                preview = JsonSerializer.Deserialize<CaptureProfilePreview>(stream);
            }
            catch (JsonException)
            {
                return "preview_invalid";
            }

            if (preview is null)
            {
                return "preview_invalid";
            }

            if (preview.Consumed)
            {
                return "confirmation_used";
            }

            string? reason = now > preview.ExpiresAtUtc ? "confirmation_expired"
                : !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(preview.Code),
                    System.Text.Encoding.ASCII.GetBytes(code)) ? "confirmation_mismatch"
                : preview.Profile != profile ? "profile_mismatch"
                : !string.Equals(preview.ConfigurationPath, environment.ConfigurationPath, StringComparison.OrdinalIgnoreCase)
                    ? "configuration_path_mismatch"
                : preview.ConfigurationSha256 != environment.ConfigurationSha256 ? "configuration_changed_since_preview"
                : rejection
                ?? (plan is null || plan.NormalMs != preview.NormalMs || plan.CombatMs != preview.CombatMs ? "plan_changed" : null);
            stream.SetLength(0);
            JsonSerializer.Serialize(stream, preview with { Consumed = true });
            return reason;
        }
        catch (FileNotFoundException)
        {
            return "preview_not_found";
        }
        catch (IOException)
        {
            // Другой процесс держит код: параллельное применение не допускается.
            return "preview_busy";
        }
        catch (UnauthorizedAccessException)
        {
            return "preview_invalid";
        }
    }

    /// <summary>Независимая проверка записанного файла: значения профиля и неизменность остальных настроек.</summary>
    internal static string? Verify(AzurPilotConfiguration before, AzurPilotConfigurationSnapshot reread, CaptureProfilePlan plan)
    {
        ScreenshotConfiguration expected = before.Screenshot with
        {
            NormalIntervalMilliseconds = plan.NormalMs,
            CombatIntervalMilliseconds = plan.CombatMs ?? before.Screenshot.CombatIntervalMilliseconds,
        };
        return reread.Configuration.Screenshot != expected ? "applied_values_mismatch"
            : reread.Configuration != before with { Screenshot = expected } ? "other_settings_changed"
            : null;
    }

    private static CaptureCommandOutcome Write(AzurPilotOperatorService service, string session, CaptureReportWriter writer,
        CaptureProfileSession loaded, CaptureProfilePlan plan, CaptureProfileEnvironment environment)
    {
        (string yamlPath, string jsonPath) = service.ConfigurationFilePaths();
        ApplicationResult<AzurPilotConfigurationSnapshot> before = AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);
        ApplicationResult<AzurPilotConfigurationSnapshot> saved = service.SaveScreenshotIntervals(environment.ConfigurationSha256,
            plan.NormalMs, plan.CombatMs, out string? backupPath);
        if (before.IsFailure || saved.IsFailure)
        {
            ApplicationFailure cause = (before.FailureInfo ?? saved.FailureInfo)!;
            return new(new { status = "FAILED", reason = "configuration_write_failed" }, new ApplicationFailure
            {
                Code = cause.Code,
                Message = cause.Message,
                Details = new Dictionary<string, string>(cause.Details ?? new Dictionary<string, string>(), StringComparer.Ordinal)
                {
                    ["reason"] = "configuration_write_failed",
                    [ApplicationFailure.MutationAttemptedDetailKey] = "true",
                },
            }, "none");
        }

        ApplicationResult<AzurPilotConfigurationSnapshot> reread = AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);
        string? mismatch = reread.IsFailure ? "reread_failed" : Verify(before.Value!.Configuration, reread.Value!, plan);
        object data = Describe(mismatch is null ? "APPLIED" : "FAILED", session, writer, loaded, plan, environment, null,
            new
            {
                backupPath,
                configurationSha256 = reread.Value?.SourceVersion,
                verification = mismatch ?? "reread_values_match",
                note = "Значения действуют для процессов, запущенных после записи.",
            });
        writer.WriteJson("apply-result.json", data);
        return new(data, mismatch is null ? null : new ApplicationFailure
        {
            Code = ApplicationFailure.ConfigurationInvalid,
            Message = "Записанная конфигурация не совпала с планом профиля; проверьте файл и резервную копию.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = mismatch,
                [ApplicationFailure.MutationAttemptedDetailKey] = "true",
            },
        }, "changed");
    }

    private static object Describe(string status, string session, CaptureReportWriter writer, CaptureProfileSession loaded,
        CaptureProfilePlan plan, CaptureProfileEnvironment environment, CaptureProfilePreview? preview, object? applied = null)
    {
        CaptureIntervalBinding bound = loaded.State.Binding!;
        ScreenshotConfiguration current = environment.Screenshot;
        int combatAfter = plan.CombatMs ?? current.CombatIntervalMilliseconds;
        return new
        {
            status,
            session,
            report = writer.RelativeDirectory,
            profile = plan.Profile,
            configurationPath = environment.ConfigurationPath,
            configurationSha256 = environment.ConfigurationSha256,
            current = new
            {
                method = current.Method,
                normalIntervalMs = current.NormalIntervalMilliseconds,
                combatIntervalMs = current.CombatIntervalMilliseconds,
            },
            proposed = new { method = current.Method, normalIntervalMs = plan.NormalMs, combatIntervalMs = combatAfter },
            changes = new[]
            {
                ("screenshot.normalIntervalMs", current.NormalIntervalMilliseconds, plan.NormalMs),
                ("screenshot.combatIntervalMs", current.CombatIntervalMilliseconds, combatAfter),
            }.Where(change => change.Item2 != change.Item3).Select(change => new { key = change.Item1, from = change.Item2, to = change.Item3 }),
            measured = new { normalMs = plan.MeasuredNormalMs, combatMs = plan.MeasuredCombatMs },
            combat = plan.Combat,
            binding = new
            {
                method = loaded.State.Method,
                bound.Width,
                bound.Height,
                bound.MuMuVersion,
                bound.CalibrationRunId,
                measuredAtUtc = loaded.State.IssuedAtUtc,
            },
            confirmation = preview is null ? null : new
            {
                preview.Code,
                preview.ExpiresAtUtc,
                instruction = "Передавайте код только после явного согласия пользователя на запись этих значений; "
                    + "код одноразовый, изменение файла или условий требует нового preview.",
            },
            applied,
        };
    }

    private static async Task<ApplicationResult<CaptureProfileEnvironment>> ObserveAsync(AzurPilotOperatorService service,
        CaptureMeasurementTarget target, CancellationToken cancellationToken)
    {
        if (service.ScreenshotRouter is not ScreenshotCaptureRouter router)
        {
            return Failure("capture_router_unavailable");
        }

        ApplicationResult<string> fingerprint = await router.FingerprintAsync(target.InstanceId, target.Endpoint,
            cancellationToken).ConfigureAwait(false);
        if (fingerprint.IsFailure)
        {
            return ApplicationResult<CaptureProfileEnvironment>.Failure(fingerprint.FailureInfo!);
        }

        // Тот же production-путь, что у observe/Dock: выбор транспорта и его откат наблюдаются, как у потребителей.
        ApplicationResult<CapturedAndroidScreenshot> frame = await router.CaptureScreenshotAsync(target.InstanceId,
            target.Endpoint, cancellationToken).ConfigureAwait(false);
        if (frame.IsFailure)
        {
            return ApplicationResult<CaptureProfileEnvironment>.Failure(frame.FailureInfo!);
        }

        (int width, int height) = ((int)frame.Value!.Info.FrameInfo.Width, (int)frame.Value.Info.FrameInfo.Height);
        frame.Value.Dispose();
        ScreenshotSelection selection = router.LastSelection!;
        (string yamlPath, string jsonPath) = service.ConfigurationFilePaths();
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration = AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);
        if (configuration.IsFailure)
        {
            return ApplicationResult<CaptureProfileEnvironment>.Failure(configuration.FailureInfo!);
        }

        return ApplicationResult<CaptureProfileEnvironment>.Success(new(target.InstanceId.ToString(), target.Endpoint.ToString(),
            fingerprint.Value!, target.MuMuVersion, width, height, ScreenshotMethodNames.ToName(selection.Method),
            selection.FallbackFrom is ScreenshotMethod fallback ? ScreenshotMethodNames.ToName(fallback) : null,
            router.Calibration.Find(fingerprint.Value!)?.RunId, yamlPath, configuration.Value!.SourceFormat,
            configuration.Value.SourceVersion, configuration.Value.Configuration.Screenshot));
    }

    private static int Milliseconds(double value) => (int)Math.Ceiling(value - 1e-9);

    private static bool IsStablePoint(IEnumerable<ScreenshotIntervalResult> results, double interval)
        => results.Any(result => Math.Abs(result.RequestedIntervalMilliseconds - interval) < 1e-6 && IsProvenStable(result));

    private static string? ReadVerdict(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("verdict", out JsonElement verdict) && verdict.ValueKind == JsonValueKind.String
                ? verdict.GetString() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static ApplicationResult<CaptureProfileEnvironment> Failure(string reason)
        => ApplicationResult<CaptureProfileEnvironment>.Failure(Rejected(reason).Failure!);

    internal static CaptureCommandOutcome Rejected(string reason)
        => new(new { status = "REJECTED", reason }, new ApplicationFailure
        {
            Code = ApplicationFailure.InvalidArgument,
            Message = "Применение профиля интервалов отклонено.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                [ApplicationFailure.MutationAttemptedDetailKey] = "false",
            },
        }, "none");
}
