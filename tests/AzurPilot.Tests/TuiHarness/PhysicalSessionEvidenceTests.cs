using System.Text.Json;
using AzurPilot.TuiHarness;
using AzurPilot.TuiHarness.Physical;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class PhysicalSessionEvidenceTests
{
    private const string ExeSha256 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string AppDllSha256 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Theory]
    [InlineData("never", true, true)]
    [InlineData("always", true, false)]
    [InlineData("never", false, false)]
    public void PixelAnalysis_PreservesMeasuredRgbAndSelectionWhenUiTextChanges(string adjustment,
        bool focusKnown, bool foregroundComparable)
    {
        byte[] rgba = new byte[60 * 20 * 4];
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 60; x++)
            {
                int offset = ((y * 60) + x) * 4;
                byte value = x >= 25 && x <= 34 && y >= 5 && y <= 14 ? (byte)238 : (byte)12;
                rgba[offset] = rgba[offset + 1] = rgba[offset + 2] = value;
                rgba[offset + 3] = 255;
            }
        }

        PhysicalPngImage image = new(60, 20, rgba);
        TerminalAppearance appearance = TerminalAppearance.Unknown("current", "Тестовый профиль.") with
        {
            Theme = new(StringComparer.Ordinal) { ["background"] = "#0C0C0C", ["foreground"] = "#EEEEEE" },
            OpaqueBackgroundConfirmed = focusKnown,
            PaletteStateConfirmed = focusKnown,
            AdjustIndistinguishableColors = adjustment,
        };
        object[] cells = [Cell(0, " "), Cell(1, "A"), Cell(2, ">")];
        PhysicalSession.PhysicalPngAnalysis Analyze(string line)
        {
            return PhysicalSession.AnalyzePhysicalPixels(image,
            JsonSerializer.SerializeToElement(new
            {
                columns = 3,
                rows = 1,
                geometry = new { cellWidth = 20, cellHeight = 20 },
                lines = new[] { line },
                cells,
            }), new(0, 0, 60, 20), 3, 1, appearance);
        }

        PhysicalSession.PhysicalPngAnalysis before = Analyze("Операция: ошибка предупреждение готов");
        PhysicalSession.PhysicalPngAnalysis after = Analyze("Renamed status and action");

        Assert.Equal("PASS", before.Status);
        Assert.Equal("PASS", after.Status);
        Assert.Equal(2, before.Measurements.Count);
        Assert.Equal(before.Measurements.Select(m => (m.Bounds, m.Kind, m.ActualRgb, m.Expected, m.Status)),
            after.Measurements.Select(m => (m.Bounds, m.Kind, m.ActualRgb, m.Expected, m.Status)));
        PhysicalPixelMeasurement foreground = Assert.Single(after.Measurements, m => m.Kind == "foreground");
        Assert.Equal("PASS", foreground.MeasurementStatus);
        Assert.Equal(new PhysicalRgb(238, 238, 238), foreground.ActualRgb);
        Assert.Equal(foregroundComparable ? "PASS" : "NOT_TESTED", foreground.ComparisonStatus);
        PhysicalPixelMeasurement background = Assert.Single(after.Measurements, m => m.Kind == "background");
        Assert.Equal(new PhysicalRgb(12, 12, 12), background.ActualRgb);
        Assert.Equal(focusKnown ? "PASS" : "NOT_TESTED", background.ComparisonStatus);

        static object Cell(int x, string text)
        {
            return new
            {
                x,
                y = 0,
                text,
                dim = false,
                inverse = false,
                width = 1,
                fg = new { mode = "rgb", value = 0xEEEEEE },
                bg = new { mode = "rgb", value = 0x0C0C0C },
            };
        }
    }

    [Theory]
    [InlineData("PASS")]
    [InlineData("BLOCKED")]
    [InlineData("NOT_TESTED")]
    public async Task ConPtyCapture_PreservesDistinctCloseStatus(string closeStatus)
    {
        string root = Path.Combine(Path.GetTempPath(), "azurpilot-cleanup-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        Dictionary<string, object?> report = new(StringComparer.Ordinal);
        try
        {
            object result = await PhysicalSession.CaptureConPtyAsync("app.exe", root, "profile.json", 80, 25,
                ExeSha256, AppDllSha256, report, command => Task.FromResult(command.Command switch
                {
                    "start" => new HarnessResponse("PASS", "controlled-session", "",
                        new { identity = new { exeSha256 = ExeSha256, assemblySha256 = AppDllSha256 } }),
                    "close" => new HarnessResponse(closeStatus, "controlled-session", "Закрытие проверено."),
                    _ => new HarnessResponse("PASS", "controlled-session", "", new { png = "fixture.png", cells = "fixture.cells.json" }),
                }));

            JsonElement evidence = JsonSerializer.SerializeToElement(result, HarnessJson.Options);
            Assert.Equal("PASS", evidence.GetProperty("binaryIdentity").GetProperty("status").GetString());
            Assert.Equal(closeStatus, evidence.GetProperty("sessionCleanup").GetProperty("status").GetString());
            Assert.Equal(closeStatus, Assert.IsType<PhysicalSession.ConPtySessionCleanupEvidence>(report["conptySessionCleanup"]).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("ipc")]
    [InlineData("io")]
    public async Task ConPtyCapture_CloseExceptionPreservesOriginalCaptureFailureAndEarlierEvidence(string closeFailure)
    {
        string root = Path.Combine(Path.GetTempPath(), "azurpilot-cleanup-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        Dictionary<string, object?> report = new(StringComparer.Ordinal);
        IOException original = new("Исходная ошибка захвата меню.");
        try
        {
            IOException actual = await Assert.ThrowsAsync<IOException>(() => PhysicalSession.CaptureConPtyAsync(
                "app.exe", root, "profile.json", 80, 25, ExeSha256, AppDllSha256, report, command =>
                {
                    if (command.Command == "start")
                    {
                        return Task.FromResult(new HarnessResponse("PASS", "controlled-session", "",
                            new { identity = new { exeSha256 = ExeSha256, assemblySha256 = AppDllSha256 } }));
                    }

                    return command.Command == "close"
                        ? Task.FromException<HarnessResponse>(closeFailure == "io"
                            ? new IOException("Ошибка записи закрытия.") : new HarnessBlockedException("Ошибка IPC закрытия."))
                        : command.Name == "actions-menu" ? Task.FromException<HarnessResponse>(original)
                        : Task.FromResult(new HarnessResponse("PASS", "controlled-session", "", new { png = "startup.png" }));
                }));

            Assert.Same(original, actual);
            Assert.Equal("BLOCKED", Assert.IsType<PhysicalSession.ConPtySessionCleanupEvidence>(report["conptySessionCleanup"]).Status);
            JsonElement partial = JsonSerializer.SerializeToElement(report["conpty"], HarnessJson.Options);
            Assert.Equal("startup.png", partial.GetProperty("paths").GetProperty("png").GetString());
            Assert.True(File.Exists(Path.Combine(root, "conpty-capture.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ConPtyIdentity_PassesOnlyWhenExeAndDllMatchPhysicalRun()
    {
        object data = new
        {
            identity = new
            {
                exeSha256 = ExeSha256.ToLowerInvariant(),
                assemblySha256 = AppDllSha256,
            },
        };

        PhysicalSession.ConPtyBinaryIdentityEvidence evidence = PhysicalSession.AssessConPtyBinaryIdentity(
            data, ExeSha256, AppDllSha256);

        Assert.Equal("PASS", evidence.Status);
        Assert.Equal(ExeSha256.ToLowerInvariant(), evidence.ExeSha256);
        Assert.Equal(AppDllSha256, evidence.AppDllSha256);
    }

    [Fact]
    public void ConPtyIdentity_BlocksWhenExeOrDllDiffersOrIdentityIsMissing()
    {
        object differentExe = new
        {
            identity = new
            {
                exeSha256 = AppDllSha256,
                assemblySha256 = AppDllSha256,
            },
        };
        object differentDll = new
        {
            identity = new
            {
                exeSha256 = ExeSha256,
                assemblySha256 = ExeSha256,
            },
        };

        PhysicalSession.ConPtyBinaryIdentityEvidence exeMismatch = PhysicalSession.AssessConPtyBinaryIdentity(
            differentExe, ExeSha256, AppDllSha256);
        PhysicalSession.ConPtyBinaryIdentityEvidence mismatch = PhysicalSession.AssessConPtyBinaryIdentity(
            differentDll, ExeSha256, AppDllSha256);
        PhysicalSession.ConPtyBinaryIdentityEvidence missing = PhysicalSession.AssessConPtyBinaryIdentity(
            new { directory = "session" }, ExeSha256, AppDllSha256);

        Assert.Equal("BLOCKED", exeMismatch.Status);
        Assert.Equal("BLOCKED", mismatch.Status);
        Assert.Equal(ExeSha256, mismatch.AppDllSha256);
        Assert.Equal("BLOCKED", missing.Status);
    }

    [Fact]
    public async Task ConPtySessionCleanup_RecordsCloseFailureWithoutThrowing()
    {
        PhysicalSession.ConPtySessionCleanupEvidence evidence =
            await PhysicalSession.CaptureConPtySessionCleanupEvidenceAsync(
                () => Task.FromException<HarnessResponse>(new HarnessBlockedException("Владелец не ответил.")));

        Assert.Equal("BLOCKED", evidence.Status);
        Assert.Equal("Владелец не ответил.", evidence.Reason);
    }

    [Fact]
    public void CleanupMarkerAbsence_RecordsEnumerationFailureAsNotTested()
    {
        PhysicalSession.MarkerAbsenceEvidence evidence = PhysicalSession.CheckMarkerAbsence(
            () => throw new HarnessBlockedException("EnumWindows завершился ошибкой."));

        Assert.False(evidence.MarkerAbsent);
        Assert.Equal("NOT_TESTED", evidence.Status);
        Assert.Equal("EnumWindows завершился ошибкой.", evidence.Reason);
    }

    [Fact]
    public void ForegroundSample_RequiresTheMeasuredBlankCellBackground()
    {
        using JsonDocument blank = JsonDocument.Parse("""{"mode":"indexed","value":0}""");
        using JsonDocument same = JsonDocument.Parse("""{"mode":"indexed","value":0}""");
        using JsonDocument different = JsonDocument.Parse("""{"mode":"indexed","value":4}""");
        using JsonDocument equivalentResolvedColor = JsonDocument.Parse("""{"mode":"default","value":0}""");

        Assert.True(PhysicalSession.MatchesMeasuredBackground(same.RootElement, blank.RootElement, 0, 0));
        Assert.False(PhysicalSession.MatchesMeasuredBackground(different.RootElement, blank.RootElement, 4, 0));
        Assert.True(PhysicalSession.MatchesMeasuredBackground(equivalentResolvedColor.RootElement,
            blank.RootElement, 0, 0));
    }

    [Fact]
    public void NumericJsonProperties_ReturnFallbackForWrongValueKindsAndNonObjects()
    {
        using JsonDocument wrongKinds = JsonDocument.Parse(
            """{"integer":null,"long":"7","optionalInteger":false,"optionalDouble":"1.5"}""");
        using JsonDocument nonObject = JsonDocument.Parse("null");

        Assert.Equal(-1, PhysicalSession.IntProperty(wrongKinds.RootElement, "integer"));
        Assert.Equal(-1, PhysicalSession.Int64Property(wrongKinds.RootElement, "long"));
        Assert.Null(PhysicalSession.OptionalInt(wrongKinds.RootElement, "optionalInteger"));
        Assert.Null(PhysicalSession.OptionalDouble(wrongKinds.RootElement, "optionalDouble"));
        Assert.Equal(-1, PhysicalSession.IntProperty(nonObject.RootElement, "integer"));
        Assert.Equal(-1, PhysicalSession.Int64Property(nonObject.RootElement, "long"));
        Assert.Null(PhysicalSession.OptionalInt(nonObject.RootElement, "optionalInteger"));
        Assert.Null(PhysicalSession.OptionalDouble(nonObject.RootElement, "optionalDouble"));
    }
}
