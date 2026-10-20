using System.Text.Json;
using AzurPilot.App.Agent;
using AzurPilot.App.Capture;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Tests.Configuration;
using Xunit;

namespace AzurPilot.Tests.Capture;

/// <summary>Применение профиля интервалов: привязка сессии, достоверность профилей, одноразовый код и проверка записи.</summary>
[Trait("Category", "Capture")]
public sealed class CaptureProfileApplyTests
{
    private static readonly string ConfigurationPath = Path.Combine(Path.GetTempPath(), "azurpilot-apply", "config.yaml");
    private static readonly DateTimeOffset Measured = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Measured.AddHours(1);
    private static readonly ScreenshotConfiguration Defaults = AzurPilotConfigurationDefaults.Create().Screenshot;
    private static readonly double[] NormalGrid = [10, 25, 33.3, 50, 75, 100, 150, 200, 300];
    private static readonly double[] CombatGrid = [25, 50, 75, 100, 150, 200, 300, 500, 750, 1000];

    [Theory(DisplayName = "Профили берутся из устойчивых точек; current восстанавливает значения измерения")]
    [InlineData("fast", 34, 75, 33.3)]
    [InlineData("balanced", 75, 300, 75.0)]
    [InlineData("low_load", 150, 500, 150.0)]
    [InlineData("current", 150, 1000, null)]
    public void ProfilesComeFromStablePoints(string profile, int normal, int combat, double? measured)
    {
        (CaptureProfilePlan? plan, string? rejection) = CaptureProfileApply.Evaluate(Session(), Environment(), profile, Now);

        Assert.Null(rejection);
        Assert.Equal((normal, combat), (plan!.NormalMs, plan.CombatMs!.Value));
        Assert.Equal(measured, plan.MeasuredNormalMs);
        Assert.Equal(profile == "current" ? "restore" : "measured", plan.Combat);
    }

    [Theory(DisplayName = "Изменение окружения или конфигурации отклоняет применение")]
    [InlineData("target", "target_mismatch")]
    [InlineData("mumu", "emulator_version_changed")]
    [InlineData("fingerprint", "environment_changed")]
    [InlineData("resolution", "resolution_changed")]
    [InlineData("method", "method_changed")]
    [InlineData("fallback", "method_changed")]
    [InlineData("calibration", "calibration_changed")]
    [InlineData("path", "configuration_path_mismatch")]
    [InlineData("json", "configuration_legacy_json")]
    [InlineData("settings", "configuration_changed")]
    public void ChangedEnvironmentIsRejected(string change, string reason)
    {
        CaptureProfileEnvironment environment = Environment();
        environment = change switch
        {
            "target" => environment with { InstanceId = "mumu:2" },
            "mumu" => environment with { MuMuVersion = "6.9.0.0" },
            "fingerprint" => environment with { Fingerprint = "other" },
            "resolution" => environment with { Width = 1920, Height = 1080 },
            "method" => environment with { Method = "ADB" },
            "fallback" => environment with { FallbackFrom = "nemu_ipc" },
            "calibration" => environment with { CalibrationRunId = "run-2" },
            "path" => environment with { ConfigurationPath = Path.Combine(Path.GetTempPath(), "azurpilot-other", "config.yaml") },
            "json" => environment with { ConfigurationFormat = AzurPilotConfigurationFormat.Json },
            _ => environment with { Screenshot = Defaults with { NormalIntervalMilliseconds = 100 } },
        };

        Assert.Equal((null, reason), CaptureProfileApply.Evaluate(Session(), environment, "balanced", Now));
    }

    [Fact(DisplayName = "Просроченная, старая и незавершённая сессии отклоняются")]
    public void ExpiredLegacyAndIncompleteSessions()
    {
        Assert.Equal("session_expired", CaptureProfileApply.Evaluate(Session(), Environment(), "balanced", Measured.AddHours(25)).Rejection);
        Assert.Equal("session_expired", CaptureProfileApply.Evaluate(Session(), Environment(), "balanced", Measured.AddMinutes(-1)).Rejection);
        CaptureProfileSession legacy = Session() with { State = Session().State with { FormatVersion = 0, Binding = null } };
        Assert.Equal("session_binding_missing", CaptureProfileApply.Evaluate(legacy, Environment(), "balanced", Now).Rejection);
        CaptureProfileSession partial = Session() with { NormalReportVerdict = "PARTIAL" };
        Assert.Equal("normal_phase_incomplete", CaptureProfileApply.Evaluate(partial, Environment(), "balanced", Now).Rejection);
    }

