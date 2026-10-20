using System.Text;
using AzurPilot.App.Agent;
using AzurPilot.App.Capture;
using AzurPilot.Core.Android;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Tests.Configuration;
using AzurPilot.Windows.Capture;
using Xunit;

namespace AzurPilot.Tests.Capture;

/// <summary>Контракты без устройства: аргументы Agent CLI, worker envelope, журнал helper-ов, shell-токены и настройка.</summary>
[Trait("Category", "Capture")]
public sealed class CaptureContractTests
{
    [Theory(DisplayName = "Команды транспортов принимают только описанные аргументы")]
    [InlineData(new[] { "capture-benchmark", "--methods", "ADB,nemu_ipc,scrcpy" }, true)]
    [InlineData(new[] { "capture-benchmark", "--methods", "all", "--profile", "full", "--calibrate", "false" }, true)]
    [InlineData(new[] { "capture-benchmark", "--methods", "ADB,ADB" }, false)]
    [InlineData(new[] { "capture-benchmark", "--methods", "ldopengl" }, false)]
    [InlineData(new[] { "capture-benchmark", "--methods", "adb" }, false)]
    [InlineData(new[] { "capture-benchmark", "--profile", "huge" }, false)]
    [InlineData(new[] { "capture-benchmark", "--samples", "1001" }, false)]
    [InlineData(new[] { "capture-probe", "--method", "DroidCast_raw", "--samples", "5" }, true)]
    [InlineData(new[] { "capture-probe", "--method", "ADB,scrcpy" }, false)]
    [InlineData(new[] { "capture-intervals", "--scene", "normal", "--session", "pr-26-intervals" }, true)]
    [InlineData(new[] { "capture-intervals", "--scene", "combat", "--session", "pr-26-intervals", "--confirm", "ABCD2345" }, true)]
    [InlineData(new[] { "capture-intervals", "--scene", "combat", "--confirm", "abcd2345" }, false)]
    [InlineData(new[] { "capture-intervals", "--scene", "battle" }, false)]
    [InlineData(new[] { "capture-intervals", "--grid", "16.7,33.3,50" }, true)]
    [InlineData(new[] { "capture-intervals", "--grid", "0.5" }, false)]
    [InlineData(new[] { "capture-intervals", "--grid", "50;rm" }, false)]
    [InlineData(new[] { "capture-methods", "--methods", "all" }, false)]
    public void ParserAcceptsOnlyDescribedCaptureArguments(string[] arguments, bool accepted)
        => Assert.Equal(accepted, AgentRequest.Parse(["agent", .. arguments]).IsSuccess);

    [Fact(DisplayName = "Discovery описывает команды транспортов и их эффект mutation")]
    public void DiscoveryDescribesCaptureCommands()
    {
        Assert.False(AgentCommands.All.Single(c => c.Name == "capture-methods").Mutation);
        Assert.True(AgentCommands.All.Single(c => c.Name == "capture-probe").Mutation);
        Assert.True(AgentCommands.All.Single(c => c.Name == "capture-benchmark").Mutation);
        Assert.True(AgentCommands.All.Single(c => c.Name == "capture-intervals").Mutation);
        Assert.All(AgentCommands.All.Where(c => c.Name.StartsWith("capture-", StringComparison.Ordinal)), c => Assert.True(c.Target));
        AgentRequest parsed = AgentRequest.Parse(["agent", "capture-benchmark", "--methods", "ADB", "--debug-overhead", "true"]).Value!;
        Assert.Equal("ADB", parsed.Options!["--methods"]);
        Assert.Equal("true", parsed.Options["--debug-overhead"]);
        Assert.Equal([ScreenshotMethod.Adb, ScreenshotMethod.NemuIpc], AgentCaptureArguments.ParseMethods("ADB,nemu_ipc"));
        Assert.Equal(ScreenshotMethodNames.All, AgentCaptureArguments.ParseMethods(null));
        Assert.Equal(9, CaptureCompatibility.All.Count);
        Assert.Equal(ScreenshotMethodNames.All, CaptureCompatibility.All.Select(entry => entry.Method));
    }

    [Fact(DisplayName = "Envelope worker-а разбирается в итог серии; busy и мусор — BLOCKED и FAILED")]
    public void WorkerEnvelopeIsParsed()
    {
        string success = """
            {"schemaVersion":1,"status":"success","data":{"method":"nemu_ipc","verdict":"PASS","width":3840,"height":2160,
            "attempts":25,"successes":25,"endToEnd":{"count":25,"minimum":5,"median":6,"p95":7,"p99":8,"maximum":9,"mean":6,"standardDeviation":1},
            "freshFramesPerSecond":150.5,"correctnessProven":true,"facts":{"dll":"nx_main/sdk"}}}
            """;
        CaptureProbeResult parsed = CaptureWorker.Parse("nemu_ipc", Encoding.UTF8.GetBytes(success));
        Assert.Equal("PASS", parsed.Verdict);
        Assert.Equal(7, parsed.EndToEnd.P95);
        Assert.True(parsed.CorrectnessProven);
        Assert.Equal("nx_main/sdk", parsed.Facts["dll"]);
        ScreenshotCandidateMeasurement measurement = CaptureBenchmark.Measurement(parsed);
        Assert.Equal(ScreenshotMethod.NemuIpc, measurement.Method);
        Assert.Equal(CaptureVerdict.Pass, measurement.Verdict);

        CaptureProbeResult busy = CaptureWorker.Parse("scrcpy", Encoding.UTF8.GetBytes(
            """{"status":"busy","failure":{"code":"target_busy"},"data":null}"""));
        Assert.Equal(("BLOCKED", "target_busy"), (busy.Verdict, busy.Reason));
        CaptureProbeResult other = CaptureWorker.Parse("scrcpy", Encoding.UTF8.GetBytes(success));
        Assert.Equal("FAILED", other.Verdict);
        Assert.Equal("worker_output_invalid", CaptureWorker.Parse("ADB", [0x7B]).Reason);
    }

