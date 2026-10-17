using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.TuiHarness.Physical;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AzurPilot.TuiHarness;

/// <summary>Управляет принадлежащим проверке окном Windows Terminal для физической контрольной проверки TUI.</summary>
internal static partial class PhysicalSession
{
    private static readonly string[] PixelAnalysisCaveats =
    [
        "Метод захвата остаётся Unknown, если WinApp не сообщает его в JSON именно этого кадра.",
        "ClearType/сглаживание, резервный шрифт для глифов, HDR, Acrylic/Mica и DWM могут изменить края или композицию.",
    ];
    private const int WindowStartupMs = 25_000;
    private const int WindowCleanupMs = 5_000;
    private const int PhysicalSettleMs = 1_000;
    private const int PhysicalForegroundWaitMs = 30_000;
    private const int WmClose = 0x0010;
    private const uint DwmExtendedFrameBounds = 9;
    private const int StdOutputHandle = -11;
    private const uint DesktopReadObjects = 0x0001;
    private const int UserObjectName = 2;
    private const int MaxPhysicalSamples = 12;

    internal static async Task<HarnessResponse> RunAsync(HarnessRequest request)
    {
        string session = Guid.NewGuid().ToString("N");
        string marker = "AzurPilot-Physical-" + session;
        string outputRoot = request.Output is null
            ? Path.Combine(Program.ResolveSessionRoot(null, AppContext.BaseDirectory, Environment.CurrentDirectory), "physical")
            : Path.GetFullPath(request.Output, Environment.CurrentDirectory);
        string directory = Path.Combine(outputRoot, session);
        _ = Directory.CreateDirectory(directory);

        Dictionary<string, object?> report = new(StringComparer.Ordinal)
        {
            ["schemaVersion"] = 1,
            ["session"] = session,
            ["windowMarker"] = marker,
            ["status"] = "NOT_TESTED",
            ["physicalAcceptance"] = "NOT_TESTED",
            ["outputDirectory"] = directory,
            ["interactiveRequested"] = request.Interactive,
            ["requestedTerminalScheme"] = request.TerminalScheme,
            ["requestedColumns"] = request.Columns,
            ["requestedRows"] = request.Rows,
            ["startedUtc"] = DateTimeOffset.UtcNow,
            ["environment"] = new
            {
                operatingSystem = Environment.OSVersion.VersionString,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            },
            ["visualAssessment"] = new
            {
                status = "NOT_TESTED",
                reason = "Harness не кодирует агентский визуальный осмотр; результат сообщается отдельно от машинного отчёта.",
            },
        };
        string reportPath = Path.Combine(directory, "physical-report.json");
        string? exe = null;
        string? expectedExeHash = null;
        string? isolationRoot = null;
        string? userConfigurationPath = null;
        string? userConfigurationHashBefore = null;
        string? conptyMenuCellsPath = null;
        PhysicalPngAnalysis? palettePixelAnalysis = null;
        string? appStartPath = null;
        PhysicalAppStartRecord? appRecord = null;
        WinAppWindow? ownedWindow = null;
        Semaphore? desktopSemaphore = null;
        bool ownsDesktopSemaphore = false;
        string status = "BLOCKED";
        string message = "Физическая проверка не завершена.";
        TerminalAppearance appearance = TerminalAppearance.Unknown(request.TerminalScheme,
            "Точный профиль Windows Terminal ещё не установлен.");
        PhysicalAcceptanceEvidence? acceptanceEvidence = null;

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new HarnessBlockedException("Физическая проверка требует рабочего стола Windows.");
            }

            userConfigurationPath = AzurPilotConfigurationPath.GetDefaultRuntimePath();
            userConfigurationHashBefore = HashIfPresent(userConfigurationPath);