    [Theory(DisplayName = "Неизмеренный или незавершённый бой не меняет боевой интервал")]
    [InlineData(false, null, "not_measured")]
    [InlineData(true, "PARTIAL", "phase_incomplete")]
    public void CombatIsKeptWithoutCompleteMeasurement(bool completed, string? verdict, string decision)
    {
        CaptureProfileSession session = Session() with
        {
            State = Session().State with { CombatCompleted = completed, CombatVerdict = verdict },
            CombatReportVerdict = verdict,
        };

        CaptureProfilePlan plan = CaptureProfileApply.Evaluate(session, Environment(), "balanced", Now).Plan!;

        Assert.Equal(75, plan.NormalMs);
        Assert.Null(plan.CombatMs);
        Assert.Equal(decision, plan.Combat);
    }

    [Fact(DisplayName = "Повреждённые результаты не принимаются на веру")]
    public void CorruptedResultsAreRejected()
    {
        ScreenshotIntervalResult forged = Point(75, stable: false) with { Class = ScreenshotIntervalClass.Stable };
        CaptureProfileSession session = Session() with
        {
            Normal = [.. Session().Normal!.Where(r => r.RequestedIntervalMilliseconds != 75), forged],
        };
        Assert.Equal("session_results_invalid", CaptureProfileApply.Evaluate(session, Environment(), "balanced", Now).Rejection);
        Assert.Equal("session_results_invalid", CaptureProfileApply.Evaluate(Session() with { Normal = null }, Environment(), "balanced", Now).Rejection);
        CaptureProfileSession unstable = Session() with { Normal = [Point(50, stable: false)] };
        Assert.Equal("profile_unavailable", CaptureProfileApply.Evaluate(unstable, Environment(), "fast", Now).Rejection);
    }

