using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzurPilot.TuiHarness;
using AzurPilot.TuiHarness.Physical;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class WinAppCliTests : IDisposable
{
    private const string Marker = "AzurPilot-physical-0123456789abcdef0123456789abcdef";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AzurPilot.WinAppCliTests", Guid.NewGuid().ToString("N"));

    public WinAppCliTests()
    {
        _ = Directory.CreateDirectory(directory);
    }

    [Fact]
    public async Task SelectsOnlyFullMarkerAndExactTerminalProcessWithWideHwnd()
    {
        WinAppCli cli = new(runner: (executable, arguments, _) =>
        {
            Assert.Equal("winapp.exe", executable);
            Assert.Equal(["ui", "list-windows", "-a", "Terminal", "--json"], arguments);
            return Task.FromResult(Success(JsonSerializer.Serialize(new[]
            {
                WindowJson(Marker + "-foreign", 123, 10, "WindowsTerminal"),
                WindowJson(Marker, 4294967296L, 27, "WindowsTerminal"),
                WindowJson("другое окно", 456, 11, "OtherTerminal"),
            })));
        });

        WinAppWindow window = await cli.SelectWindowAsync(Marker, TestContext.Current.CancellationToken);

        Assert.Equal(4294967296L, window.Hwnd);
        Assert.Equal(27, window.ProcessId);
        Assert.Equal(Marker, window.Title);
        Assert.Equal("WindowsTerminal", window.ProcessName);
        Assert.Equal(800, window.Width);
        Assert.Equal(600, window.Height);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("wrong-process")]
    [InlineData("zero-hwnd")]
    [InlineData("negative-hwnd")]
    [InlineData("zero-pid")]
    [InlineData("zero-width")]
    [InlineData("ambiguous")]
    [InlineData("duplicate-hwnd")]
    public async Task WindowDiscoveryFailsClosedForUnprovenIdentity(string scenario)
    {
        object[] windows = scenario switch
        {
            "missing" => [],
            "prefix" => [WindowJson("foreign-" + Marker, 123, 27, "WindowsTerminal")],
            "suffix" => [WindowJson(Marker + "-foreign", 123, 27, "WindowsTerminal")],
            "wrong-process" => [WindowJson(Marker, 123, 27, "Terminal")],
            "zero-hwnd" => [WindowJson(Marker, 0, 27, "WindowsTerminal")],
            "negative-hwnd" => [WindowJson(Marker, -1, 27, "WindowsTerminal")],
            "zero-pid" => [WindowJson(Marker, 123, 0, "WindowsTerminal")],
            "zero-width" => [new { title = Marker, hwnd = 123, processId = 27,
                processName = "WindowsTerminal", width = 0, height = 600 }],
            "ambiguous" => [WindowJson(Marker, 123, 27, "WindowsTerminal"),
                WindowJson(Marker, 456, 28, "WindowsTerminal")],
            "duplicate-hwnd" => [WindowJson(Marker, 123, 27, "WindowsTerminal"),
                WindowJson(Marker, 123, 27, "WindowsTerminal")],
            _ => throw new InvalidOperationException("Неизвестный сценарий теста."),
        };
        WinAppCli cli = WithOutput(JsonSerializer.Serialize(windows));

        _ = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => cli.SelectWindowAsync(Marker, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("object-instead-of-array")]
    [InlineData("non-object-entry")]
    [InlineData("invalid-hwnd-type")]
    public async Task RejectsMalformedOrUnprovenWindowJson(string scenario)
    {
        string output = scenario switch
        {
            "malformed" => "{",
            "object-instead-of-array" => "{}",
            "non-object-entry" => "[null]",
            "invalid-hwnd-type" => JsonSerializer.Serialize(new[]
            {
                new { title = Marker, hwnd = "123", processId = 27, processName = "WindowsTerminal", width = 800, height = 600 },
            }),
            _ => throw new InvalidOperationException("Неизвестный сценарий теста."),
        };

        _ = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => WithOutput(output).SelectWindowAsync(Marker, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CapturesExactHwndAndPreservesUnknownCaptureMethod()
    {
        string outputPath = Path.Combine(directory, "physical.png");
        WinAppCli cli = new(runner: async (_, arguments, cancellationToken) =>
        {
            if (arguments.SequenceEqual(["ui", "list-windows", "-a", "Terminal", "--json"]))
            {
                return Success(JsonSerializer.Serialize(new[]
                {
                    WindowJson(Marker, 4294967296L, 27, "WindowsTerminal", 1, 1),
                }));
            }

            Assert.Equal(["ui", "screenshot", "-w", "4294967296", "--output", outputPath, "--json"], arguments);
            Assert.DoesNotContain("--capture-screen", arguments);
            Assert.DoesNotContain("--focus", arguments);
            await File.WriteAllBytesAsync(outputPath, OnePixelPng(), cancellationToken);
            return Success(CaptureMetadata(outputPath).ToJsonString());
        });

        WinAppCapture capture = await cli.CaptureAsync(OwnedWindow(), outputPath, TestContext.Current.CancellationToken);

        Assert.Equal(outputPath, capture.Path);
        Assert.Equal(1, capture.Width);
        Assert.Equal(1, capture.Height);
        Assert.Equal("Unknown", capture.CaptureMethod);
        Assert.Contains("не сообщает фактический mode", capture.Limitation, StringComparison.Ordinal);
        Assert.True(capture.TargetRevalidated);
        Assert.Equal(4294967296L, capture.RawMetadata.GetProperty("hwnd").GetInt64());
        Assert.True(File.Exists(outputPath));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, null)]
    [InlineData(null, true, null)]
    [InlineData(null, null, null)]
    public async Task Capture_ConfirmsFocusOnlyWhenBothObservationsAgree(bool? before, bool? after, bool? expected)
    {
        string path = Path.Combine(directory, "focus.png");
        int listings = 0;
        WinAppCli cli = new(runner: async (_, arguments, token) =>
        {
            if (arguments.Contains("list-windows"))
            {
                return Success(JsonSerializer.Serialize(new[]
                {
                    WindowJson(Marker, OwnedWindow().Hwnd, OwnedWindow().ProcessId, "WindowsTerminal",
                        1, 1, ++listings == 1 ? before : after),
                }));
            }

            await File.WriteAllBytesAsync(path, OnePixelPng(), token);
            return Success(CaptureMetadata(path).ToJsonString());
        });

        WinAppCapture capture = await cli.CaptureAsync(OwnedWindow(), path, TestContext.Current.CancellationToken);

        Assert.Equal(expected, capture.Focused);
        Assert.Equal(2, listings);
        Assert.True(capture.TargetRevalidated);
    }

    [Fact]
    public async Task ScreenshotWithoutIdentityFieldsUsesExactRequestedHwndAndRevalidatesIt()
    {
        string path = Path.Combine(directory, "identity-omitted.png");
        JsonObject metadata = new()
        {
            ["filePath"] = path,
            ["width"] = 1,
            ["height"] = 1,
            ["windows"] = new JsonArray(new JsonObject
            {
                ["hwnd"] = 4294967296L,
                ["title"] = Marker,
                ["width"] = 1,
                ["height"] = 1,
                ["captured"] = true,
            }),
        };
        WinAppCli cli = WithCaptureOutput(path, metadata.ToJsonString(), OnePixelPng());

        WinAppCapture capture = await cli.CaptureAsync(OwnedWindow(), path, TestContext.Current.CancellationToken);

        Assert.Equal(OwnedWindow().Hwnd, capture.Target.Hwnd);
        Assert.Equal(OwnedWindow().ProcessId, capture.Target.ProcessId);
        Assert.True(capture.TargetRevalidated);
    }

    [Fact]
    public async Task CaptureFailsWhenMarkerResolvesToAnotherWindowAfterScreenshot()
    {
        string path = Path.Combine(directory, "changed-target.png");
        int listings = 0;
        WinAppCli cli = new(runner: async (_, arguments, cancellationToken) =>
        {
            if (arguments.SequenceEqual(["ui", "list-windows", "-a", "Terminal", "--json"]))
            {
                listings++;
                long hwnd = listings == 1 ? OwnedWindow().Hwnd : OwnedWindow().Hwnd + 1;
                return Success(JsonSerializer.Serialize(new[]
                {
                    WindowJson(Marker, hwnd, OwnedWindow().ProcessId, "WindowsTerminal", 1, 1),
                }));
            }

            await File.WriteAllBytesAsync(path, OnePixelPng(), cancellationToken);
            return Success(CaptureMetadata(path).ToJsonString());
        });

        HarnessBlockedException failure = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => cli.CaptureAsync(OwnedWindow(), path, TestContext.Current.CancellationToken));

        Assert.Equal(2, listings);
        Assert.Contains("точный HWND/PID/marker", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("wrong-hwnd")]
    [InlineData("wrong-pid")]
    [InlineData("wrong-title")]
    [InlineData("wrong-path")]
    [InlineData("relative-path")]
    [InlineData("zero-width")]
    [InlineData("composite")]
    [InlineData("element")]
    [InlineData("remote-target")]
    public async Task ScreenshotMetadataRejectsMismatchedOptionalIdentityOrUnsafeTarget(string scenario)
    {
        string path = Path.Combine(directory, "metadata.png");
        JsonObject metadata = CaptureMetadata(path);
        switch (scenario)
        {
            case "wrong-hwnd": metadata["hwnd"] = 123; break;
            case "wrong-pid": metadata["processId"] = 28; break;
            case "wrong-title": metadata["windowTitle"] = Marker + "-foreign"; break;
            case "wrong-path": metadata["filePath"] = Path.Combine(directory, "other.png"); break;
            case "relative-path": metadata["filePath"] = "metadata.png"; break;
            case "zero-width": metadata["width"] = 0; break;
            case "composite":
                metadata["windows"] = new JsonArray(
                    new JsonObject { ["hwnd"] = 4294967296L, ["title"] = Marker, ["width"] = 1, ["height"] = 1, ["captured"] = true },
                    new JsonObject { ["hwnd"] = 99, ["width"] = 1, ["height"] = 1, ["captured"] = true });
                break;
            case "element": metadata["elementId"] = "pane"; break;
            case "remote-target": metadata["executionTarget"] = new JsonObject(); break;
            default: throw new InvalidOperationException("Неизвестный сценарий теста.");
        }

        HarnessBlockedException failure = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => WithCaptureOutput(path, metadata.ToJsonString()).CaptureAsync(
                OwnedWindow(), path, TestContext.Current.CancellationToken));

        Assert.Contains("Метаданные PNG", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenshotSuccessRequiresAnActualPng()
    {
        string path = Path.Combine(directory, "missing.png");

        HarnessBlockedException failure = await Assert.ThrowsAsync<HarnessBlockedException>(() =>
            WithCaptureOutput(path, CaptureMetadata(path).ToJsonString()).CaptureAsync(
                OwnedWindow(), path, TestContext.Current.CancellationToken));

        Assert.Contains("без файла PNG", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("invalid-signature")]
    [InlineData("wrong-dimensions")]
    public async Task RejectsPngHeaderAndGeometryMismatch(string scenario)
    {
        string path = Path.Combine(directory, "invalid.png");
        WinAppCli cli = new(runner: async (_, arguments, cancellationToken) =>
        {
            if (arguments.SequenceEqual(["ui", "list-windows", "-a", "Terminal", "--json"]))
            {
                return Success(JsonSerializer.Serialize(new[]
                {
                    WindowJson(Marker, OwnedWindow().Hwnd, OwnedWindow().ProcessId,
                        "WindowsTerminal", OwnedWindow().Width, OwnedWindow().Height),
                }));
            }

            byte[] png = OnePixelPng();
            JsonObject metadata = CaptureMetadata(path);
            if (scenario == "truncated")
            {
                png = png[..8];
            }
            else if (scenario == "invalid-signature")
            {
                png[0] = 0;
            }
            else
            {
                metadata["width"] = 2;
            }

            await File.WriteAllBytesAsync(path, png, cancellationToken);
            return Success(metadata.ToJsonString());
        });

        _ = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => cli.CaptureAsync(OwnedWindow(), path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DoesNotOverwriteExistingBaseline()
    {
        string path = Path.Combine(directory, "baseline.png");
        await File.WriteAllBytesAsync(path, OnePixelPng(), TestContext.Current.CancellationToken);
        WinAppCli cli = new(runner: (_, _, _) => throw new InvalidOperationException("CLI не должен запускаться."));

        _ = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => cli.CaptureAsync(OwnedWindow(), path, TestContext.Current.CancellationToken));

        Assert.Equal(OnePixelPng(), await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonzeroExitIncludesBoundedSafeDiagnostic()
    {
        WinAppCli cli = new(runner: (_, _, _) => Task.FromResult(new WinAppProcessResult(5, "", "no_interactive_desktop")));

        HarnessBlockedException failure = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => cli.SelectWindowAsync(Marker, TestContext.Current.CancellationToken));

        Assert.Contains("кодом 5", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no_interactive_desktop", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCliIsBlocked()
    {
        WinAppCli cli = new(runner: (_, _, _) => throw new Win32Exception(2));

        HarnessBlockedException failure = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => cli.GetVersionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("CLI недоступен", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliDeadlineIsBlockedAndCallerCancellationPropagates()
    {
        WinAppCli cli = new(timeoutMs: 10, runner: async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Success("");
        });

        HarnessBlockedException timeout = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => cli.GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.Contains("не завершился", timeout.Message, StringComparison.Ordinal);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cli.GetVersionAsync(cancelled.Token));
    }

    [Fact]
    public async Task BoundedOutputRejectsOversizedMachineReply()
    {
        WinAppCli cli = WithOutput(new string('x', (1024 * 1024) + 1));

        HarnessBlockedException failure = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => cli.SelectWindowAsync(Marker, TestContext.Current.CancellationToken));

        Assert.Contains("1 MiB", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionIsReadFromTheInstalledCli()
    {
        WinAppCli cli = new(runner: (_, arguments, _) =>
        {
            Assert.Equal(["--version"], arguments);
            return Task.FromResult(Success("0.5.0-preview\r\n"));
        });

        Assert.Equal("0.5.0-preview", await cli.GetVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoninteractiveHelpDoesNotRequirePhysicalInputSupport()
    {
        WinAppCli cli = new(runner: (_, arguments, _) =>
        {
            Assert.DoesNotContain("send-keys", arguments);
            return Task.FromResult(Success("ui list-windows screenshot inspect --output --json"));
        });

        IReadOnlyDictionary<string, string> evidence = await cli.ValidateHelpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, evidence.Count);
        Assert.Contains("ui screenshot --help", evidence.Keys);
        Assert.Contains("ui inspect --help", evidence.Keys);
    }

    [Fact]
    public async Task InteractiveHelpRequiresPhysicalFocusSupport()
    {
        WinAppCli cli = WithOutput("ui list-windows screenshot inspect --output --json");

        HarnessBlockedException failure = await Assert.ThrowsAsync<HarnessBlockedException>(() =>
            cli.ValidateHelpAsync(TestContext.Current.CancellationToken, interactive: true));

        Assert.Contains("ui focus --help", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FocusTargetsExactOwnedWindowAndSelector()
    {
        const string selector = "lbl-azurpilotphysic-4b49";
        WinAppCli cli = new(runner: (_, arguments, _) =>
        {
            Assert.Equal(["ui", "focus", selector, "-w", "4294967296", "--json"], arguments);
            return Task.FromResult(Success("{\"hwnd\":4294967296,\"selector\":\"lbl-azurpilotphysic-4b49\",\"focused\":true}"));
        });

        JsonElement metadata = await cli.FocusAsync(OwnedWindow(), selector, TestContext.Current.CancellationToken);

        Assert.Equal(4294967296L, metadata.GetProperty("hwnd").GetInt64());
        Assert.Equal(selector, metadata.GetProperty("selector").GetString());
        Assert.True(metadata.GetProperty("focused").GetBoolean());
    }

    [Theory]
    [InlineData("wrong-hwnd")]
    [InlineData("wrong-selector")]
    public async Task FocusRejectsMetadataForAnotherTarget(string scenario)
    {
        string metadata = scenario switch
        {
            "wrong-hwnd" => "{\"hwnd\":123,\"selector\":\"target\"}",
            "wrong-selector" => "{\"hwnd\":4294967296,\"selector\":\"foreign\"}",
            _ => throw new InvalidOperationException("Неизвестный сценарий теста."),
        };

        _ = await Assert.ThrowsAsync<HarnessBlockedException>(() =>
            WithOutput(metadata).FocusAsync(OwnedWindow(), "target", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SelectWindowPreservesForegroundStateForInputSafety()
    {
        WinAppCli cli = WithOutput(JsonSerializer.Serialize(new[]
        {
            WindowJson(Marker, 4294967296L, 27, "WindowsTerminal", isForeground: true),
        }));

        WinAppWindow window = await cli.SelectWindowAsync(Marker, TestContext.Current.CancellationToken);

        Assert.True(window.IsForeground);
    }

    [Fact]
    public async Task InspectUsesExactHwndAndPreservesRawGeometry()
    {
        WinAppCli cli = new(runner: (_, arguments, _) =>
        {
            Assert.Equal(["ui", "inspect", "-w", "4294967296", "--json"], arguments);
            return Task.FromResult(Success("{\"windows\":[{\"hwnd\":4294967296,\"windowDpi\":144,\"elements\":[]}]}"));
        });

        JsonElement metadata = await cli.InspectAsync(OwnedWindow(), TestContext.Current.CancellationToken);

        Assert.Equal(144, metadata.GetProperty("windows")[0].GetProperty("windowDpi").GetInt32());
    }

    [Fact]
    public async Task InspectRejectsAnotherWindow()
    {
        WinAppCli cli = WithOutput("{\"windows\":[{\"hwnd\":123}]} ");

        _ = await Assert.ThrowsAsync<HarnessBlockedException>(
            () => cli.InspectAsync(OwnedWindow(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PhysicalKeysUseExactHwndAndExplicitSendInput()
    {
        WinAppCli cli = new(runner: (_, arguments, _) =>
        {
            Assert.Equal(["ui", "send-keys", "esc", "-w", "4294967296", "--via", "send-input", "--json"], arguments);
            return Task.FromResult(Success("{\"hwnd\":4294967296,\"via\":\"send-input\",\"actionCount\":1}"));
        });

        JsonElement metadata = await cli.SendKeysAsync(OwnedWindow(), "esc", TestContext.Current.CancellationToken);

        Assert.Equal(1, metadata.GetProperty("actionCount").GetInt32());
    }

    [Fact]
    public async Task PhysicalF2ThenEscapeRemainDeliverableToTheSameExactWindow()
    {
        string[] expected = ["f2", "esc"];
        int index = 0;
        WinAppCli cli = new(runner: (_, arguments, _) =>
        {
            Assert.Equal(["ui", "send-keys", expected[index], "-w", "4294967296", "--via", "send-input", "--json"], arguments);
            index++;
            return Task.FromResult(Success("{\"hwnd\":4294967296,\"via\":\"send-input\",\"actionCount\":1}"));
        });

        JsonElement menuInput = await cli.SendKeysAsync(OwnedWindow(), "f2", TestContext.Current.CancellationToken);
        JsonElement restoreInput = await cli.SendKeysAsync(OwnedWindow(), "esc", TestContext.Current.CancellationToken);

        Assert.Equal(2, index);
        Assert.Equal(4294967296L, menuInput.GetProperty("hwnd").GetInt64());
        Assert.Equal(4294967296L, restoreInput.GetProperty("hwnd").GetInt64());
    }

    [Theory]
    [InlineData("wrong-hwnd")]
    [InlineData("wrong-method")]
    [InlineData("warning")]
    [InlineData("no-actions")]
    public async Task PhysicalKeysRejectUnprovenOrWarnedDelivery(string scenario)
    {
        JsonObject metadata = new() { ["hwnd"] = 4294967296L, ["via"] = "send-input", ["actionCount"] = 1 };
        switch (scenario)
        {
            case "wrong-hwnd": metadata["hwnd"] = 123; break;
            case "wrong-method": metadata["via"] = "uia"; break;
            case "warning": metadata["warnings"] = new JsonArray("foreground_not_target"); break;
            case "no-actions": metadata["actionCount"] = 0; break;
            default: throw new InvalidOperationException("Неизвестный сценарий теста.");
        }

        _ = await Assert.ThrowsAsync<HarnessBlockedException>(() =>
            WithOutput(metadata.ToJsonString()).SendKeysAsync(OwnedWindow(), "esc", TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static object WindowJson(string title, long hwnd, int processId, string processName,
        int width = 800, int height = 600, bool? isForeground = null)
    {
        return new { title, hwnd, processId, processName, width, height, isForeground };
    }

    private static WinAppWindow OwnedWindow()
    {
        return new WinAppWindow(4294967296L, 27, "WindowsTerminal", Marker, 1, 1);
    }

    private static JsonObject CaptureMetadata(string path)
    {
        return new JsonObject
        {
            ["filePath"] = path,
            ["width"] = 1,
            ["height"] = 1,
            ["processId"] = 27,
            ["windowTitle"] = Marker,
            ["hwnd"] = 4294967296L,
            ["windows"] = new JsonArray(new JsonObject
            {
                ["hwnd"] = 4294967296L,
                ["title"] = Marker,
                ["width"] = 1,
                ["height"] = 1,
                ["captured"] = true,
            }),
        };
    }

    private static WinAppCli WithOutput(string output)
    {
        return new WinAppCli(runner: (_, _, _) => Task.FromResult(Success(output)));
    }

    private static WinAppCli WithCaptureOutput(string path, string output, byte[]? png = null)
    {
        return new WinAppCli(runner: async (_, arguments, cancellationToken) =>
        {
            if (arguments.SequenceEqual(["ui", "list-windows", "-a", "Terminal", "--json"]))
            {
                return Success(JsonSerializer.Serialize(new[]
                {
                    WindowJson(Marker, OwnedWindow().Hwnd, OwnedWindow().ProcessId,
                        "WindowsTerminal", OwnedWindow().Width, OwnedWindow().Height),
                }));
            }

            if (png is not null)
            {
                await File.WriteAllBytesAsync(path, png, cancellationToken);
            }

            return Success(output);
        });
    }

    private static WinAppProcessResult Success(string output)
    {
        return new WinAppProcessResult(0, output, "");
    }

    private static byte[] OnePixelPng()
    {
        // Тестовый образец проверяет файловый контракт адаптера и не является свидетельством физической проверки.
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2pAAAAABJRU5ErkJggg==");
    }
}