            exe = Path.GetFullPath(request.Exe!, Environment.CurrentDirectory);
            if (!string.Equals(Path.GetFileName(exe), "AzurPilot.App.exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(exe))
            {
                throw new ArgumentException("--exe должен указывать на существующий AzurPilot.App.exe.");
            }

            expectedExeHash = HashFile(exe);
            string appDirectory = Path.GetDirectoryName(exe)!;
            Dictionary<string, string?> binaries = new(StringComparer.OrdinalIgnoreCase)
            {
                ["AzurPilot.App.exe"] = expectedExeHash,
                ["AzurPilot.App.dll"] = HashIfPresent(Path.Combine(appDirectory, "AzurPilot.App.dll")),
                ["AzurPilot.Core.dll"] = HashIfPresent(Path.Combine(appDirectory, "AzurPilot.Core.dll")),
                ["AzurPilot.Windows.dll"] = HashIfPresent(Path.Combine(appDirectory, "AzurPilot.Windows.dll")),
            };
            report["application"] = new
            {
                exe,
                exeSha256 = expectedExeHash,
                dlls = binaries.Where(pair => pair.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                version = FileVersionInfo.GetVersionInfo(exe).ProductVersion,
                arguments = (string[])["--mode", "diagnostic", "--isolated-ui", Path.Combine(directory, "runtime")],
                git = await SessionOwner.SourceIdentityAsync(AppContext.BaseDirectory),
            };

            if (FindExistingMarkerWindows(marker).Count != 0)
            {
                throw new HarnessBlockedException("Уникальный маркер уже принадлежит существующему окну; новый сеанс не запущен.");
            }

            desktopSemaphore = new Semaphore(1, 1, "Local\\AzurPilot.TuiHarness.PhysicalDesktop");
            ownsDesktopSemaphore = desktopSemaphore.WaitOne(0);
            if (!ownsDesktopSemaphore)
            {
                throw new HarnessBlockedException("Другой физический TUI прогон уже владеет семафором рабочего стола.");
            }

            string desktop = GetInputDesktopName();
            if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0
                || !string.Equals(desktop, "Default", StringComparison.OrdinalIgnoreCase))
            {
                throw new HarnessBlockedException("Нет разблокированного интерактивного рабочего стола пользователя.");
            }

            report["desktop"] = new
            {
                status = "PASS",
                sessionId = Process.GetCurrentProcess().SessionId,
                inputDesktop = desktop,
                input = request.Interactive ? "Запрошен; будет отправляться только через winapp send-input в выбранный HWND." : "Не запрошен.",
            };

            WinAppCli winapp = new();
            string winappVersion = await winapp.GetVersionAsync(CancellationToken.None);
            IReadOnlyDictionary<string, string> help = await winapp.ValidateHelpAsync(
                CancellationToken.None, interactive: request.Interactive);
            await File.WriteAllTextAsync(Path.Combine(directory, "winapp-help.json"),
                JsonSerializer.Serialize(help, HarnessJson.Options));
            report["winapp"] = new
            {
                executable = "winapp.exe",
                version = winappVersion,
                help = Path.Combine(directory, "winapp-help.json"),
                requiredCommandsValidated = help.Keys.ToArray(),
                captureMethod = "Указывается для каждого PNG, если WinApp сообщает способ захвата; иначе Unknown.",
                captureMethodReason = "Unknown остаётся Unknown; точный HWND подтверждается отдельно от механизма захвата.",
            };

            string wt = FindExecutable("wt.exe")
                ?? throw new HarnessBlockedException("Windows Terminal wt.exe не найден в PATH.");
            report["windowsTerminal"] = new { executable = wt, version = (string?)null };

            isolationRoot = CreateIsolatedRuntime(directory);
            string isolatedConfigurationPath = Path.Combine(
                isolationRoot, Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath()));
            report["isolatedRuntime"] = new
            {
                root = isolationRoot,
                configurationPath = isolatedConfigurationPath,
                configurationSha256 = HashIfPresent(isolatedConfigurationPath),
                legacyJsonPath = Path.Combine(isolationRoot,
                    Path.GetFileName(AzurPilotConfigurationPath.GetLegacyJsonRuntimePath())),
                legacyJsonExists = File.Exists(Path.Combine(isolationRoot,
                    Path.GetFileName(AzurPilotConfigurationPath.GetLegacyJsonRuntimePath()))),
                appChildProcessesDenied = true,
                policy = "Только diagnostic --isolated-ui; предел job app=1 процесс; MuMu/Android/игра не запускаются и не изменяются.",
            };
            string appDirectoryPath = Path.Combine(directory, "physical");
            _ = Directory.CreateDirectory(appDirectoryPath);
            appStartPath = Path.Combine(appDirectoryPath, "app-start.json");
            string launchRequestPath = Path.Combine(appDirectoryPath, "launch-request.json");
            PhysicalAppLaunchRequest launchRequest = new(
                exe,
                isolationRoot,
                appDirectoryPath,
                appStartPath,
                expectedExeHash,
                HashIfPresent(Path.ChangeExtension(exe, ".dll")),
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
            await File.WriteAllTextAsync(launchRequestPath, JsonSerializer.Serialize(launchRequest, HarnessJson.Options));

            using Process? launcher = StartWindowsTerminal(wt, marker, request, launchRequestPath);
            report["windowsTerminal"] = new
            {
                executable = wt,
                version = GetWindowsTerminalVersion(wt),
                windowName = marker,
                windowMarker = marker,
                columns = request.Columns,
                rows = request.Rows,
            };

            appRecord = await WaitForAppRecordAsync(appStartPath, WindowStartupMs);
            VerifyAppProcess(appRecord, exe, expectedExeHash);
            ownedWindow = await WaitForWindowAsync(winapp, marker, WindowStartupMs);
            if (ownedWindow.ProcessId == appRecord.ProcessId)
            {
                throw new HarnessBlockedException("HWND Windows Terminal и PID приложения неожиданно совпали.");
            }

            VerifyWindow(ownedWindow, marker);
            appearance = ReadTerminalAppearance(request.TerminalScheme, ownedWindow, appRecord.TerminalProfileId);
            report["terminalAppearance"] = appearance.Evidence;
            report["windowsTerminal"] = new
            {
                executable = wt,
                version = GetWindowsTerminalVersion(wt),
                windowName = marker,
                windowMarker = marker,
                columns = request.Columns,
                rows = request.Rows,
                terminalScheme = appearance.SchemeName ?? "UNKNOWN",
                terminalProfile = appearance.ProfileName ?? "UNKNOWN",
                terminalProfileId = appRecord.TerminalProfileId ?? "UNKNOWN",
            };
            if (request.TerminalScheme != "current" && appearance.SchemeName is null)
            {
                report["requestedSchemeResolution"] = new
                {
                    status = "NOT_TESTED",
                    reason = appearance.Limitation ?? "Ожидаемую палитру запрошенной схемы нельзя подтвердить по профилю текущего окна.",
                };
            }

            string profilePath = await WriteComparisonProfileAsync(directory, appearance);
            report["comparisonProfile"] = new
            {
                path = profilePath,
                sha256 = HashIfPresent(profilePath),
                source = appearance.ComparisonReady
                    ? "Фактическая схема выбранного профиля с переопределениями Windows Terminal"
                    : "Запасной профиль только для ConPTY; он не используется как ожидаемые значения физического окна",
            };
            int comparisonColumns = appRecord.Columns is > 0 ? appRecord.Columns.Value : request.Columns;
            int comparisonRows = appRecord.Rows is > 0 ? appRecord.Rows.Value : request.Rows;
            report["requestedGeometry"] = new
            {
                status = appRecord.Columns is null || appRecord.Rows is null ? "NOT_TESTED"
                    : appRecord.Columns == request.Columns && appRecord.Rows == request.Rows ? "PASS" : "DIFFERENT",
                requested = new { columns = request.Columns, rows = request.Rows },
                physical = new { columns = appRecord.Columns, rows = appRecord.Rows },
                conptyComparison = new { columns = comparisonColumns, rows = comparisonRows },
                reason = appRecord.Columns == request.Columns && appRecord.Rows == request.Rows
                    ? "Физическая сетка соответствует запрошенной."
                    : "Windows Terminal может применить фактический размер окна; ConPTY сравнивается с измеренной сеткой production App.",
            };
            object conptyCapture = await CaptureConPtyAsync(exe, directory, profilePath,
                comparisonColumns, comparisonRows, appRecord.ExeSha256, appRecord.AppDllSha256, report);
            report["conpty"] = conptyCapture;
            JsonElement conptyEvidence = JsonSerializer.SerializeToElement(conptyCapture, HarnessJson.Options);
            conptyMenuCellsPath = conptyEvidence.GetProperty("actionMenu").GetProperty("paths")
                .GetProperty("cells").GetString();

            report["ownedSession"] = new
            {
                marker,
                hwnd = ownedWindow.Hwnd,
                windowsTerminalPid = ownedWindow.ProcessId,
                processName = ownedWindow.ProcessName,
                title = ownedWindow.Title,
                appPid = appRecord.ProcessId,
                appHelperPid = appRecord.HelperPid,
                terminalSessionId = appRecord.TerminalSessionId,
                terminalProfileId = appRecord.TerminalProfileId,
                appPath = appRecord.Exe,
                exeSha256 = appRecord.ExeSha256,
                appDllSha256 = appRecord.AppDllSha256,
                actualColumns = appRecord.Columns,
                actualRows = appRecord.Rows,
                consoleGeometryStatus = appRecord.Columns is not null && appRecord.Rows is not null ? "PASS" : "NOT_TESTED",
                startedUtc = appRecord.StartedUtc,
            };

            JsonElement inspect = await winapp.InspectAsync(ownedWindow, CancellationToken.None);
            string inspectPath = Path.Combine(appDirectoryPath, "window-inspect.json");
            await File.WriteAllTextAsync(inspectPath, inspect.GetRawText());

            await Task.Delay(PhysicalSettleMs);
            WinAppCapture startup = await CapturePhysicalAsync(winapp, ownedWindow, Path.Combine(appDirectoryPath, "startup.png"));
            appearance = ReadTerminalAppearance(request.TerminalScheme, ownedWindow, appRecord.TerminalProfileId, startup.Focused);
            report["terminalAppearance"] = appearance.Evidence;
            report["physicalStartup"] = new
            {
                png = startup.Path,
                width = startup.Width,
                height = startup.Height,
                hwnd = ownedWindow.Hwnd,
                processId = ownedWindow.ProcessId,
                captureMethod = startup.CaptureMethod,
                captureLimitation = startup.Limitation,
                targetRevalidated = startup.TargetRevalidated,
                focused = startup.Focused,
                sourceWindowWidth = startup.SourceWindowWidth,
                sourceWindowHeight = startup.SourceWindowHeight,
                compositeHeaderHeight = startup.CompositeHeaderHeight,
                metadata = startup.RawMetadata,
                inspect = inspectPath,
            };
            report["windowGeometry"] = ReadWindowGeometry(
                inspect, ownedWindow, appRecord, startup, request.Columns, request.Rows, out ContentRectangle? content);

            JsonElement xtermCapture = GetCapturePaths(report);
            string cellsPath = xtermCapture.GetProperty("cells").GetString()!;
            PhysicalPngAnalysis pixelAnalysis = await AnalyzePhysicalPngAsync(
                startup.Path, cellsPath, content, appRecord, appearance);
            report["pixelAnalysis"] = pixelAnalysis.Evidence;

            bool visualTransition = false;
            JsonElement? focusEvidence = null;
            if (request.Interactive)
            {
                string textSurfaceSelector = FindTerminalTextSurfaceSelector(inspect);
                WinAppWindow foregroundWindow = await WaitForForegroundWindowAsync(winapp, marker, ownedWindow);
                focusEvidence = await winapp.FocusAsync(foregroundWindow, textSurfaceSelector, CancellationToken.None);
                WinAppWindow focusedWindow = await winapp.SelectWindowAsync(marker, CancellationToken.None);
                if (!SameOwnedWindow(foregroundWindow, focusedWindow) || focusedWindow.IsForeground != true)
                {
                    throw new HarnessBlockedException("После установки фокуса UIA не подтверждены прежние HWND/PID и активность окна.");
                }

                JsonElement inputF2 = await winapp.SendKeysAsync(focusedWindow, "f2", CancellationToken.None);
                await Task.Delay(500);
                WinAppCapture palette = await CapturePhysicalAsync(winapp, ownedWindow, Path.Combine(appDirectoryPath, "palette.png"));
                TerminalAppearance paletteAppearance = ReadTerminalAppearance(request.TerminalScheme, ownedWindow,
                    appRecord.TerminalProfileId, palette.Focused);
                palettePixelAnalysis = conptyMenuCellsPath is null
                    ? new("NOT_TESTED", "Ячейки ConPTY меню действий отсутствуют; сравнение этой физической сцены не выполнялось.", [],
                        new { status = "NOT_TESTED", reason = "Нет ячеек xterm для того же состояния меню действий F2." })
                    : await AnalyzePhysicalPngAsync(palette.Path, conptyMenuCellsPath, content, appRecord, paletteAppearance);
                report["palettePixelAnalysis"] = palettePixelAnalysis.Evidence;
                JsonElement paletteInspect = await winapp.InspectAsync(ownedWindow, CancellationToken.None);
                await File.WriteAllTextAsync(Path.Combine(appDirectoryPath, "palette-inspect.json"), paletteInspect.GetRawText());
                WinAppWindow beforeEscape = await winapp.SelectWindowAsync(marker, CancellationToken.None);
                if (!SameOwnedWindow(focusedWindow, beforeEscape) || beforeEscape.IsForeground != true)
                {
                    throw new HarnessBlockedException("Перед Escape не подтверждены активность окна и идентичность принадлежащего проверке HWND.");
                }

                JsonElement inputEscape = await winapp.SendKeysAsync(beforeEscape, "esc", CancellationToken.None);
                await Task.Delay(500);
                WinAppCapture restored = await CapturePhysicalAsync(winapp, ownedWindow, Path.Combine(appDirectoryPath, "restored.png"));
                PhysicalPngImage beforeImage = PhysicalPngAnalyzer.Decode(await File.ReadAllBytesAsync(startup.Path));
                PhysicalPngImage paletteImage = PhysicalPngAnalyzer.Decode(await File.ReadAllBytesAsync(palette.Path));
                PhysicalPngImage restoredImage = PhysicalPngAnalyzer.Decode(await File.ReadAllBytesAsync(restored.Path));
                double? paletteChange = content is null ? null : ChangedFraction(beforeImage, paletteImage, content);
                double? restoreChange = content is null ? null : ChangedFraction(paletteImage, restoredImage, content);
                visualTransition = paletteChange is >= 0.005 && restoreChange is >= 0.005;
                report["physicalInteraction"] = new
                {
                    status = visualTransition ? "PASS" : "NOT_TESTED",
                    action = "F2 → Escape",
                    inputMode = "WinApp send-keys --via send-input",
                    focus = focusEvidence,
                    focusedHwnd = focusedWindow.Hwnd,
                    focusedWindowForeground = focusedWindow.IsForeground,
                    inputF2,
                    palettePng = palette.Path,
                    inputEscape,
                    restoredPng = restored.Path,
                    paletteInspect = Path.Combine(appDirectoryPath, "palette-inspect.json"),
                    visualDifferenceMetric = "Доля пикселей области содержимого с отличающимся точным RGB; PASS при ≥0.5% на обоих переходах.",
                    paletteChangedFraction = paletteChange,
                    restoreChangedFraction = restoreChange,
                    reason = visualTransition ? "Оба физических кадра отличаются в области содержимого после доставки F2/Escape." :
                        "WinApp подтвердил ввод, но видимый переход не удалось подтвердить по двум PNG и доказанной геометрии содержимого.",
                };
            }
            else
            {
                report["palettePixelAnalysis"] = new
                {
                    status = "NOT_TESTED",
                    reason = "Физическое взаимодействие выключено; PNG меню действий и соответствующие ячейки xterm не создавались.",
                };
                report["physicalInteraction"] = new
                {
                    status = "NOT_TESTED",
                    reason = "Физический ввод выключен по умолчанию; передайте --interactive true для F2/Escape.",
                };
            }

            bool geometryProven = content is not null && appRecord.Columns is > 0 && appRecord.Rows is > 0;
            acceptanceEvidence = new(
                WindowIdentity: "PASS",
                AppIdentity: "PASS",
                PngIntegrity: "PASS",
                ContentGeometry: geometryProven ? "PASS" : "BLOCKED",
                RgbMeasurement: pixelAnalysis.Status,
                SeparateConPtyEvidence: conptyEvidence.GetProperty("binaryIdentity").GetProperty("status").GetString()
                    ?? "NOT_TESTED",
                PhysicalInteraction: visualTransition ? "PASS" : "BLOCKED",
                Cleanup: "NOT_TESTED",
                UserConfigurationUnchanged: "NOT_TESTED",
                CaptureProvenance: startup.CaptureMethod == "Unknown" ? "NOT_TESTED" : "PASS",
                ColorComparison: pixelAnalysis.ComparisonStatus,
                StateComparability: "NOT_TESTED",
                VisualAssessment: "NOT_TESTED",
                ConPtySessionCleanup: conptyEvidence.GetProperty("sessionCleanup").GetProperty("status").GetString()
                    ?? "NOT_TESTED");
            report["physicalAcceptance"] = "NOT_TESTED";
            report["acceptanceNote"] = "Финальный технический статус будет вычислен после проверки освобождения ресурсов и пользовательской конфигурации.";
        }
        catch (Exception exception)
        {
            HarnessResponse failure = exception is InvalidDataException
                ? new HarnessResponse("FAIL", session, exception.Message)
                : SessionOwner.Failure(exception, session);
            status = failure.Status;
            message = failure.Message;
            report["status"] = status;
            report["physicalAcceptance"] = status == "FAIL" ? "FAIL" : "BLOCKED";
            report["failure"] = new { status, message };
        }
        finally
        {
            object cleanup = await CleanupAsync(ownedWindow, appRecord, marker, exe);
            report["cleanup"] = cleanup;
            string cleanupStatus = JsonSerializer.SerializeToElement(cleanup, HarnessJson.Options)
                .TryGetProperty("status", out JsonElement cleanupStatusValue)
                    ? cleanupStatusValue.GetString() ?? "NOT_TESTED" : "NOT_TESTED";
            try
            {
                string? userConfigurationHashAfter = userConfigurationPath is null
                    ? null : HashIfPresent(userConfigurationPath);
                bool configurationUnchanged = userConfigurationHashBefore == userConfigurationHashAfter;
                report["userConfiguration"] = new
                {
                    path = userConfigurationPath,
                    sha256Before = userConfigurationHashBefore,
                    sha256After = userConfigurationHashAfter,
                    unchanged = configurationUnchanged,
                    checkedReadOnly = true,
                };
                if (acceptanceEvidence is not null)
                {
                    acceptanceEvidence = acceptanceEvidence with
                    {
                        Cleanup = cleanupStatus,
                        UserConfigurationUnchanged = configurationUnchanged ? "PASS" : "FAIL",
                    };
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                report["userConfiguration"] = new
                {
                    status = "NOT_TESTED",
                    path = userConfigurationPath,
                    reason = "Не удалось перечитать пользовательскую конфигурацию после физического прогона: " + exception.Message,
                };
                if (acceptanceEvidence is not null)
                {
                    acceptanceEvidence = acceptanceEvidence with
                    {
                        Cleanup = cleanupStatus,
                        UserConfigurationUnchanged = "NOT_TESTED",
                    };
                }
            }

            if (acceptanceEvidence is not null)
            {
                PhysicalAcceptanceResult evaluation = PhysicalAcceptanceEvaluator.Evaluate(acceptanceEvidence, request.Interactive);
                status = evaluation.Status;
                message = evaluation.Message;
                report["status"] = status;
                report["physicalAcceptance"] = evaluation.Status;
                report["acceptanceGates"] = evaluation.Gates;
                report["blockedGates"] = evaluation.BlockedGates;
                report["failedGates"] = evaluation.FailedGates;
                report["acceptanceMessage"] = evaluation.Message;
            }

            report["completedUtc"] = DateTimeOffset.UtcNow;
            try
            {
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, HarnessJson.Options));
            }
            finally
            {
                if (ownsDesktopSemaphore)
                {
                    _ = desktopSemaphore?.Release();
                }

                desktopSemaphore?.Dispose();
            }
        }