    [Fact(DisplayName = "Сессия читается с диска; повреждённый отчёт фазы отклоняется")]
    public void SessionIsLoadedFromReport()
    {
        string directory = Path.Combine(Path.GetTempPath(), "azurpilot-apply-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            CaptureIntervals.WriteState(Path.Combine(directory, CaptureIntervals.StateFile), Session().State);
            WritePhase(directory, CaptureIntervals.NormalFile, "PASS", Session().Normal!);
            WritePhase(directory, CaptureIntervals.CombatFile, "UNVERIFIED_SCENE", Session().Combat!);
            CaptureProfileSession loaded = CaptureProfileApply.Load(directory)!;
            Assert.Equal((75, 300), (CaptureProfileApply.Evaluate(loaded, Environment(), "balanced", Now).Plan!.NormalMs,
                CaptureProfileApply.Evaluate(loaded, Environment(), "balanced", Now).Plan!.CombatMs!.Value));

            File.WriteAllText(Path.Combine(directory, CaptureIntervals.NormalFile), "{\"verdict\":\"PASS\",\"points\":[{\"result\":7}]}");
            Assert.Equal("session_results_invalid",
                CaptureProfileApply.Evaluate(CaptureProfileApply.Load(directory)!, Environment(), "balanced", Now).Rejection);
            File.WriteAllText(Path.Combine(directory, CaptureIntervals.StateFile), "{");
            Assert.Null(CaptureProfileApply.Load(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "Код одноразовый, привязан к профилю, пути и SHA-256 файла; неверная попытка гасит код")]
    public void ConfirmationIsSingleUse()
    {
        string directory = Path.Combine(Path.GetTempPath(), "azurpilot-apply-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            CaptureProfileEnvironment environment = Environment();
            CaptureProfilePlan plan = CaptureProfileApply.Evaluate(Session(), environment, "balanced", Now).Plan!;
            Assert.Equal("preview_not_found", Redeem("ABCDEFGH", "balanced", environment, plan, null, Now));

            CaptureProfilePreview preview = CaptureProfileApply.Issue(directory, plan, environment, Now);
            Assert.Equal(8, preview.Code.Length);
            Assert.Null(Redeem(preview.Code, "balanced", environment, plan, null, Now));
            Assert.Equal("confirmation_used", Redeem(preview.Code, "balanced", environment, plan, null, Now));

            preview = CaptureProfileApply.Issue(directory, plan, environment, Now);
            Assert.Equal("confirmation_mismatch", Redeem(Other(preview.Code), "balanced", environment, plan, null, Now));
            Assert.Equal("confirmation_used", Redeem(preview.Code, "balanced", environment, plan, null, Now));

            foreach ((string profile, CaptureProfileEnvironment env, CaptureProfilePlan? candidate, string? rejection, DateTimeOffset at, string reason) in new[]
            {
                ("balanced", environment, plan, null, Now + CaptureProfileApply.PreviewLifetime + TimeSpan.FromSeconds(1), "confirmation_expired"),
                ("fast", environment, plan, null, Now, "profile_mismatch"),
                ("balanced", environment with { ConfigurationPath = Path.Combine(Path.GetTempPath(), "azurpilot-other", "config.yaml") }, plan, null, Now, "configuration_path_mismatch"),
                ("balanced", environment with { ConfigurationSha256 = "CHANGED" }, plan, null, Now, "configuration_changed_since_preview"),
                ("balanced", environment, plan, "resolution_changed", Now, "resolution_changed"),
                ("balanced", environment, plan with { NormalMs = 50 }, null, Now, "plan_changed"),
            })
            {
                preview = CaptureProfileApply.Issue(directory, plan, environment, Now);
                Assert.Equal(reason, Redeem(preview.Code, profile, env, candidate, rejection, at));
                Assert.Equal("confirmation_used", Redeem(preview.Code, "balanced", environment, plan, null, Now));
            }

            preview = CaptureProfileApply.Issue(directory, plan, environment, Now);
            CaptureProfilePreview replaced = CaptureProfileApply.Issue(directory, plan, environment, Now);
            Assert.Equal("confirmation_mismatch", Redeem(preview.Code, "balanced", environment, plan, null, Now));
            Assert.Equal("confirmation_used", Redeem(replaced.Code, "balanced", environment, plan, null, Now));

            preview = CaptureProfileApply.Issue(directory, plan, environment, Now);
            using (new FileStream(Path.Combine(directory, CaptureProfileApply.PreviewFile), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal("preview_busy", Redeem(preview.Code, "balanced", environment, plan, null, Now));
            }

            Assert.Null(Redeem(preview.Code, "balanced", environment, plan, null, Now));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        string? Redeem(string code, string profile, CaptureProfileEnvironment env, CaptureProfilePlan? plan, string? rejection,
            DateTimeOffset at)
        {
            return CaptureProfileApply.Redeem(directory, code, profile, env, plan, rejection, at);
        }
    }

    [Fact(DisplayName = "После записи файл перечитывается и независимо сверяется с планом")]
    public void AppliedFileIsVerified()
    {
        using TemporaryConfigurationDirectory directory = new();
        _ = directory.WriteYamlConfiguration("schemaVersion: 3\nui:\n  mode: normal\nmumu:\n  instance: \"mumu:1\"\n"
            + "diagnostics:\n  minimumLevel: Information\nlogging:\n  viewer:\n    enabled: true\n");
        AzurPilotConfigurationSnapshot before = AzurPilotConfigurationLoader.Load(directory.YamlConfigurationFilePath).Value!;
        CaptureProfilePlan plan = CaptureProfileApply.Evaluate(Session(), Environment(), "balanced", Now).Plan!;
        ApplicationResult<AzurPilotConfigurationSnapshot> saved = AzurPilotConfigurationStore.SaveScreenshotIntervals(
            directory.YamlConfigurationFilePath, directory.LegacyJsonConfigurationFilePath, before.SourceVersion, plan.NormalMs,
            plan.CombatMs, out _);
        Assert.True(saved.IsSuccess, saved.IsSuccess ? null : saved.FailureInfo!.Message);

        AzurPilotConfigurationSnapshot reread = AzurPilotConfigurationLoader.Load(directory.YamlConfigurationFilePath).Value!;
        Assert.Null(CaptureProfileApply.Verify(before.Configuration, reread, plan));
        Assert.Equal("applied_values_mismatch", CaptureProfileApply.Verify(before.Configuration, reread, plan with { NormalMs = 50 }));
        AzurPilotConfiguration otherBefore = before.Configuration with
        {
            Logging = before.Configuration.Logging with { Viewer = before.Configuration.Logging.Viewer with { Enabled = false } },
        };
        Assert.Equal("other_settings_changed", CaptureProfileApply.Verify(otherBefore, reread, plan));
    }

    [Theory(DisplayName = "Команда применения принимает только описанные аргументы")]
    [InlineData(new[] { "capture-profile-apply", "--session", "capture-intervals-live-2", "--profile", "balanced" }, true)]
    [InlineData(new[] { "capture-profile-apply", "--session", "s1", "--profile", "current", "--confirm", "ABCD2345" }, true)]
    [InlineData(new[] { "capture-profile-apply", "--session", "s1", "--profile", "turbo" }, false)]
    [InlineData(new[] { "capture-profile-apply", "--session", "s1", "--profile", "balanced", "--confirm", "abcd2345" }, false)]
    [InlineData(new[] { "capture-profile-apply", "--session", "s1", "--profile", "balanced", "--normal", "75" }, false)]
    public void ParserAcceptsOnlyDescribedArguments(string[] arguments, bool accepted)
    {
        Assert.Equal(accepted, AgentRequest.Parse(["agent", .. arguments]).IsSuccess);
        Assert.True(AgentCommands.All.Single(c => c.Name == CaptureProfileApply.Command).Mutation);
    }

    private static CaptureProfileSession Session()
    {
        CaptureIntervalBinding binding = new("fingerprint", "6.8.5.0", "15.0", 3840, 2160, "calibrated:run-1", "run-1",
            ConfigurationPath, "SHA", Defaults);
        CaptureIntervalSessionState state = new("s1", "mumu:1", "127.0.0.1:16416", "nemu_ipc", "ABCDEFGH", Measured,
            Measured.AddMinutes(30), true, CaptureIntervals.StateFormatVersion, binding, "PASS", "UNVERIFIED_SCENE");
        return new(state, [.. NormalGrid.Select(interval => Point(interval, interval >= 33.3))], "PASS",
            [.. CombatGrid.Select(interval => Point(interval, interval >= 50))], "UNVERIFIED_SCENE");
    }

    private static CaptureProfileEnvironment Environment()
        => new("mumu:1", "127.0.0.1:16416", "fingerprint", "6.8.5.0", 3840, 2160, "nemu_ipc", null, "run-1",
            ConfigurationPath, AzurPilotConfigurationFormat.Yaml, "SHA", Defaults);

    private static ScreenshotIntervalResult Point(double interval, bool stable)
    {
        double spacing = stable ? interval : interval * 1.6;
        int count = Math.Max(8, (int)(2000 / interval));
        List<double> starts = [.. Enumerable.Range(0, count).Select(index => index * spacing)];
        return ScreenshotIntervalAnalysis.Summarize(new(interval, starts, [.. starts.Select(start => start + Math.Min(5, interval / 2))],
            count, null));
    }

    private static void WritePhase(string directory, string name, string verdict, IEnumerable<ScreenshotIntervalResult> results)
        => File.WriteAllBytes(Path.Combine(directory, name), JsonSerializer.SerializeToUtf8Bytes(
            new { verdict, points = results.Select(result => new { result }) }, AgentJson.Options));

    private static string Other(string code) => code[0] == 'A' ? "B" + code[1..] : "A" + code[1..];
}
