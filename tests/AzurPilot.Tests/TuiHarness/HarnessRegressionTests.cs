using System.Diagnostics;
using System.Text.Json;
using AzurPilot.App;
using AzurPilot.App.OperatorUi;
using AzurPilot.TuiHarness;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

[Collection(InteropCollection.Name)]
public sealed class HarnessRegressionTests
{
    [Fact]
    public async Task IsolatedConfigurationAcceptsSeparateDirectoryAndRejectsUserAliases()
    {
        using Scratch scratch = new();
        string user = scratch.File("user/config.yaml");
        string userLegacyJson = scratch.File("user/config.json");
        string separate = scratch.File("separate/config.yaml");
        WindowsConfigurationIsolation.ValidateAgainst(separate, user, userLegacyJson);
        foreach (string alias in new[] { user, user.ToUpperInvariant(), Path.Combine(Path.GetDirectoryName(user)!, ".", "config.yaml"),
            Path.Combine(Path.GetDirectoryName(user)!, "..", "user", "config.yaml"), @"\\?\" + user })
        {
            _ = Assert.Throws<ArgumentException>(() => WindowsConfigurationIsolation.ValidateAgainst(alias, user, userLegacyJson));
        }

        string link = Path.Combine(scratch.Root, "junction");
        _ = await PowerShell("$d = $env:AZURPILOT_TEST_DATA | ConvertFrom-Json; New-Item -ItemType Junction -Path $d.link -Target $d.target | Out-Null", new { link, target = Path.GetDirectoryName(user) });
        _ = Assert.Throws<ArgumentException>(() => WindowsConfigurationIsolation.ValidateAgainst(Path.Combine(link, "config.yaml"), user, userLegacyJson));
        Directory.Delete(link);
        _ = await PowerShell("$d = $env:AZURPILOT_TEST_DATA | ConvertFrom-Json; New-Item -ItemType Junction -Path $d.link -Target $d.target | Out-Null", new { link, target = Path.GetDirectoryName(separate) });
        WindowsConfigurationIsolation.ValidateAgainst(Path.Combine(link, "config.yaml"), user, userLegacyJson);
        Directory.Delete(link);

        string hardlink = Path.Combine(scratch.Root, "hardlink.yaml");
        _ = await PowerShell("$d = $env:AZURPILOT_TEST_DATA | ConvertFrom-Json; New-Item -ItemType HardLink -Path $d.link -Target $d.target | Out-Null", new { link = hardlink, target = user });
        _ = Assert.Throws<ArgumentException>(() => WindowsConfigurationIsolation.ValidateAgainst(hardlink, user, userLegacyJson));
        _ = Assert.Throws<ArgumentException>(() => WindowsConfigurationIsolation.ValidateAgainst(Path.Combine(scratch.Root, "absent.yaml"), user, userLegacyJson));
        string legacyHardlink = Path.Combine(scratch.Root, "legacy-hardlink.yaml");
        _ = await PowerShell("$d = $env:AZURPILOT_TEST_DATA | ConvertFrom-Json; New-Item -ItemType HardLink -Path $d.link -Target $d.target | Out-Null", new { link = legacyHardlink, target = userLegacyJson });
        _ = Assert.Throws<ArgumentException>(() => WindowsConfigurationIsolation.ValidateAgainst(legacyHardlink, user, userLegacyJson));
        Assert.Equal("schemaVersion: 3", await File.ReadAllTextAsync(user, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsolatedHostRejectsUserDirectoryBeforeSessionCallback()
    {
        bool called = false;
        ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(() => AzurPilotHost.RunInteractiveAsync(
            (_, _, _, _, _, _) => { called = true; return Task.CompletedTask; },
            configurationPath: Core.Configuration.AzurPilotConfigurationPath.GetDefaultRuntimePath(),
            isolatedUi: true, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Изолированная конфигурация не может находиться в пользовательском runtime-каталоге.", failure.Message, StringComparison.Ordinal);
        Assert.False(called);
    }

    [Theory]
    [InlineData(46_000, true)]
    [InlineData(89_999, true)]
    [InlineData(101_001, false)]
    public async Task StartupWatchdogUsesDocumentedBudgetWithoutWallClockSleep(int readinessMs, bool expected)
    {
        ManualDeadlineClock clock = new();
        TaskCompletionSource ready = new();
        TaskCompletionSource clientExited = new();
        Task<bool> watched = HarnessTimeouts.StartupCompletedAsync(ready.Task, clientExited.Task, clock);
        clock.Advance(readinessMs);
        if (expected)
        {
            Assert.False(watched.IsCompleted);
        }

        ready.SetResult();
        Assert.Equal(expected, await watched);
        Assert.True(HarnessTimeouts.StartProcessMs > HarnessTimeouts.StartConnectionMs + HarnessTimeouts.StartupResponseMs + HarnessTimeouts.ResponseGraceMs);
        Assert.Equal(45_000, HarnessTimeouts.CommandProcessMs);
        Assert.True(HarnessTimeouts.ScenarioProcessMs > HarnessTimeouts.CommandProcessMs);
    }

    [Fact]
    public async Task StartupWatchdogRejectsAbandonedClient()
    {
        TaskCompletionSource ready = new();
        TaskCompletionSource exited = new();
        Task<bool> result = HarnessTimeouts.StartupCompletedAsync(ready.Task, exited.Task, new ManualDeadlineClock());
        exited.SetResult();
        Assert.False(await result);
    }

    [Fact]
    public async Task ConfigurationPathVerifierPreservesSpacesAcrossProductionWrap()
    {
        string path = Path.Combine(Path.GetTempPath(), "A B", "очень длинное имя  с пробелами", "config.yaml");
        string source = OperatorUiController.ConfigurationPathLabel + path;
        string[] rows = [.. AzurPilotOperatorUi.Wrap(source, 35).Select(row => row.PadRight(35))];
        Assert.Equal(source, string.Concat(rows).TrimEnd());
        JsonElement result = JsonSerializer.Deserialize<JsonElement>(await PowerShell("""
            . './tools/AzurPilot.TuiHarness/VerificationSupport.ps1'
            $d = $env:AZURPILOT_TEST_DATA | ConvertFrom-Json
            @{ correct = (Test-DisplayedConfigurationPath $d.rows $d.path)
               wrong = (Test-DisplayedConfigurationPath $d.rows ($d.path.Replace('A B', 'AB')))
               suffix = (Test-DisplayedConfigurationPath $d.rows ($d.path.Replace('config.yaml', 'config.yam'))) } | ConvertTo-Json -Compress
            """, new { rows, path }));
        Assert.True(result.GetProperty("correct").GetBoolean());
        Assert.False(result.GetProperty("wrong").GetBoolean());
        Assert.False(result.GetProperty("suffix").GetBoolean());
    }

    [Theory]
    [InlineData("NOT_TESTED", true, "click_effect_not_observed", false, "NOT_TESTED")]
    [InlineData("PASS", true, "", true, "PASS")]
    [InlineData("PASS", true, "", false, "NOT_TESTED")]
    [InlineData("BLOCKED", false, "delivery_error", false, "BLOCKED")]
    [InlineData("FAIL", false, "ipc_error", false, "FAIL")]
    public async Task OptionalClickReportsActualSupportAndDelivery(string status, bool delivered, string reason, bool settingsObserved, string expected)
    {
        string output = await PowerShell("""
            . './tools/AzurPilot.TuiHarness/VerificationSupport.ps1'
            $d = $env:AZURPILOT_TEST_DATA | ConvertFrom-Json
            Get-PaletteClickAssessment $d.response $d.settingsObserved | ConvertTo-Json -Compress
            """, new { response = new { status, message = "Фактический результат", data = new { delivered, reason } }, settingsObserved });
        Assert.Equal(expected, JsonSerializer.Deserialize<JsonElement>(output).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void HistoryObservationFollowsSemanticRegionsAndDirection(int extraRows)
    {
        JsonElement before = Screen(extraRows, ["old-3", "old-4", "old-5", "old-6"], "Операция: нет");
        JsonElement after = Screen(extraRows, ["old-1", "old-2", "old-3", "old-4"], "Операция: завершена");
        Assert.Equal("Операция: нет", TerminalUiObservation.Operation(before));
        Assert.Equal((40, extraRows + 5), TerminalUiObservation.HistoryPoint(before));
        TerminalUiObservation.RequireHistoryScrolledUp(before, after);
        TerminalUiObservation.RequireHistoryEqual(before, before);
        _ = Assert.Throws<HarnessAssertionException>(() => TerminalUiObservation.RequireHistoryScrolledUp(before, before));
        _ = Assert.Throws<HarnessAssertionException>(() => TerminalUiObservation.RequireHistoryScrolledUp(after, before));
        _ = Assert.Throws<HarnessAssertionException>(() => TerminalUiObservation.RequireHistoryEqual(before, after));
        JsonElement statusOnly = Screen(extraRows, ["old-3", "old-4", "old-5", "old-6"], "Операция: отказ");
        _ = Assert.Throws<HarnessAssertionException>(() => TerminalUiObservation.RequireHistoryScrolledUp(before, statusOnly));
        JsonElement scrollback = Screen(extraRows, ["old-1", "old-2", "old-3", "old-4"], "Операция: нет", 1);
        _ = Assert.Throws<HarnessAssertionException>(() => TerminalUiObservation.RequireHistoryScrolledUp(before, scrollback));
        JsonElement pageBefore = Screen(extraRows, ["> marker 9", "строка 9", "> marker 10", "строка 10"], "Операция: нет");
        JsonElement pageAfter = Screen(extraRows, ["> marker 7", "строка 7", "> marker 8", "строка 8"], "Операция: нет");
        TerminalUiObservation.RequireHistoryScrolledUp(pageBefore, pageAfter, "> marker ");
        _ = Assert.Throws<HarnessAssertionException>(() => TerminalUiObservation.RequireHistoryScrolledUp(pageAfter, pageBefore, "> marker "));
    }

    [Fact]
    public async Task SourceIdentityUsesHarnessCheckoutFromForeignAndNonGitWorkingDirectories()
    {
        using Scratch scratch = new();
        string foreign = Path.Combine(scratch.Root, "foreign");
        _ = Directory.CreateDirectory(foreign);
        _ = await Git(foreign, "init");
        await File.WriteAllTextAsync(Path.Combine(foreign, "other.txt"), "чужой checkout", TestContext.Current.CancellationToken);
        _ = await Git(foreign, "add", "other.txt");
        _ = await Git(foreign, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
        string expected = (await Git(PinnedVersions.RepositoryRoot, "rev-parse", "HEAD")).Trim();
        string original = Environment.CurrentDirectory;
        string? originalGitDirectory = Environment.GetEnvironmentVariable("GIT_DIR");
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(foreign, ".git"));
            foreach (string cwd in new[] { PinnedVersions.RepositoryRoot, foreign, scratch.Root })
            {
                Environment.CurrentDirectory = cwd;
                JsonElement source = JsonSerializer.SerializeToElement(await SessionOwner.SourceIdentityAsync(AppContext.BaseDirectory), HarnessJson.Options);
                Assert.Equal("PASS", source.GetProperty("status").GetString());
                Assert.Equal(expected, source.GetProperty("commit").GetString());
            }

            JsonElement unavailable = JsonSerializer.SerializeToElement(await SessionOwner.SourceIdentityAsync(foreign), HarnessJson.Options);
            Assert.Equal("NOT_TESTED", unavailable.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("commit").ValueKind);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            Environment.SetEnvironmentVariable("GIT_DIR", originalGitDirectory);
        }
    }

    private static readonly string[] ScreenPrefix = ["AzurPilot"];

    private static JsonElement Screen(int extraRows, string[] history, string operation, int viewportY = 0)
    {
        return JsonSerializer.SerializeToElement(new
        {
            viewportY,
            columns = 80,
            lines = ScreenPrefix.Concat(Enumerable.Repeat("пояснение", extraRows))
                .Concat([operation, "── Сообщения и результаты ──"]).Concat(history).Concat(["────────────────────────────", "❯", "F2 действия"])
        });
    }

    private static Task<string> PowerShell(string script, object data)
    {
        ProcessStartInfo info = new("pwsh") { WorkingDirectory = PinnedVersions.RepositoryRoot };
        foreach (string value in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
        {
            info.ArgumentList.Add(value);
        }

        info.Environment["AZURPILOT_TEST_DATA"] = JsonSerializer.Serialize(data);
        return Run(info);
    }

    private static Task<string> Git(string directory, params string[] arguments)
    {
        ProcessStartInfo info = new("git") { WorkingDirectory = directory };
        foreach (string key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _ = info.Environment.Remove(key);
        }

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Run(info);
    }

    private static async Task<string> Run(ProcessStartInfo info)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    private sealed class ManualDeadlineClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        private double _due;
        internal void Advance(int milliseconds)
        {
            if (milliseconds >= _due)
            {
                _callback!(_state);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            _due = dueTime.TotalMilliseconds;
            return new DeadlineTimer();
        }

        private sealed class DeadlineTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                throw new NotSupportedException();
            }

            public void Dispose() { }
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "azurpilot-regression-" + Guid.NewGuid().ToString("N"));
        internal Scratch()
        {
            _ = Directory.CreateDirectory(Root);
        }

        internal string File(string relative)
        {
            string path = Path.Combine(Root, relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "schemaVersion: 3");
            return path;
        }

        public void Dispose()
        {
            // Git помечает свои objects read-only; меняем атрибут только в собственной фикстуре.
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}