        return new HarnessResponse(status, session, message, new { outputDirectory = directory, report = reportPath });
    }

    internal static async Task<int> RunTerminalAppAsync(string requestPath)
    {
        PhysicalAppLaunchRequest request;
        SafeFileHandle? job = null;
        Process? app = null;
        string startedPath = string.Empty;
        try
        {
            request = JsonSerializer.Deserialize<PhysicalAppLaunchRequest>(
                await File.ReadAllTextAsync(requestPath), HarnessJson.Options)
                ?? throw new InvalidDataException("Пустой запрос запуска физической проверки.");
            startedPath = request.AppStartPath;
            using Process owner = OpenOwnedProcess(request.OwnerPid,
                Environment.ProcessPath ?? string.Empty, request.OwnerStartTimeTicks);
            string exe = Path.GetFullPath(request.Exe);
            string isolationRoot = Path.GetFullPath(request.IsolationRoot);
            if (!File.Exists(exe) || !Directory.Exists(isolationRoot) || HashFile(exe) != request.ExpectedExeSha256
                || HashIfPresent(Path.ChangeExtension(exe, ".dll")) != request.ExpectedAppDllSha256)
            {
                throw new HarnessBlockedException("EXE/DLL изменились до запуска дочернего процесса физической проверки.");
            }

            string[] appArguments = ["--mode", "diagnostic", "--isolated-ui", isolationRoot];
            Environment.SetEnvironmentVariable("TERM", "xterm-256color");
            Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
            Environment.SetEnvironmentVariable("TERM_PROGRAM", "Windows Terminal");
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            Environment.SetEnvironmentVariable("FORCE_COLOR", null);

            job = ConPty.CreateJob(denyChildren: true);
            app = ConPty.StartInJob(exe, appArguments, request.WorkingDirectory, job);
            string appPath = ReadProcessPath(app);
            if (!string.Equals(appPath, exe, StringComparison.OrdinalIgnoreCase))
            {
                throw new HarnessBlockedException("Запущенный процесс не совпал с заданным AzurPilot.App.exe.");
            }

            await Task.Delay(PhysicalSettleMs);
            (int? columns, int? rows) = ReadConsoleSize();
            PhysicalAppStartRecord record = new(
                app.Id,
                Environment.ProcessId,
                appPath,
                request.ExpectedExeSha256,
                request.ExpectedAppDllSha256,
                appArguments,
                Environment.GetEnvironmentVariable("WT_SESSION"),
                Environment.GetEnvironmentVariable("WT_PROFILE_ID"),
                DateTimeOffset.UtcNow,
                columns,
                rows,
                app.StartTime.ToUniversalTime().Ticks,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
            await WriteAtomicJsonAsync(startedPath, record);
            await WaitForOwnedAppAsync(app, owner, job, CancellationToken.None);
            await WriteAtomicJsonAsync(Path.Combine(request.WorkingDirectory, "app-exit.json"),
                new { processId = app.Id, exitCode = app.ExitCode, exitedUtc = DateTimeOffset.UtcNow });
            return app.ExitCode;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(startedPath))
            {
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(startedPath)!, "app-start-error.json"),
                        JsonSerializer.Serialize(SessionOwner.Failure(exception, null), HarnessJson.Options));
                }
                catch (IOException)
                {
                    // Ошибка запуска важнее вторичной ошибки записи диагностики.
                }
            }

            return 2;
        }
        finally
        {
            app?.Dispose();
            job?.Dispose();
        }
    }

    internal static async Task WaitForOwnedAppAsync(Process app, Process owner, SafeFileHandle job,
        CancellationToken cancellationToken)
    {
        // Дескриптор владельца удерживает конкретный объект процесса даже после повторного использования PID.
        using CancellationTokenSource watcher = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task appExit = app.WaitForExitAsync(CancellationToken.None);
        Task ownerExit = owner.WaitForExitAsync(watcher.Token);
        try
        {
            await await Task.WhenAny(appExit, ownerExit);
        }
        finally
        {
            job.Dispose();
            await watcher.CancelAsync();
            try { await ownerExit; }
            catch (OperationCanceledException) when (watcher.IsCancellationRequested) { }

            await appExit.WaitAsync(TimeSpan.FromMilliseconds(WindowCleanupMs), CancellationToken.None);
        }
    }

    internal static Process OpenOwnedProcess(int processId, string expectedPath, long expectedStartTimeTicks,
        bool allowExited = false)
    {
        Process process = Process.GetProcessById(processId);
        try
        {
            // Открыть и удержать дескриптор до проверки идентичности и любой попытки завершения.
            _ = process.SafeHandle;
            if (allowExited && process.HasExited)
            {
                return process;
            }

            return expectedStartTimeTicks <= 0 || process.StartTime.ToUniversalTime().Ticks != expectedStartTimeTicks
                || !ReadProcessPath(process)
                    .Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase)
                ? throw new HarnessBlockedException("Не подтверждены путь и время создания исходного процесса.")
                : process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static unsafe string ReadProcessPath(Process process)
    {
        char[] path = new char[32768];
        uint length = (uint)path.Length;
        fixed (char* buffer = path)
        {
            if (!Native.QueryFullProcessImageName(process.SafeHandle, 0, buffer, ref length))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        return Path.GetFullPath(new string(path, 0, (int)length));
    }

    internal static async Task<object> CaptureConPtyAsync(
        string exe, string directory, string profilePath, int columns, int rows,
        string expectedExeSha256, string? expectedAppDllSha256, Dictionary<string, object?> report,
        Func<HarnessRequest, Task<HarnessResponse>>? runner = null)
    {
        Task<HarnessResponse> InvokeAsync(HarnessRequest command)
        {
            return runner is not null ? runner(command)
            : command.Command == "start" ? Program.StartAsync(command) : Program.SendAsync(command);
        }

        string output = Path.Combine(directory, "conpty");
        HarnessResponse started = await InvokeAsync(new HarnessRequest
        {
            Command = "start",
            Exe = exe,
            Output = output,
            Profile = profilePath,
            Mode = "diagnostic",
            Providers = "disabled",
            Columns = columns,
            Rows = rows,
            LifetimeSeconds = 60,
        });
        if (started.Status != "PASS" || started.Session is null)
        {
            throw new HarnessBlockedException("Сравнительная сессия ConPTY не запустилась: " + started.Message);
        }

        Dictionary<string, object?> partial = new(StringComparer.Ordinal)
        {
            ["session"] = started.Session,
            ["outputDirectory"] = output,
            ["status"] = "NOT_TESTED",
        };
        report["conpty"] = partial;
        HarnessResponse capture;
        HarnessResponse actionInput;
        HarnessResponse actionMenu;
        HarnessResponse escapeInput;
        HarnessResponse restored;
        ConPtyBinaryIdentityEvidence binaryIdentity;
        ConPtySessionCleanupEvidence sessionCleanup;
        try
        {
            binaryIdentity = AssessConPtyBinaryIdentity(started.Data, expectedExeSha256, expectedAppDllSha256);
            partial["binaryIdentity"] = binaryIdentity;
            if (binaryIdentity.Status != "PASS")
            {
                throw new HarnessBlockedException("Бинарные файлы ConPTY не совпадают с физическим запуском App: "
                    + binaryIdentity.Reason);
            }

            capture = await InvokeAsync(new HarnessRequest
            {
                Command = "capture",
                Session = started.Session,
                Name = "startup",
                TimeoutMs = 10_000,
            });
            if (capture.Status != "PASS")
            {
                throw new HarnessBlockedException("Не удалось захватить начальный кадр ConPTY: " + capture.Message);
            }

            partial["capture"] = capture.Data;
            partial["paths"] = GetCapturePaths(capture.Data);
            await File.WriteAllTextAsync(Path.Combine(directory, "conpty-capture.json"),
                JsonSerializer.Serialize(capture, HarnessJson.Options));

            actionInput = await InvokeAsync(new HarnessRequest
            {
                Command = "key",
                Session = started.Session,
                Value = "F2",
                TimeoutMs = 10_000,
            });
            if (actionInput.Status != "PASS")
            {
                throw new HarnessBlockedException("Не удалось передать F2 для открытия меню действий ConPTY: " + actionInput.Message);
            }

            actionMenu = await InvokeAsync(new HarnessRequest
            {
                Command = "capture",
                Session = started.Session,
                Name = "actions-menu",
                TimeoutMs = 10_000,
            });
            if (actionMenu.Status != "PASS")
            {
                throw new HarnessBlockedException("Не удалось захватить меню действий ConPTY: " + actionMenu.Message);
            }

            partial["actionMenu"] = actionMenu.Data;

            escapeInput = await InvokeAsync(new HarnessRequest
            {
                Command = "key",
                Session = started.Session,
                Value = "Escape",
                TimeoutMs = 10_000,
            });
            if (escapeInput.Status != "PASS")
            {
                throw new HarnessBlockedException("Не удалось передать Escape в ConPTY: " + escapeInput.Message);
            }

            restored = await InvokeAsync(new HarnessRequest
            {
                Command = "capture",
                Session = started.Session,
                Name = "restored",
                TimeoutMs = 10_000,
            });
            if (restored.Status != "PASS")
            {
                throw new HarnessBlockedException("Не удалось захватить восстановленный кадр ConPTY: " + restored.Message);
            }
        }
        finally
        {
            sessionCleanup = await CaptureConPtySessionCleanupEvidenceAsync(() => InvokeAsync(new HarnessRequest
            {
                Command = "close",
                Session = started.Session,
                TimeoutMs = 5_000,
            }));
            report["conptySessionCleanup"] = sessionCleanup;
            partial["sessionCleanup"] = sessionCleanup;
        }

        return new
        {
            status = "PASS",
            binaryIdentity,
            sessionCleanup,
            outputDirectory = output,
            session = started.Session,
            configurationPath = Path.Combine(output, started.Session,
                "runtime", Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath())),
            configurationSha256 = HashIfPresent(Path.Combine(output, started.Session,
                "runtime",
                Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath()))),
            providers = "disabled",
            grid = new { columns, rows, source = "Фактическая геометрия экранного буфера настоящего App" },
            capture = capture.Data,
            paths = GetCapturePaths(capture.Data),
            actionMenu = new
            {
                input = actionInput.Data,
                capture = actionMenu.Data,
                paths = GetCapturePaths(actionMenu.Data),
            },
            restored = new
            {
                input = escapeInput.Data,
                capture = restored.Data,
                paths = GetCapturePaths(restored.Data),
            },
            evidence = "ConPTY → VT + ячейки xterm + PNG Chromium; отдельный источник свидетельств от физического Windows Terminal.",
        };
    }

    internal static ConPtyBinaryIdentityEvidence AssessConPtyBinaryIdentity(
        object? data, string expectedExeSha256, string? expectedAppDllSha256)
    {
        JsonElement response = JsonSerializer.SerializeToElement(data, HarnessJson.Options);
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("identity", out JsonElement identity)
            || identity.ValueKind != JsonValueKind.Object)
        {
            return new("BLOCKED", null, null, "В начальном ответе ConPTY отсутствует identity.");
        }

        string? exeSha256 = TextProperty(identity, "exeSha256");
        string? appDllSha256 = TextProperty(identity, "assemblySha256");
        bool exeMatches = string.Equals(exeSha256, expectedExeSha256, StringComparison.OrdinalIgnoreCase);
        bool appDllMatches = expectedAppDllSha256 is not null
            && string.Equals(appDllSha256, expectedAppDllSha256, StringComparison.OrdinalIgnoreCase);
        string reason = exeMatches && appDllMatches
            ? "ConPTY сообщает SHA-256 тех же EXE/DLL, что и физический запуск."
            : $"Ожидались EXE={expectedExeSha256}, DLL={expectedAppDllSha256 ?? "<нет>"}; "
                + $"ConPTY сообщает EXE={exeSha256 ?? "<нет>"}, DLL={appDllSha256 ?? "<нет>"}.";
        return new(exeMatches && appDllMatches ? "PASS" : "BLOCKED", exeSha256, appDllSha256, reason);
    }

    internal static async Task<ConPtySessionCleanupEvidence> CaptureConPtySessionCleanupEvidenceAsync(
        Func<Task<HarnessResponse>> closeSession)
    {
        ArgumentNullException.ThrowIfNull(closeSession);
        try
        {
            HarnessResponse response = await closeSession();
            string status = response.Status is "PASS" or "FAIL" or "BLOCKED" or "NOT_TESTED"
                ? response.Status : "NOT_TESTED";
            return new(status, status == "PASS" ? null : response.Message);
        }
        catch (Exception exception) when (exception is HarnessBlockedException or IOException or ObjectDisposedException
            or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        {
            return new("BLOCKED", exception.Message);
        }
    }

    internal static bool MatchesMeasuredBackground(JsonElement cellBackground, JsonElement measuredBackground,
        int? cellRgb, int? measuredRgb)
    {
        return cellBackground.ValueKind == JsonValueKind.Object && measuredBackground.ValueKind == JsonValueKind.Object
            && (string.Equals(cellBackground.GetRawText(), measuredBackground.GetRawText(), StringComparison.Ordinal)
                || (cellRgb is int resolvedCellRgb && measuredRgb == resolvedCellRgb));
    }

    private static JsonElement GetCapturePaths(Dictionary<string, object?> report)
    {
        JsonElement conpty = JsonSerializer.SerializeToElement(report["conpty"], HarnessJson.Options);
        return conpty.GetProperty("paths");
    }

    private static JsonElement GetCapturePaths(object? data)
    {
        JsonElement serialized = JsonSerializer.SerializeToElement(data, HarnessJson.Options);
        return serialized;
    }

    private static async Task<PhysicalPngAnalysis> AnalyzePhysicalPngAsync(
        string pngPath,
        string cellsPath,
        ContentRectangle? content,
        PhysicalAppStartRecord app,
        TerminalAppearance appearance)
    {
        PhysicalPngImage image = PhysicalPngAnalyzer.Decode(await File.ReadAllBytesAsync(pngPath));
        if (content is null || app.Columns is null || app.Rows is null
            || app.Columns <= 0 || app.Rows <= 0)
        {
            return new("NOT_TESTED", "Для цветовых образцов не подтверждены границы содержимого или фактическая сетка ячеек.", [],
                new { status = "NOT_TESTED", reason = "Недостаточно наблюдаемых геометрии; произвольные координаты не подставляются." });
        }

        using JsonDocument cellsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(cellsPath));
        return AnalyzePhysicalPixels(image, cellsDocument.RootElement, content, app.Columns.Value, app.Rows.Value, appearance);
    }

    internal static PhysicalPngAnalysis AnalyzePhysicalPixels(PhysicalPngImage image, JsonElement snapshot,
        ContentRectangle content, int physicalColumns, int physicalRows, TerminalAppearance appearance)
    {
        int columns = snapshot.GetProperty("columns").GetInt32();
        int rows = snapshot.GetProperty("rows").GetInt32();
        if (columns != physicalColumns || rows != physicalRows)
        {
            return new("NOT_TESTED", "Размеры сравниваемых ConPTY и физической сетки ячеек различаются.", [],
                new { status = "NOT_TESTED", xtermColumns = columns, xtermRows = rows, physicalColumns, physicalRows });
        }

        JsonElement xtermGeometry = snapshot.GetProperty("geometry");
        double cellWidth = (double)content.Width / columns;
        double cellHeight = (double)content.Height / rows;
        if (cellWidth < 4 || cellHeight < 5 || cellWidth > 100 || cellHeight > 100)
        {
            return new("NOT_TESTED", "Размеры ячейки вне ожидаемого диапазона границ содержимого UIA.", [],
                new { status = "NOT_TESTED", cellWidth, cellHeight });
        }

        List<PhysicalPixelSample> samples = [];
        JsonElement cellArray = snapshot.GetProperty("cells");
        JsonElement background = cellArray.EnumerateArray()
            .FirstOrDefault(cell => string.IsNullOrWhiteSpace(cell.GetProperty("text").GetString())
                && !cell.GetProperty("dim").GetBoolean() && !cell.GetProperty("inverse").GetBoolean());
        int? measuredBackgroundValue = background.ValueKind == JsonValueKind.Object
            ? ColorSamples.TryResolve(background.GetProperty("bg"), appearance.Theme, "background") : null;
        PhysicalPixelSample? backgroundSample = null;
        PhysicalRgb? observedBackground = null;
        if (background.ValueKind == JsonValueKind.Object)
        {
            int x = background.GetProperty("x").GetInt32();
            int y = background.GetProperty("y").GetInt32();
            PhysicalPixelRectangle rectangle = CellRectangle(content, columns, rows, x, y, 1, 1);
            int? expectedValue = ColorSamples.TryResolve(background.GetProperty("bg"), appearance.Theme, "background");
            PhysicalRgb? expected = appearance.OpaqueBackgroundConfirmed && expectedValue is int expectedRgb
                ? FromRgb(expectedRgb) : null;
            backgroundSample = new("background-blank-cell", rectangle, "background", expected);
            PhysicalPixelMeasurement measurement = PhysicalPngAnalyzer.Analyze(image, [backgroundSample])[0];
            samples.Add(backgroundSample);
            if (measurement.MeasurementStatus == "PASS")
            {
                observedBackground = measurement.ActualRgb;
            }
        }

        Dictionary<string, JsonElement> foregroundSamples = new(StringComparer.Ordinal);
        foreach (JsonElement cell in cellArray.EnumerateArray())
        {
            if (string.IsNullOrWhiteSpace(cell.GetProperty("text").GetString()) || cell.GetProperty("dim").GetBoolean()
                || cell.GetProperty("inverse").GetBoolean() || cell.GetProperty("width").GetInt32() != 1)
            {
                continue;
            }

            int? expected = ColorSamples.TryResolve(cell.GetProperty("fg"), appearance.Theme, "foreground");
            JsonElement cellBackground = cell.GetProperty("bg");
            int? backgroundRgb = ColorSamples.TryResolve(cellBackground, appearance.Theme, "background");
            if (!MatchesMeasuredBackground(cellBackground, background, backgroundRgb, measuredBackgroundValue))
            {
                continue;
            }

            if (expected is int expectedKnown && backgroundRgb is int backgroundKnown && expectedKnown == backgroundKnown)
            {
                continue;
            }

            string expectedKey = expected?.ToString("X6", CultureInfo.InvariantCulture) ?? "UNKNOWN";
            string backgroundKey = backgroundRgb?.ToString("X6", CultureInfo.InvariantCulture) ?? "UNKNOWN";
            int row = cell.GetProperty("y").GetInt32();
            string key = $"{expectedKey}:{backgroundKey}:{row}";
            if (!foregroundSamples.ContainsKey(key))
            {
                foregroundSamples.Add(key, cell.Clone());
            }
        }

        foreach ((string key, JsonElement cell) in foregroundSamples.Take(MaxPhysicalSamples - samples.Count))
        {
            string[] colors = key.Split(':');
            PhysicalRgb? expected = colors[0] == "UNKNOWN" ? null : ParseRgb(colors[0]);
            string semantic = FindSemanticRole(snapshot, cell, expected, appearance.Theme);
            if (!appearance.CanCompareForeground(cell.GetProperty("fg").GetProperty("mode").GetString() ?? string.Empty))
            {
                expected = null;
            }

            int x = cell.GetProperty("x").GetInt32();
            int y = cell.GetProperty("y").GetInt32();
            PhysicalPixelRectangle rectangle = CellRectangle(content, columns, rows, x, y, 1, 1);
            string glyph = cell.GetProperty("text").GetString()!;
            if (observedBackground is not null)
            {
                samples.Add(new($"{semantic}-{EscapeSampleName(glyph)}-{colors[0]}", rectangle, "foreground", expected,
                    observedBackground));
            }
        }

        if (samples.Count == 0)
        {
            return new("NOT_TESTED", "В настоящем TUI не обнаружены однозначные ячейки фона или текста.", [],
                new { status = "NOT_TESTED", reason = "Ни один образец не был создан искусственно." });
        }

        IReadOnlyList<PhysicalPixelMeasurement> measurements = PhysicalPngAnalyzer.Analyze(image, samples);
        int measuredBackground = measurements.Count(measurement => measurement.Kind == "background"
            && measurement.MeasurementStatus == "PASS" && measurement.ActualRgb is not null);
        int measuredForeground = measurements.Count(measurement => measurement.Kind == "foreground"
            && measurement.MeasurementStatus == "PASS" && measurement.ActualRgb is not null);
        string result = measuredBackground >= 1 ? "PASS" : "NOT_TESTED";
        string comparison = measurements.Any(measurement => measurement.ComparisonStatus == "FAIL") ? "DIFFERENT"
            : result == "PASS" && measurements.All(measurement => measurement.ComparisonStatus == "PASS") ? "MATCH" : "NOT_TESTED";
        return new(result, result == "PASS" ? "Измерен фактический RGB фона; измерения текста и сравнение с ожидаемыми цветами отражены отдельно."
            : "Для свидетельства RGB нужен однозначно измеренный фактический RGB фона.", measurements,
            new
            {
                status = result,
                comparisonStatus = comparison,
                metric = "Преобладающий непрозрачный RGB фона и однородный внутренний участок текста 3×3 измеряются независимо от ожидаемой палитры; при известном ожидаемом RGB сравнение точное, без допуска.",
                expectedPalette = appearance.ComparisonReady ? appearance.PaletteSource : "NOT_TESTED",
                foregroundComparisonLimitation = "При неизвестном фокусе или возможной adjustIndistinguishableColors ожидаемый RGB текста не вычисляется.",
                measuredBackgroundSamples = measuredBackground,
                measuredForegroundSamples = measuredForeground,
                notTestedForegroundSamples = measurements.Count(measurement => measurement.Kind == "foreground"
                    && measurement.MeasurementStatus == "NOT_TESTED"),
                contentRectangle = content,
                grid = new
                {
                    columns,
                    rows,
                    physicalCellWidth = cellWidth,
                    physicalCellHeight = cellHeight,
                    xtermCellWidth = xtermGeometry.GetProperty("cellWidth").GetDouble(),
                    xtermCellHeight = xtermGeometry.GetProperty("cellHeight").GetDouble(),
                    physicalToXtermCellScaleX = cellWidth / xtermGeometry.GetProperty("cellWidth").GetDouble(),
                    physicalToXtermCellScaleY = cellHeight / xtermGeometry.GetProperty("cellHeight").GetDouble(),
                    source = "UIA Pane bounds / PseudoConsole screen buffer",
                },
                stateComparability = new
                {
                    status = "NOT_TESTED",
                    reason = "Ячейки ConPTY и физический PNG принадлежат раздельным процессам; соответствие видимых состояний требует визуального осмотра.",
                },
                samples = measurements,
                caveats = PixelAnalysisCaveats.Concat([appearance.Limitation
                    ?? "Цвета профиля и общих настроек были применены в порядке Windows Terminal."]).ToArray(),
            }, comparison);
    }

    private static string FindSemanticRole(JsonElement snapshot, JsonElement cell, PhysicalRgb? expected,
        IReadOnlyDictionary<string, string>? theme)
    {
        string[] lines = [.. snapshot.GetProperty("lines").EnumerateArray().Select(line => line.GetString() ?? string.Empty)];
        int y = cell.GetProperty("y").GetInt32();
        string line = y >= 0 && y < lines.Length ? lines[y] : string.Empty;
        string glyph = cell.GetProperty("text").GetString() ?? string.Empty;
        if (line.TrimStart().StartsWith("> ", StringComparison.Ordinal))
        {
            return "selected-menu";
        }

        if (glyph is "❯" or ">")
        {
            return "accent-text";
        }

        if (expected is PhysicalRgb foreground && foreground == ThemeColor(theme, "foreground"))
        {
            return "primary-text";
        }

        if (line.TrimStart().StartsWith("──", StringComparison.Ordinal))
        {
            string dividerText = line.Trim('─', ' ', '\t');
            return dividerText.Length == 0 ? "accent-border" : "muted-text";
        }

        if (line.StartsWith("Операция:", StringComparison.Ordinal)
            || line.StartsWith("Следующий шаг:", StringComparison.Ordinal))
        {
            return "muted-text";
        }

        if (line.Contains("ошиб", StringComparison.OrdinalIgnoreCase) || line.Contains("отказ", StringComparison.OrdinalIgnoreCase))
        {
            return "error-text";
        }

        if (line.Contains("предупреж", StringComparison.OrdinalIgnoreCase) || line.Contains("внимание", StringComparison.OrdinalIgnoreCase))
        {
            return "warning-text";
        }

        if (line.Contains("готов", StringComparison.OrdinalIgnoreCase) || line.Contains("успеш", StringComparison.OrdinalIgnoreCase))
        {
            return "success-text";
        }

        if (expected is PhysicalRgb muted && muted == ThemeColor(theme, "brightBlack"))
        {
            return "muted-text";
        }

        return "palette-text";
    }

    private static PhysicalPixelRectangle CellRectangle(ContentRectangle content, int columns, int rows,
        int column, int row, int columnSpan, int rowSpan)
    {
        int left = content.X + (int)Math.Floor((double)column * content.Width / columns);
        int top = content.Y + (int)Math.Floor((double)row * content.Height / rows);
        int right = content.X + (int)Math.Ceiling((double)(column + columnSpan) * content.Width / columns);
        int bottom = content.Y + (int)Math.Ceiling((double)(row + rowSpan) * content.Height / rows);
        int insetX = Math.Min(2, Math.Max(0, (right - left) / 5));
        int insetY = Math.Min(2, Math.Max(0, (bottom - top) / 5));
        return new(left + insetX, top + insetY, Math.Max(1, right - left - insetX * 2), Math.Max(1, bottom - top - insetY * 2));
    }

    private static object ReadWindowGeometry(JsonElement inspect, WinAppWindow window,
        PhysicalAppStartRecord app, WinAppCapture capture, int requestedColumns, int requestedRows,
        out ContentRectangle? content)
    {
        content = null;
        JsonElement windows = inspect.GetProperty("windows");
        if (windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() != 1
            || Int64Property(windows[0], "hwnd") != window.Hwnd
            || TextProperty(windows[0], "title") != window.Title)
        {
            return new { status = "NOT_TESTED", reason = "UIA inspect не сохранил единственную точную идентичность HWND и маркера." };
        }

        if (capture.Target.Hwnd != window.Hwnd || capture.Target.ProcessId != window.ProcessId
            || capture.SourceWindowWidth <= 0 || capture.SourceWindowHeight <= 0
            || capture.Width != capture.SourceWindowWidth
            || capture.Height - capture.SourceWindowHeight != capture.CompositeHeaderHeight)
        {
            return new { status = "NOT_TESTED", reason = "WinApp PNG не сохранил геометрию единственного выбранного HWND." };
        }

        JsonElement inspectedWindow = windows[0];
        List<UiRectangle> surfaces = [];
        if (!inspectedWindow.TryGetProperty("elements", out JsonElement elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            return new { status = "NOT_TESTED", reason = "UIA не вернул дерево элементов." };
        }

        foreach (JsonElement element in Descendants(elements))
        {
            if (TextProperty(element, "type") != "Custom"
                || (element.TryGetProperty("isOffscreen", out JsonElement offscreen) && offscreen.ValueKind == JsonValueKind.True))
            {
                continue;
            }

            int x = IntProperty(element, "x");
            int y = IntProperty(element, "y");
            int width = IntProperty(element, "width");
            int height = IntProperty(element, "height");
            if (width >= app.Columns.GetValueOrDefault() * 4
                && height >= app.Rows.GetValueOrDefault() * 5)
            {
                surfaces.Add(new(x, y, width, height, TextProperty(element, "name") ?? string.Empty));
            }
        }

        int? windowDpi = OptionalInt(inspectedWindow, "windowDpi");
        double? scale = OptionalDouble(inspectedWindow, "scale");
        if (windowDpi is null or < 48 or > 960 || scale is null or <= 0 or > 10
            || Math.Abs((double)windowDpi.Value / 96 - scale.Value) > 0.02
            || TextProperty(inspectedWindow, "coordinateSpace") != "physical-screen-pixels")
        {
            return new
            {
                status = "NOT_TESTED",
                reason = "UIA не подтвердил согласованные DPI и физические экранные координаты.",
                windowDpi,
                scale,
                coordinateSpace = TextProperty(inspectedWindow, "coordinateSpace"),
            };
        }

        NativeRect rawBounds = GetVerifiedWindowRect(window);
        NativeRect outerBounds = new(
            ScaleCoordinate(rawBounds.Left, scale.Value),
            ScaleCoordinate(rawBounds.Top, scale.Value),
            ScaleCoordinate(rawBounds.Right, scale.Value),
            ScaleCoordinate(rawBounds.Bottom, scale.Value));
        NativeRect visibleBounds = GetVerifiedVisibleWindowRect(window);
        int outerWidthDiff = Math.Abs(outerBounds.Right - outerBounds.Left - window.Width);
        int outerHeightDiff = Math.Abs(outerBounds.Bottom - outerBounds.Top - window.Height);
        int captureWidthDiff = Math.Abs(visibleBounds.Right - visibleBounds.Left - capture.SourceWindowWidth);
        int captureHeightDiff = Math.Abs(visibleBounds.Bottom - visibleBounds.Top - capture.SourceWindowHeight);
        if (outerWidthDiff > 2 || outerHeightDiff > 2 || captureWidthDiff > 2
            || captureHeightDiff > 2 || surfaces.Count == 0)
        {
            return new
            {
                status = "NOT_TESTED",
                reason = surfaces.Count == 0 ? "UIA не сообщил пригодную Custom-поверхность Windows Terminal."
                    : "Границы HWND с учётом DPI, видимая рамка DWM и геометрия источника WinApp расходятся.",
                winApp = new { window.Width, window.Height },
                win32 = new { width = outerBounds.Right - outerBounds.Left, height = outerBounds.Bottom - outerBounds.Top },
                rawWin32 = new { width = rawBounds.Right - rawBounds.Left, height = rawBounds.Bottom - rawBounds.Top },
                dwmVisibleFrame = visibleBounds,
                capturedWindow = new { capture.SourceWindowWidth, capture.SourceWindowHeight },
                customSurfaceCount = surfaces.Count,
                windowDpi,
                scale,
                dpiAwareness = TextProperty(inspectedWindow, "dpiAwareness"),
                coordinateSpace = TextProperty(inspectedWindow, "coordinateSpace"),
            };
        }

        int largestArea = surfaces.Max(surface => checked(surface.Width * surface.Height));
        UiRectangle[] largest = [.. surfaces.Where(surface => checked(surface.Width * surface.Height) == largestArea)
            .DistinctBy(pane => (pane.X, pane.Y, pane.Width, pane.Height))];
        if (largest.Length != 1 || app.Columns is null || app.Rows is null)
        {
            return new
            {
                status = "NOT_TESTED",
                reason = "UIA Custom-поверхность отсутствует, неоднозначна или размеры физической сетки ячеек не наблюдались.",
                customSurfaceCount = surfaces.Count,
                largestSurfaceCount = largest.Length,
                windowDpi,
                scale,
                dpiAwareness = TextProperty(inspectedWindow, "dpiAwareness"),
                coordinateSpace = TextProperty(inspectedWindow, "coordinateSpace"),
            };
        }

        UiRectangle surface = largest[0];
        ContentRectangle candidate = new(surface.X - visibleBounds.Left,
            surface.Y - visibleBounds.Top + capture.CompositeHeaderHeight, surface.Width, surface.Height);
        if (candidate.X < 0 || candidate.Y < 0 || candidate.X + candidate.Width > capture.Width
            || candidate.Y + candidate.Height > capture.Height)
        {
            return new { status = "NOT_TESTED", reason = "UIA Custom-поверхность находится вне границ HWND захваченного изображения.", surface };
        }

        content = candidate;
        return new
        {
            status = "PASS",
            source = "Точный HWND WinApp, GetWindowRect с учётом DPI, видимая рамка DWM, один захваченный источник WinApp и единственная наибольшая поверхность UIA Custom в физических пикселях; источник координат, текст и ячейки терминала им не подтверждаются",
            contentRectangle = candidate,
            surface = new { surface.Name, surface.X, surface.Y, surface.Width, surface.Height },
            win32Bounds = outerBounds,
            rawWin32Bounds = rawBounds,
            dwmVisibleFrame = visibleBounds,
            windowDpi,
            scale,
            dpiAwareness = TextProperty(inspectedWindow, "dpiAwareness"),
            coordinateSpace = TextProperty(inspectedWindow, "coordinateSpace"),
            captureSource = new
            {
                capture.SourceWindowWidth,
                capture.SourceWindowHeight,
                capture.CompositeHeaderHeight,
                capture.Width,
                capture.Height,
            },
            physicalColumns = app.Columns,
            physicalRows = app.Rows,
            requestedColumns,
            requestedRows,
            requestedGeometryMatches = app.Columns == requestedColumns && app.Rows == requestedRows,
        };
    }

    private static int ScaleCoordinate(int coordinate, double scale)
    {
        double scaled = Math.Round(coordinate * scale, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(scaled) || scaled is < int.MinValue or > int.MaxValue)
        {
            throw new HarnessBlockedException("DPI-преобразование геометрии HWND вышло за допустимый диапазон.");
        }

        return (int)scaled;
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement elements)
    {
        foreach (JsonElement element in elements.EnumerateArray())
        {
            yield return element;
            if (element.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in Descendants(children))
                {
                    yield return child;
                }
            }
        }
    }

    private static string FindTerminalTextSurfaceSelector(JsonElement inspect)
    {
        if (!inspect.TryGetProperty("windows", out JsonElement windows)
            || windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() != 1
            || !windows[0].TryGetProperty("elements", out JsonElement elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            throw new HarnessBlockedException("UIA inspect не содержит дерево выбранного окна для физического ввода.");
        }

        string[] selectors = [.. Descendants(elements)
            .Where(element => TextProperty(element, "type") == "Text"
                && TextProperty(element, "className") == "TermControl"
                && !(element.TryGetProperty("isOffscreen", out JsonElement offscreen)
                    && offscreen.ValueKind == JsonValueKind.True))
            .Select(element => TextProperty(element, "selector"))
            .Where(selector => !string.IsNullOrWhiteSpace(selector))
            .Select(selector => selector!)
            .Distinct(StringComparer.Ordinal)];
        if (selectors.Length != 1)
        {
            throw new HarnessBlockedException("UIA inspect не определил единственную видимую поверхность Terminal TermControl.");
        }

        return selectors[0];
    }

    private static async Task<WinAppWindow> WaitForForegroundWindowAsync(
        WinAppCli winapp, string marker, WinAppWindow ownedWindow)
    {
        WinAppWindow current = await winapp.SelectWindowAsync(marker, CancellationToken.None);
        if (!SameOwnedWindow(ownedWindow, current))
        {
            throw new HarnessBlockedException("Перед физическим вводом изменились HWND/PID/marker принадлежащего окна.");
        }

        if (current.IsForeground == true)
        {
            return current;
        }

        Console.Error.WriteLine($"Активируйте окно Windows Terminal с маркером {marker}; ожидание до "
            + $"{PhysicalForegroundWaitMs / 1000} секунд перед физическим F2/Escape.");
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < PhysicalForegroundWaitMs)
        {
            await Task.Delay(500, CancellationToken.None);
            current = await winapp.SelectWindowAsync(marker, CancellationToken.None);
            if (!SameOwnedWindow(ownedWindow, current))
            {
                throw new HarnessBlockedException("Во время ожидания активности окна изменились HWND/PID/marker окна.");
            }

            if (current.IsForeground == true)
            {
                return current;
            }
        }

        throw new HarnessBlockedException("Пользователь не активировал принадлежащее окно Windows Terminal; физический ввод не отправлен.");
    }

    private static bool SameOwnedWindow(WinAppWindow expected, WinAppWindow actual)
    {
        return actual.Hwnd == expected.Hwnd && actual.ProcessId == expected.ProcessId
            && actual.Title == expected.Title
            && string.Equals(actual.ProcessName, expected.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TextProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    internal static int IntProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : -1;
    }

    internal static long Int64Property(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) ? result : -1;
    }

    internal static int? OptionalInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
    }

    internal static double? OptionalDouble(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) ? result : null;
    }

    private static async Task<WinAppCapture> CapturePhysicalAsync(WinAppCli winapp, WinAppWindow window, string path)
    {
        WinAppCapture capture = await winapp.CaptureAsync(window, path, CancellationToken.None);
        PhysicalPngImage decoded = PhysicalPngAnalyzer.Decode(await File.ReadAllBytesAsync(capture.Path));
        if (decoded.Width != capture.Width || decoded.Height != capture.Height)
        {
            throw new HarnessBlockedException("PNG фактический размер расходится с WinApp metadata.");
        }

        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".winapp.json"),
            JsonSerializer.Serialize(new
            {
                requestedHwnd = window.Hwnd,
                requestedPid = window.ProcessId,
                window.Title,
                capture.Path,
                capture.Width,
                capture.Height,
                capture.SourceWindowWidth,
                capture.SourceWindowHeight,
                capture.CompositeHeaderHeight,
                capture.CaptureMethod,
                capture.Limitation,
                capture.TargetRevalidated,
                capture.Focused,
                capture.RawMetadata,
            }, HarnessJson.Options));
        return capture;
    }

    private static async Task<WinAppWindow> WaitForWindowAsync(WinAppCli winapp, string marker, int timeoutMs)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        Exception? last = null;
        while (elapsed.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                return await winapp.SelectWindowAsync(marker, CancellationToken.None);
            }
            catch (HarnessBlockedException exception)
            {
                last = exception;
                await Task.Delay(250);
            }
        }

        throw new HarnessBlockedException("Ожидание exact HWND Windows Terminal завершилось по таймауту: "
            + (last?.Message ?? "окно не появилось."));
    }

    private static async Task<PhysicalAppStartRecord> WaitForAppRecordAsync(string path, int timeoutMs)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path))
            {
                PhysicalAppStartRecord? record = JsonSerializer.Deserialize<PhysicalAppStartRecord>(
                    await File.ReadAllTextAsync(path), HarnessJson.Options);
                return record ?? throw new HarnessBlockedException("Запись идентичности дочернего процесса физической проверки пуста.");
            }

            string failurePath = Path.Combine(Path.GetDirectoryName(path)!, "app-start-error.json");
            if (File.Exists(failurePath))
            {
                HarnessResponse? failure = JsonSerializer.Deserialize<HarnessResponse>(await File.ReadAllTextAsync(failurePath), HarnessJson.Options);
                throw new HarnessBlockedException(failure?.Message ?? "Дочерний процесс физической проверки не удалось запустить.");
            }

            await Task.Delay(100);
        }

        throw new HarnessBlockedException("Процесс App физической проверки не сообщил PID до истечения срока запуска.");
    }

    private static Process? StartWindowsTerminal(string wt, string marker, HarnessRequest request, string launchRequest)
    {
        ProcessStartInfo start = new(wt) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--window");
        start.ArgumentList.Add(marker);
        start.ArgumentList.Add("--size");
        start.ArgumentList.Add($"{request.Columns},{request.Rows}");
        start.ArgumentList.Add("new-tab");
        start.ArgumentList.Add("--title");
        start.ArgumentList.Add(marker);
        start.ArgumentList.Add("--suppressApplicationTitle");
        if (request.TerminalScheme != "current")
        {
            start.ArgumentList.Add("--colorScheme");
            start.ArgumentList.Add(request.TerminalScheme);
        }

        string processPath = Environment.ProcessPath ?? throw new HarnessBlockedException("Не определён путь TuiHarness launcher.");
        start.ArgumentList.Add(processPath);
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        start.ArgumentList.Add("__physical-app");
        start.ArgumentList.Add(launchRequest);
        return Process.Start(start);
    }

    private static void VerifyAppProcess(PhysicalAppStartRecord record, string expectedExe, string expectedHash)
    {
        if (record.ProcessId <= 0 || record.HelperPid <= 0
            || !Path.GetFullPath(record.Exe).Equals(expectedExe, StringComparison.OrdinalIgnoreCase)
            || record.ExeSha256 != expectedHash || (!string.IsNullOrEmpty(record.TerminalSessionId)
                && record.TerminalSessionId.Length > 128))
        {
            throw new HarnessBlockedException("Запись идентичности дочернего процесса не совпала с запрошенным EXE и аргументами.");
        }

        using Process process = OpenOwnedProcess(record.ProcessId, expectedExe, record.ProcessStartTimeTicks);
        if (process.HasExited)
        {
            throw new HarnessBlockedException("Фактический PID не принадлежит запущенному AzurPilot.App.exe.");
        }
    }

    private static void VerifyWindow(WinAppWindow window, string marker)
    {
        if (window.Hwnd <= 0 || window.ProcessId <= 0 || window.Title != marker
            || !window.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessBlockedException("Выбранное HWND не принадлежит точному окну Windows Terminal, принадлежащему проверке.");
        }

        _ = GetVerifiedWindowRect(window);
    }

    private static NativeRect GetVerifiedWindowRect(WinAppWindow window)
    {
        nint handle = (nint)window.Hwnd;
        if (!Native.IsWindow(handle)
            || Native.GetWindowThreadProcessId(handle, out uint processId) == 0 || processId != window.ProcessId
            || !Native.GetWindowRect(handle, out NativeRect rect))
        {
            throw new HarnessBlockedException("Принадлежность и геометрия HWND из Win32 не совпали с точным окном WinApp.");
        }

        string title = GetWindowTitle(handle);
        if (!string.Equals(title, window.Title, StringComparison.Ordinal))
        {
            throw new HarnessBlockedException("Заголовок HWND изменился после выбора; окно не будет адресовано.");
        }

        return rect;
    }

    private static NativeRect GetVerifiedVisibleWindowRect(WinAppWindow window)
    {
        _ = GetVerifiedWindowRect(window);
        int result = Native.DwmGetWindowAttribute(
            (nint)window.Hwnd, DwmExtendedFrameBounds, out NativeRect bounds,
            (uint)Marshal.SizeOf<NativeRect>());
        if (result != 0 || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            throw new HarnessBlockedException("DWM не подтвердил физические видимые границы выбранного HWND.");
        }

        return bounds;
    }

    private static string GetWindowTitle(nint handle)
    {
        const int Capacity = 512;
        nint buffer = Marshal.AllocHGlobal(Capacity * sizeof(char));
        try
        {
            int count = Native.GetWindowText(handle, buffer, Capacity);
            return count <= 0 ? string.Empty : Marshal.PtrToStringUni(buffer, count) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<Win32Window> FindExistingMarkerWindows(string marker)
    {
        List<Win32Window> matches = [];
        if (!Native.EnumWindows((handle, _) =>
        {
            if (!Native.IsWindow(handle) || !string.Equals(GetWindowTitle(handle), marker, StringComparison.Ordinal))
            {
                return true;
            }

            uint threadId = Native.GetWindowThreadProcessId(handle, out uint processId);
            if (threadId == 0 || processId is 0 or > int.MaxValue)
            {
                return true;
            }

            try
            {
                using Process process = Process.GetProcessById((int)processId);
                string? path = process.MainModule?.FileName;
                if (process.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
                    && path is not null && Path.GetFileName(path).Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new(handle, (int)processId));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Неизвестный процесс с таким случайным заголовком не считается тестовым окном.
            }

            return true;
        }, 0))
        {
            throw new HarnessBlockedException("Windows не смог перечислить окна для проверки маркера.");
        }

        return matches;
    }

    private static async Task<object> CleanupAsync(WinAppWindow? window, PhysicalAppStartRecord? app,
        string marker, string? expectedExe)
    {
        bool windowCloseRequested = false;
        string windowCloseStatus = "NOT_TESTED";
        try
        {
            Win32Window? candidate = window is null
                ? FindExistingMarkerWindows(marker).SingleOrDefault()
                : new(window.Hwnd, window.ProcessId);
            if (candidate is Win32Window exact && IsOwnedWindow(exact, marker))
            {
                windowCloseRequested = Native.PostMessage((nint)exact.Hwnd, WmClose, 0, 0);
                windowCloseStatus = windowCloseRequested ? "PASS" : "BLOCKED";
                Stopwatch timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < WindowCleanupMs && Native.IsWindow((nint)exact.Hwnd))
                {
                    if (!IsOwnedWindow(exact, marker))
                    {
                        break;
                    }

                    await Task.Delay(100);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or HarnessBlockedException)
        {
            windowCloseStatus = "BLOCKED: " + exception.Message;
        }

        List<string> processCleanup = [];
        if (app is not null)
        {
            if (expectedExe is not null)
            {
                processCleanup.Add("AzurPilot.App:" + KillOwnedProcess(app.ProcessId, expectedExe, app.ProcessStartTimeTicks));
            }

            string helperPath = Environment.ProcessPath ?? string.Empty;
            processCleanup.Add("physical-helper:" + KillOwnedProcess(app.HelperPid, helperPath, app.HelperStartTimeTicks));
        }

        MarkerAbsenceEvidence markerAbsence = CheckMarkerAbsence(() => FindExistingMarkerWindows(marker).Count == 0);
        bool windowCloseConfirmed = window is null
            ? markerAbsence.MarkerAbsent && (windowCloseStatus is "NOT_TESTED" or "PASS")
            : windowCloseStatus == "PASS" && !Native.IsWindow((nint)window.Hwnd);
        bool processesConfirmed = app is null
            ? processCleanup.Count == 0
            : processCleanup.Count == 2 && processCleanup.All(ProcessCleanupConfirmed);
        bool cleanupConfirmed = windowCloseConfirmed && processesConfirmed && markerAbsence.Status == "PASS";
        return new
        {
            status = cleanupConfirmed ? "PASS" : markerAbsence.Status == "BLOCKED" ? "BLOCKED" : "NOT_TESTED",
            ownedWindowCloseRequested = windowCloseRequested,
            ownedWindowCloseStatus = windowCloseStatus,
            ownedWindowClosed = windowCloseConfirmed,
            markerAbsent = markerAbsence.MarkerAbsent,
            markerAbsenceStatus = markerAbsence.Status,
            markerAbsenceReason = markerAbsence.Reason,
            exactWindowTitleRequired = marker,
            processes = processCleanup,
            note = "WindowsTerminal.exe не завершается целиком; освобождение ресурсов адресует только HWND с точным маркером и PID собственных дочерних процессов.",
        };
    }

    internal static MarkerAbsenceEvidence CheckMarkerAbsence(Func<bool> markerIsAbsent)
    {
        ArgumentNullException.ThrowIfNull(markerIsAbsent);
        try
        {
            bool absent = markerIsAbsent();
            return absent
                ? new(true, "PASS", null)
                : new(false, "BLOCKED", "Маркерное окно осталось после освобождения ресурсов.");
        }
        catch (HarnessBlockedException exception)
        {
            return new(false, "NOT_TESTED", exception.Message);
        }
    }

    private static bool ProcessCleanupConfirmed(string result)
    {
        return result.EndsWith(":PASS", StringComparison.Ordinal)
            || result.EndsWith(":PASS:already-exited", StringComparison.Ordinal);
    }

    private static bool IsOwnedWindow(Win32Window window, string marker)
    {
        nint handle = (nint)window.Hwnd;
        if (!Native.IsWindow(handle) || GetWindowTitle(handle) != marker
            || Native.GetWindowThreadProcessId(handle, out uint pid) == 0 || pid != window.ProcessId)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(window.ProcessId);
            return process.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(process.MainModule?.FileName ?? string.Empty)
                    .Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static string KillOwnedProcess(int processId, string expectedPath, long expectedStartTimeTicks)
    {
        if (processId <= 0 || string.IsNullOrWhiteSpace(expectedPath))
        {
            return "NOT_TESTED";
        }

        try
        {
            if (!Path.IsPathFullyQualified(expectedPath))
            {
                return "NOT_TESTED";
            }

            _ = Path.GetFullPath(expectedPath);
        }
        catch (ArgumentException)
        {
            return "BLOCKED:invalid-path";
        }

        try
        {
            using Process process = OpenOwnedProcess(processId, expectedPath, expectedStartTimeTicks, allowExited: true);

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                if (!process.WaitForExit(WindowCleanupMs))
                {
                    return "BLOCKED:cleanup-timeout";
                }
            }

            return "PASS";
        }
        catch (ArgumentException)
        {
            return "PASS:already-exited";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or HarnessBlockedException)
        {
            return "BLOCKED:" + exception.Message;
        }
    }

    private static double? ChangedFraction(PhysicalPngImage before, PhysicalPngImage after, ContentRectangle area)
    {
        if (before.Width != after.Width || before.Height != after.Height
            || area.X < 0 || area.Y < 0 || area.X + area.Width > before.Width || area.Y + area.Height > before.Height)
        {
            return null;
        }

        long changed = 0;
        long compared = 0;
        for (int y = area.Y; y < area.Y + area.Height; y++)
        {
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                if (!before.TryGetOpaquePixel(x, y, out PhysicalRgb first)
                    || !after.TryGetOpaquePixel(x, y, out PhysicalRgb second))
                {
                    continue;
                }

                compared++;
                if (first != second)
                {
                    changed++;
                }
            }
        }

        return compared == 0 ? null : (double)changed / compared;
    }

    private static string CreateIsolatedRuntime(string directory)
    {
        string root = Path.Combine(directory, "runtime");
        _ = Directory.CreateDirectory(root);
        string config = Path.Combine(root, Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath()));
        string legacy = Path.Combine(root, Path.GetFileName(AzurPilotConfigurationPath.GetLegacyJsonRuntimePath()));
        ApplicationResult<AzurPilotConfigurationSnapshot> save = AzurPilotConfigurationStore.Save(
            config,
            legacy,
            AzurPilotConfigurationLoader.Load(config).Value!,
            AzurPilotConfigurationDefaults.Create(),
            out _);
        if (save.IsFailure)
        {
            throw new HarnessBlockedException(save.FailureInfo!.Message);
        }

        return root;
    }

    private static async Task<string> WriteComparisonProfileAsync(string directory, TerminalAppearance appearance)
    {
        if (appearance.Theme is null)
        {
            string defaultProfile = Path.Combine(AppContext.BaseDirectory, "profiles", "campbell.json");
            return defaultProfile;
        }

        Dictionary<string, object?> profile = new(StringComparer.Ordinal)
        {
            ["name"] = $"Physical comparison: {appearance.SchemeName}",
            ["lineHeight"] = 1,
            ["letterSpacing"] = 0,
            ["cursorStyle"] = "bar",
            ["theme"] = appearance.Theme,
        };
        if (appearance.FontFamily is not null)
        {
            profile["fontFamily"] = appearance.FontFamily;
        }

        if (appearance.FontSize is int fontSize)
        {
            profile["fontSize"] = fontSize;
        }

        string path = Path.Combine(directory, "comparison-profile.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile, HarnessJson.Options));
        return path;
    }

    private static TerminalAppearance ReadTerminalAppearance(string requestedScheme, WinAppWindow window, string? profileId,
        bool? focused = null)
    {
        string? settingsPath;
        string? terminalPath;
        try
        {
            using Process terminal = Process.GetProcessById(window.ProcessId);
            terminalPath = terminal.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return TerminalAppearance.Unknown(requestedScheme,
                "Не удалось разрешить executable точного Windows Terminal PID: " + exception.Message,
                profileId: profileId);
        }

        if (string.IsNullOrWhiteSpace(terminalPath))
        {
            return TerminalAppearance.Unknown(requestedScheme,
                "Точный Windows Terminal PID не сообщил путь исполняемого файла.", profileId: profileId);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        settingsPath = TerminalAppearanceResolver.ResolveSettingsPath(terminalPath, local);
        if (settingsPath is null)
        {
            return TerminalAppearance.Unknown(requestedScheme,
                "Не найден settings.json пакета, которому принадлежит исполняемый файл выбранного Windows Terminal.",
                profileId: profileId);
        }

        string? terminalDefaultsPath = TerminalAppearanceResolver.ResolveDefaultsPath(terminalPath);
        string? terminalDefaultsJson = null;
        string? terminalDefaultsHash = null;
        string? terminalDefaultsReadError = null;
        if (terminalDefaultsPath is not null && File.Exists(terminalDefaultsPath))
        {
            try
            {
                terminalDefaultsJson = File.ReadAllText(terminalDefaultsPath);
                terminalDefaultsHash = HashFile(terminalDefaultsPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                terminalDefaultsReadError = exception.Message;
            }
        }

        try
        {
            string json = File.ReadAllText(settingsPath);
            string? systemTheme = ReadSystemApplicationTheme();
            return TerminalAppearanceResolver.Resolve(json, requestedScheme, profileId, systemTheme,
                settingsPath, HashFile(settingsPath), terminalDefaultsJson, terminalDefaultsPath,
                terminalDefaultsHash, terminalDefaultsReadError, focused);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return TerminalAppearance.Unknown(requestedScheme, "settings.json невозможно прочитать: " + exception.Message,
                settingsPath, profileId);
        }
    }

    private static string? ReadSystemApplicationTheme()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int isLight ? isLight == 0 ? "dark" : "light" : null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? FindExecutable(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), name))
            .FirstOrDefault(File.Exists);
    }

    private static string? GetWindowsTerminalVersion(string path)
    {
        try
        {
            string? terminal = Path.Combine(Path.GetDirectoryName(path)!, "WindowsTerminal.exe");
            return File.Exists(terminal) ? FileVersionInfo.GetVersionInfo(terminal).ProductVersion : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static PhysicalRgb FromRgb(int value) => new((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private static PhysicalRgb? ThemeColor(IReadOnlyDictionary<string, string>? theme, string name)
    {
        return theme is not null && theme.TryGetValue(name, out string? value)
            ? ParseRgb(value[1..]) : null;
    }

    private static PhysicalRgb ParseRgb(string value)
    {
        return new(byte.Parse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static string EscapeSampleName(string text)
    {
        char[] safeCharacters = [.. text.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(16)];
        string safe = new(safeCharacters);
        return safe.Length == 0 ? "glyph" : safe;
    }

    private static string? HashIfPresent(string path) => File.Exists(path) ? HashFile(path) : null;

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task WriteAtomicJsonAsync<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, HarnessJson.Options));
        File.Move(temporary, path, overwrite: true);
    }

    private static (int? Columns, int? Rows) ReadConsoleSize()
    {
        nint handle = Native.GetStdHandle(StdOutputHandle);
        if (handle is 0 or -1 || !Native.GetConsoleScreenBufferInfo(handle, out ConsoleScreenBufferInfo info)
            || info.Window.Right < info.Window.Left || info.Window.Bottom < info.Window.Top)
        {
            return (null, null);
        }

        return (info.Window.Right - info.Window.Left + 1, info.Window.Bottom - info.Window.Top + 1);
    }
    private static string GetInputDesktopName()
    {
        using SafeFileHandle desktop = Native.OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop.IsInvalid)
        {
            throw new HarnessBlockedException("Windows не предоставила текущий рабочий стол ввода.");
        }

        const int Capacity = 256;
        nint buffer = Marshal.AllocHGlobal(Capacity * sizeof(char));
        try
        {
            if (!Native.GetUserObjectInformation(desktop, UserObjectName, buffer,
                Capacity * sizeof(char), out _))
            {
                throw new HarnessBlockedException("Не удалось определить имя текущего рабочего стола ввода.");
            }

            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    private sealed record PhysicalAppLaunchRequest(string Exe, string IsolationRoot, string WorkingDirectory,
        string AppStartPath, string ExpectedExeSha256, string? ExpectedAppDllSha256,
        int OwnerPid, long OwnerStartTimeTicks);

    private sealed record PhysicalAppStartRecord(int ProcessId, int HelperPid, string Exe, string ExeSha256,
        string? AppDllSha256, string[] Arguments, string? TerminalSessionId, string? TerminalProfileId,
        DateTimeOffset StartedUtc, int? Columns, int? Rows,
        long ProcessStartTimeTicks, long HelperStartTimeTicks);

    internal sealed record ConPtyBinaryIdentityEvidence(string Status, string? ExeSha256,
        string? AppDllSha256, string Reason);
    internal sealed record ConPtySessionCleanupEvidence(string Status, string? Reason);
    internal sealed record MarkerAbsenceEvidence(bool MarkerAbsent, string Status, string? Reason);

    internal sealed record ContentRectangle(int X, int Y, int Width, int Height);
    private sealed record UiRectangle(int X, int Y, int Width, int Height, string Name);
    private sealed record Win32Window(long Hwnd, int ProcessId);
    internal sealed record PhysicalPngAnalysis(string Status, string Reason,
        IReadOnlyList<PhysicalPixelMeasurement> Measurements, object Evidence,
        string ComparisonStatus = "NOT_TESTED");

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        internal readonly int Left, Top, Right, Bottom;

        internal NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ConsoleCoord
    {
        internal readonly short X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ConsoleSmallRect
    {
        internal readonly short Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ConsoleScreenBufferInfo
    {
        internal readonly ConsoleCoord Size, CursorPosition;
        internal readonly short Attributes;
        internal readonly ConsoleSmallRect Window;
        internal readonly ConsoleCoord MaximumWindowSize;
    }

    private static partial class Native
    {
        [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
            char* path, ref uint length);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindow(nint hwnd);
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
        internal static extern int GetWindowText(nint hwnd, nint text, int maxCount);
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowRect(nint hwnd, out NativeRect rect);
        [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute", SetLastError = true)]
        internal static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, out NativeRect value, uint valueSize);
        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EnumWindows(EnumWindowsCallback callback, nint parameter);
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial SafeFileHandle OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint desiredAccess);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformation(SafeFileHandle handle, int index,
            nint buffer, uint length, out uint needed);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nint GetStdHandle(int standardHandle);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetConsoleScreenBufferInfo(nint output, out ConsoleScreenBufferInfo information);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    }
}