    [Fact(DisplayName = "Журнал helper-ов: ресурсы живого процесса не трогаются, ресурсы завершённого выдаются один раз")]
    public void LedgerReleasesOnlyOrphans()
    {
        string directory = Path.Combine(Path.GetTempPath(), "azurpilot-ledger-" + Guid.NewGuid().ToString("N"));
        try
        {
            CaptureHelperLedger ledger = new(new AndroidEndpoint("127.0.0.1", 16416), directory);
            CaptureHelperResource own = CaptureHelperLedger.Create("forward", 27183);
            CaptureHelperResource orphan = new("helper", 4242, "azurpilot-capture-test", int.MaxValue - 7,
                DateTimeOffset.UtcNow.AddHours(-1));
            ledger.Add(own);
            ledger.Add(orphan);

            Assert.Equal([orphan], ledger.TakeOrphans());
            Assert.Empty(ledger.TakeOrphans());
            ledger.Remove(own);
            Assert.Empty(ledger.TakeOrphans());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory(DisplayName = "Shell-токены: значения с метасимволами отвергаются до запуска adb")]
    [InlineData("scrcpy-server.jar", true)]
    [InlineData("/data/local/tmp/azurpilot-capture/u2.jar", true)]
    [InlineData("a;rm -rf /", false)]
    [InlineData("$(id)", false)]
    [InlineData("a b", false)]
    [InlineData("x`y`", false)]
    [InlineData("", false)]
    public void ShellTokensAreValidated(string value, bool accepted)
    {
        if (accepted)
        {
            Assert.Equal(value, CaptureAdb.Token(value));
        }
        else
        {
            // Небезопасный токен — ошибка программирования вызывающего кода, а не отказ устройства.
            _ = Assert.Throws<ArgumentException>(() => CaptureAdb.Token(value));
        }
    }

    [Fact(DisplayName = "Настройка screenshot: необязательная секция, явный метод, интервалы и отказы на неверные значения")]
    public void ScreenshotSettingIsStrict()
    {
        const string Base = """
            schemaVersion: 3
            ui:
              mode: normal
            mumu:
              instance: auto
            diagnostics:
              minimumLevel: Information
            logging:
              viewer:
                enabled: false
            """;
        using TemporaryConfigurationDirectory directory = new();
        AzurPilotConfiguration defaults = AzurPilotConfigurationLoader.Load(directory.WriteYamlConfiguration(Base)).Value!.Configuration;
        Assert.Equal(AzurPilotConfigurationDefaults.Create().Screenshot, defaults.Screenshot);
        Assert.True(defaults.Screenshot.MethodSetting.IsAutomatic);
        Assert.DoesNotContain("screenshot:", AzurPilotConfigurationYamlWriter.Write(defaults), StringComparison.Ordinal);

        string custom = Base + "\nscreenshot:\n  method: nemu_ipc\n  normalIntervalMs: 50\n  combatIntervalMs: 300\n";
        AzurPilotConfiguration configured = AzurPilotConfigurationLoader.Load(directory.WriteYamlConfiguration(custom)).Value!.Configuration;
        Assert.Equal(ScreenshotMethod.NemuIpc, configured.Screenshot.MethodSetting.Method);
        Assert.Equal((50, 300), (configured.Screenshot.NormalIntervalMilliseconds, configured.Screenshot.CombatIntervalMilliseconds));
        string written = AzurPilotConfigurationYamlWriter.Write(configured);
        Assert.Contains("screenshot:", written, StringComparison.Ordinal);
        Assert.Equal(configured, AzurPilotConfigurationLoader.Load(directory.WriteYamlConfiguration(written)).Value!.Configuration);

        foreach (string invalid in new[]
        {
            "\nscreenshot:\n  method: ldopengl\n",
            "\nscreenshot:\n  method: Auto\n",
            "\nscreenshot:\n  normalIntervalMs: 0\n",
            "\nscreenshot:\n  normalIntervalMs: 301\n",
            "\nscreenshot:\n  combatIntervalMs: 1001\n",
            "\nscreenshot:\n  unknown: 1\n",
        })
        {
            ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(
                directory.WriteYamlConfiguration(Base + invalid));
            Assert.True(result.IsFailure, invalid);
            Assert.Equal(ApplicationFailure.ConfigurationInvalid, result.FailureInfo!.Code);
        }
    }
}
