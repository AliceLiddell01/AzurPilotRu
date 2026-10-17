using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.TuiHarness.Physical;
using Microsoft.Win32.SafeHandles;

namespace AzurPilot.TuiHarness;

/// <summary>Оркестрирует отдельное owned-окно Windows Terminal для физического TUI smoke.</summary>
internal static partial class PhysicalSession
{
    private static readonly string[] PixelAnalysisCaveats =
    [
        "WGC/PrintWindow источник остаётся Unknown, пока screenshot JSON его не сообщает.",
        "ClearType/сглаживание, glyph fallback, HDR, Acrylic/Mica и DWM могут изменить края или композицию.",
    ];
    private const int WindowStartupMs = 25_000;
    private const int WindowCleanupMs = 5_000;
    private const int PhysicalSettleMs = 1_000;
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
            ["visualAssessment"] = new { status = "NOT_TESTED", reason = "Ключевые PNG ещё не открывались для визуального осмотра." },
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

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new HarnessBlockedException("Физическая проверка требует Windows desktop.");
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
                throw new HarnessBlockedException("Уникальный marker уже принадлежит существующему окну; новый сеанс не запущен.");
            }

            desktopSemaphore = new Semaphore(1, 1, "Local\\AzurPilot.TuiHarness.PhysicalDesktop");
            ownsDesktopSemaphore = desktopSemaphore.WaitOne(0);
            if (!ownsDesktopSemaphore)
            {
                throw new HarnessBlockedException("Другой физический TUI прогон уже владеет desktop semaphore.");
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
                captureMethod = "Reported per PNG when WinApp returns a capture mode; otherwise Unknown.",
                captureMethodReason = "WinApp documentation defines WGC as the default and PrintWindow as fallback; acceptance requires mode evidence from the exact capture.",
            };

            string wt = FindExecutable("wt.exe")
                ?? throw new HarnessBlockedException("Windows Terminal wt.exe не найден в PATH.");
            report["windowsTerminal"] = new { executable = wt, version = (string?)null };

            TerminalAppearance appearance = ReadTerminalAppearance(request.TerminalScheme);
            report["terminalAppearance"] = appearance.Evidence;
            if (request.TerminalScheme != "current" && appearance.SchemeName is null)
            {
                throw new HarnessBlockedException(appearance.Limitation
                    ?? "Запрошенная цветовая схема отсутствует в прочитанных settings.json Windows Terminal.");
            }

            string profilePath = await WriteComparisonProfileAsync(directory, appearance);
            report["comparisonProfile"] = new { path = profilePath, sha256 = HashIfPresent(profilePath) };
            object conptyCapture = await CaptureConPtyAsync(request, exe, directory, profilePath);
            report["conpty"] = conptyCapture;
            JsonElement conptyEvidence = JsonSerializer.SerializeToElement(conptyCapture, HarnessJson.Options);
            conptyMenuCellsPath = conptyEvidence.GetProperty("actionMenu").GetProperty("paths")
                .GetProperty("cells").GetString();

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
                HashIfPresent(Path.ChangeExtension(exe, ".dll")));
            await File.WriteAllTextAsync(launchRequestPath, JsonSerializer.Serialize(launchRequest, HarnessJson.Options));

            using Process? launcher = StartWindowsTerminal(wt, marker, request, appearance, launchRequestPath);
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
            };

            appRecord = await WaitForAppRecordAsync(appStartPath, WindowStartupMs);
            VerifyAppProcess(appRecord, exe, expectedExeHash);
            ownedWindow = await WaitForWindowAsync(winapp, marker, WindowStartupMs);
            if (ownedWindow.ProcessId == appRecord.ProcessId)
            {
                throw new HarnessBlockedException("HWND Windows Terminal и PID приложения неожиданно совпали.");
            }

            VerifyWindow(ownedWindow, marker);
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
                sourceWindowWidth = startup.SourceWindowWidth,
                sourceWindowHeight = startup.SourceWindowHeight,
                compositeHeaderHeight = startup.CompositeHeaderHeight,
                metadata = startup.RawMetadata,
                inspect = inspectPath,
            };
            report["windowGeometry"] = ReadWindowGeometry(
                inspect, ownedWindow, appRecord, startup, out ContentRectangle? content);

            JsonElement xtermCapture = GetCapturePaths(report);
            string cellsPath = xtermCapture.GetProperty("cells").GetString()!;
            PhysicalPngAnalysis pixelAnalysis = await AnalyzePhysicalPngAsync(
                startup.Path, cellsPath, content, appRecord, appearance);
            report["pixelAnalysis"] = pixelAnalysis.Evidence;

            bool visualTransition = false;
            if (request.Interactive)
            {
                JsonElement inputF2 = await winapp.SendKeysAsync(ownedWindow, "f2", CancellationToken.None);
                await Task.Delay(500);
                WinAppCapture palette = await CapturePhysicalAsync(winapp, ownedWindow, Path.Combine(appDirectoryPath, "palette.png"));
                palettePixelAnalysis = conptyMenuCellsPath is null
                    ? new("NOT_TESTED", "ConPTY action-menu cells отсутствуют; сравнение этой физической сцены не выполнялось.", [],
                        new { status = "NOT_TESTED", reason = "Нет xterm cells для того же F2 action-menu состояния." })
                    : await AnalyzePhysicalPngAsync(palette.Path, conptyMenuCellsPath, content, appRecord, appearance);
                report["palettePixelAnalysis"] = palettePixelAnalysis.Evidence;
                JsonElement paletteInspect = await winapp.InspectAsync(ownedWindow, CancellationToken.None);
                await File.WriteAllTextAsync(Path.Combine(appDirectoryPath, "palette-inspect.json"), paletteInspect.GetRawText());
                JsonElement inputEscape = await winapp.SendKeysAsync(ownedWindow, "esc", CancellationToken.None);
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
                    inputF2,
                    palettePng = palette.Path,
                    inputEscape,
                    restoredPng = restored.Path,
                    paletteInspect = Path.Combine(appDirectoryPath, "palette-inspect.json"),
                    visualDifferenceMetric = "Доля пикселей content rectangle с отличающимся точным RGB; PASS при ≥0.5% на обоих переходах.",
                    paletteChangedFraction = paletteChange,
                    restoreChangedFraction = restoreChange,
                    reason = visualTransition ? "Оба физических кадра отличаются в content rectangle после доставки F2/Escape." :
                        "WinApp подтвердил ввод, но видимый переход не удалось подтвердить по двум PNG и доказанной content geometry.",
                };
            }
            else
            {
                report["palettePixelAnalysis"] = new
                {
                    status = "NOT_TESTED",
                    reason = "Физическое взаимодействие выключено; action-menu PNG и соответствующие xterm cells не создавались.",
                };
                report["physicalInteraction"] = new
                {
                    status = "NOT_TESTED",
                    reason = "Физический ввод выключен по умолчанию; передайте --interactive true для F2/Escape.",
                };
            }

            bool captureProvenance = startup.CaptureMethod == "WGC";
            bool geometryProven = content is not null && appRecord.Columns == request.Columns && appRecord.Rows == request.Rows;
            bool colorEvidence = pixelAnalysis.Status == "PASS"
                && (!request.Interactive || palettePixelAnalysis?.Status == "PASS");
            bool comparison = appearance.ComparisonReady && geometryProven;
            bool stateComparable = false;
            bool visualAssessmentComplete = false;
            bool completePhysical = captureProvenance && geometryProven && colorEvidence && comparison
                && stateComparable && visualAssessmentComplete && (!request.Interactive || visualTransition);
            status = completePhysical ? "PASS" : "BLOCKED";
            List<string> blockedGates = [];
            if (!captureProvenance)
            {
                blockedGates.Add("captureProvenance: WinApp screenshot metadata не сообщает mode.");
            }

            if (!geometryProven)
            {
                blockedGates.Add("contentGeometry: точная сетка физического окна не подтверждена.");
            }

            if (!colorEvidence)
            {
                blockedGates.Add("pixelSamples: недостаточно однозначных RGB sample.");
            }

            if (!comparison)
            {
                blockedGates.Add("profileComparison: нет подтверждённой палитры или geometry.");
            }

            if (request.Interactive && !visualTransition)
            {
                blockedGates.Add("physicalTransition: F2/Escape не подтвердили видимый переход.");
            }

            if (!stateComparable)
            {
                blockedGates.Add("stateComparability: отдельные сессии требуют визуальной оценки сопоставимости состояния.");
            }

            if (!visualAssessmentComplete)
            {
                blockedGates.Add("visualAssessment: требуется открыть физические и xterm PNG.");
            }

            message = completePhysical
                ? "Физический TUI зафиксирован; обязательные источники и сравнение подтверждены."
                : "Physical acceptance заблокирован: " + string.Join("; ", blockedGates);
            report["status"] = status;
            report["physicalAcceptance"] = completePhysical ? "PASS" : "BLOCKED";
            report["blockedGates"] = completePhysical ? [] : blockedGates;
            report["acceptanceGates"] = new
            {
                windowIdentity = "PASS",
                appIdentity = "PASS",
                separateConPtyArtifacts = "PASS",
                captureProvenance = captureProvenance ? "PASS" : "NOT_TESTED",
                contentGeometry = geometryProven ? "PASS" : "NOT_TESTED",
                pixelSamples = colorEvidence ? "PASS" : "NOT_TESTED",
                profileComparison = comparison ? "PASS" : "NOT_TESTED",
                stateComparability = stateComparable ? "PASS" : "NOT_TESTED",
                physicalTransition = request.Interactive ? (visualTransition ? "PASS" : "NOT_TESTED") : "NOT_TESTED",
                visualAssessment = visualAssessmentComplete ? "PASS" : "NOT_TESTED",
            };
        }
        catch (Exception exception)
        {
            HarnessResponse failure = SessionOwner.Failure(exception, session);
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
            try
            {
                string? userConfigurationHashAfter = userConfigurationPath is null
                    ? null : HashIfPresent(userConfigurationPath);
                report["userConfiguration"] = new
                {
                    path = userConfigurationPath,
                    sha256Before = userConfigurationHashBefore,
                    sha256After = userConfigurationHashAfter,
                    unchanged = userConfigurationHashBefore == userConfigurationHashAfter,
                    checkedReadOnly = true,
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                report["userConfiguration"] = new
                {
                    status = "NOT_TESTED",
                    path = userConfigurationPath,
                    reason = "Не удалось перечитать user configuration после физического прогона: " + exception.Message,
                };
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
                ?? throw new InvalidDataException("Пустой physical launch request.");
            startedPath = request.AppStartPath;
            string exe = Path.GetFullPath(request.Exe);
            string isolationRoot = Path.GetFullPath(request.IsolationRoot);
            if (!File.Exists(exe) || !Directory.Exists(isolationRoot) || HashFile(exe) != request.ExpectedExeSha256
                || HashIfPresent(Path.ChangeExtension(exe, ".dll")) != request.ExpectedAppDllSha256)
            {
                throw new HarnessBlockedException("EXE/DLL изменились до запуска physical child.");
            }

            ProcessStartInfo start = new(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = request.WorkingDirectory,
            };
            start.ArgumentList.Add("--mode");
            start.ArgumentList.Add("diagnostic");
            start.ArgumentList.Add("--isolated-ui");
            start.ArgumentList.Add(isolationRoot);
            start.Environment["TERM"] = "xterm-256color";
            start.Environment["COLORTERM"] = "truecolor";
            start.Environment["TERM_PROGRAM"] = "Windows Terminal";
            _ = start.Environment.Remove("NO_COLOR");
            _ = start.Environment.Remove("FORCE_COLOR");

            job = ConPty.CreateJob(denyChildren: true);
            app = Process.Start(start) ?? throw new HarnessBlockedException("Не удалось запустить AzurPilot.App.exe в Windows Terminal.");
            ConPty.AssignProcessToJob(job, app.SafeHandle);
            string appPath = app.MainModule?.FileName is string modulePath ? Path.GetFullPath(modulePath) : string.Empty;
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
                ["--mode", "diagnostic", "--isolated-ui", isolationRoot],
                Environment.GetEnvironmentVariable("WT_SESSION"),
                DateTimeOffset.UtcNow,
                columns,
                rows);
            await WriteAtomicJsonAsync(startedPath, record);
            await app.WaitForExitAsync();
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

    private static async Task<object> CaptureConPtyAsync(
        HarnessRequest request, string exe, string directory, string profilePath)
    {
        string output = Path.Combine(directory, "conpty");
        HarnessResponse started = await Program.StartAsync(new HarnessRequest
        {
            Command = "start",
            Exe = exe,
            Output = output,
            Profile = profilePath,
            Mode = "diagnostic",
            Providers = "disabled",
            Columns = request.Columns,
            Rows = request.Rows,
            LifetimeSeconds = 60,
        });
        if (started.Status != "PASS" || started.Session is null)
        {
            throw new HarnessBlockedException("ConPTY comparison session did not start: " + started.Message);
        }

        HarnessResponse capture;
        HarnessResponse actionInput;
        HarnessResponse actionMenu;
        HarnessResponse escapeInput;
        HarnessResponse restored;
        try
        {
            capture = await Program.SendAsync(new HarnessRequest
            {
                Command = "capture",
                Session = started.Session,
                Name = "startup",
                TimeoutMs = 10_000,
            });
            if (capture.Status != "PASS")
            {
                throw new HarnessBlockedException("ConPTY startup capture failed: " + capture.Message);
            }

            actionInput = await Program.SendAsync(new HarnessRequest
            {
                Command = "key",
                Session = started.Session,
                Value = "F2",
                TimeoutMs = 10_000,
            });
            if (actionInput.Status != "PASS")
            {
                throw new HarnessBlockedException("ConPTY F2 action-menu input failed: " + actionInput.Message);
            }

            actionMenu = await Program.SendAsync(new HarnessRequest
            {
                Command = "capture",
                Session = started.Session,
                Name = "actions-menu",
                TimeoutMs = 10_000,
            });
            if (actionMenu.Status != "PASS")
            {
                throw new HarnessBlockedException("ConPTY action-menu capture failed: " + actionMenu.Message);
            }

            escapeInput = await Program.SendAsync(new HarnessRequest
            {
                Command = "key",
                Session = started.Session,
                Value = "Escape",
                TimeoutMs = 10_000,
            });
            if (escapeInput.Status != "PASS")
            {
                throw new HarnessBlockedException("ConPTY Escape input failed: " + escapeInput.Message);
            }

            restored = await Program.SendAsync(new HarnessRequest
            {
                Command = "capture",
                Session = started.Session,
                Name = "restored",
                TimeoutMs = 10_000,
            });
            if (restored.Status != "PASS")
            {
                throw new HarnessBlockedException("ConPTY restored capture failed: " + restored.Message);
            }
        }
        finally
        {
            _ = await Program.SendAsync(new HarnessRequest
            {
                Command = "close",
                Session = started.Session,
                TimeoutMs = 5_000,
            });
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "conpty-capture.json"),
            JsonSerializer.Serialize(capture, HarnessJson.Options));
        return new
        {
            status = "PASS",
            outputDirectory = output,
            session = started.Session,
            configurationPath = Path.Combine(output, started.Session,
                "runtime", Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath())),
            configurationSha256 = HashIfPresent(Path.Combine(output, started.Session,
                "runtime",
                Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath()))),
            providers = "disabled",
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
            evidence = "ConPTY → VT + xterm cells + Chromium PNG; это не физический Windows Terminal evidence.",
        };
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
            || app.Columns <= 0 || app.Rows <= 0 || !appearance.ComparisonReady)
        {
            return new("NOT_TESTED", "Для semantic samples не подтверждены content bounds, grid или сравнительная palette.", [],
                new { status = "NOT_TESTED", reason = "Недостаточно наблюдаемых геометрии/профиля; произвольные координаты не подставляются." });
        }

        using JsonDocument cellsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(cellsPath));
        JsonElement snapshot = cellsDocument.RootElement;
        int columns = snapshot.GetProperty("columns").GetInt32();
        int rows = snapshot.GetProperty("rows").GetInt32();
        if (columns != app.Columns || rows != app.Rows)
        {
            return new("NOT_TESTED", "Размеры сравниваемых ConPTY и physical grid различаются.", [],
                new { status = "NOT_TESTED", xtermColumns = columns, xtermRows = rows, physicalColumns = app.Columns, physicalRows = app.Rows });
        }

        JsonElement theme = snapshot.GetProperty("effectiveTheme");
        JsonElement xtermGeometry = snapshot.GetProperty("geometry");
        double cellWidth = (double)content.Width / columns;
        double cellHeight = (double)content.Height / rows;
        if (cellWidth < 4 || cellHeight < 5 || cellWidth > 100 || cellHeight > 100)
        {
            return new("NOT_TESTED", "Размеры ячейки вне ожидаемого диапазона UIA content bounds.", [],
                new { status = "NOT_TESTED", cellWidth, cellHeight });
        }

        List<PhysicalPixelSample> samples = [];
        JsonElement cellArray = snapshot.GetProperty("cells");
        JsonElement background = cellArray.EnumerateArray()
            .FirstOrDefault(cell => string.IsNullOrWhiteSpace(cell.GetProperty("text").GetString())
                && !cell.GetProperty("dim").GetBoolean() && !cell.GetProperty("inverse").GetBoolean());
        PhysicalPixelSample? backgroundSample = null;
        PhysicalRgb? observedBackground = null;
        if (background.ValueKind == JsonValueKind.Object)
        {
            int x = background.GetProperty("x").GetInt32();
            int y = background.GetProperty("y").GetInt32();
            PhysicalPixelRectangle rectangle = CellRectangle(content, columns, rows, x, y, 1, 1);
            PhysicalRgb expected = FromRgb(ColorSamples.Resolve(background.GetProperty("bg"), theme, "background"));
            backgroundSample = new("background-blank-cell", rectangle, "background", expected);
            PhysicalPixelMeasurement measurement = PhysicalPngAnalyzer.Analyze(image, [backgroundSample])[0];
            samples.Add(backgroundSample);
            if (measurement.Status != "NOT_TESTED")
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

            int expected = ColorSamples.Resolve(cell.GetProperty("fg"), theme, "foreground");
            int backgroundRgb = ColorSamples.Resolve(cell.GetProperty("bg"), theme, "background");
            if (expected == backgroundRgb)
            {
                continue;
            }

            if (observedBackground is null)
            {
                continue;
            }

            string semantic = FindSemanticRole(snapshot, cell, FromRgb(expected), theme);
            string key = $"{semantic}:{expected:X6}:{backgroundRgb:X6}";
            if (!foregroundSamples.ContainsKey(key))
            {
                foregroundSamples.Add(key, cell.Clone());
            }
        }

        foreach ((string key, JsonElement cell) in foregroundSamples.Take(MaxPhysicalSamples - samples.Count))
        {
            string[] colors = key.Split(':');
            string semantic = colors[0];
            PhysicalRgb expected = ParseRgb(colors[1]);
            int x = cell.GetProperty("x").GetInt32();
            int y = cell.GetProperty("y").GetInt32();
            PhysicalPixelRectangle rectangle = CellRectangle(content, columns, rows, x, y, 1, 1);
            string glyph = cell.GetProperty("text").GetString()!;
            samples.Add(new($"{semantic}-{EscapeSampleName(glyph)}-{colors[1]}", rectangle, "foreground", expected, observedBackground));
        }

        if (samples.Count == 0)
        {
            return new("NOT_TESTED", "В production TUI не обнаружены однозначные background/foreground ячейки.", [],
                new { status = "NOT_TESTED", reason = "Ни один sample не выдуман." });
        }

        IReadOnlyList<PhysicalPixelMeasurement> measurements = PhysicalPngAnalyzer.Analyze(image, samples);
        int measuredBackground = measurements.Count(measurement => measurement.Kind == "background"
            && measurement.Status != "NOT_TESTED" && measurement.ActualRgb is not null);
        int measuredForeground = measurements.Count(measurement => measurement.Kind == "foreground"
            && measurement.Status != "NOT_TESTED" && measurement.ActualRgb is not null);
        string result = measuredBackground >= 1 && measuredForeground >= 2 ? "PASS" : "NOT_TESTED";
        string comparison = measurements.Any(measurement => measurement.Status == "FAIL") ? "DIFFERENT"
            : result == "PASS" ? "MATCH" : "NOT_TESTED";
        return new(result, result == "PASS" ? "Измерены фактический фон и минимум две семантические текстовые области; различия цвета отражены отдельно."
            : "Для RGB evidence нужны однозначный фон и минимум две измеримые семантические текстовые области.", measurements,
            new
            {
                status = result,
                comparisonStatus = comparison,
                metric = "Фактический dominant opaque RGB для фона и однородный 3×3 interior foreground; сравнение с ожидаемым точное, maxChannelDelta показывает максимальную разницу канала без tolerance.",
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
                    reason = "ConPTY cells и физический PNG принадлежат раздельным процессам; соответствие видимых состояний требует визуального осмотра.",
                },
                samples = measurements,
                caveats = PixelAnalysisCaveats.Concat(["Измеренный цвет может отличаться от ожидаемой палитры из-за Acrylic, opacity, DWM composition или настроек профиля."]).ToArray(),
            });
    }

    private static string FindSemanticRole(JsonElement snapshot, JsonElement cell, PhysicalRgb expected, JsonElement theme)
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

        if (expected == ThemeColor(theme, "foreground"))
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

        if (expected == ThemeColor(theme, "brightBlack"))
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
        PhysicalAppStartRecord app, WinAppCapture capture, out ContentRectangle? content)
    {
        content = null;
        JsonElement windows = inspect.GetProperty("windows");
        if (windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() != 1
            || Int64Property(windows[0], "hwnd") != window.Hwnd
            || TextProperty(windows[0], "title") != window.Title)
        {
            return new { status = "NOT_TESTED", reason = "UIA inspect не сохранил единственную точную HWND/marker identity." };
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
            return new { status = "NOT_TESTED", reason = "UIA не вернул дерево elements." };
        }

        foreach (JsonElement element in Descendants(elements))
        {
            if (TextProperty(element, "type") != "Custom"
                || element.TryGetProperty("isOffscreen", out JsonElement offscreen) && offscreen.ValueKind == JsonValueKind.True)
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
        int outerWidthDiff = Math.Abs((outerBounds.Right - outerBounds.Left) - window.Width);
        int outerHeightDiff = Math.Abs((outerBounds.Bottom - outerBounds.Top) - window.Height);
        int captureWidthDiff = Math.Abs((visibleBounds.Right - visibleBounds.Left) - capture.SourceWindowWidth);
        int captureHeightDiff = Math.Abs((visibleBounds.Bottom - visibleBounds.Top) - capture.SourceWindowHeight);
        if (outerWidthDiff > 2 || outerHeightDiff > 2 || captureWidthDiff > 2
            || captureHeightDiff > 2 || surfaces.Count == 0)
        {
            return new
            {
                status = "NOT_TESTED",
                reason = surfaces.Count == 0 ? "UIA не сообщил пригодную Custom-поверхность Windows Terminal."
                    : "DPI-нормализованный HWND bounds, DWM visible frame и единственный WGC source geometry расходятся.",
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
                reason = "UIA Custom-поверхность отсутствует, неоднозначна или размеры physical grid не наблюдались.",
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
            return new { status = "NOT_TESTED", reason = "UIA Custom-поверхность находится вне screenshot HWND bounds.", surface };
        }

        content = candidate;
        return new
        {
            status = "PASS",
            source = "exact HWND WinApp target + DPI-normalized GetWindowRect + DWM visible frame + one captured WinApp source + largest unique UIA Custom surface in physical screen pixels; used for coordinates only, not terminal text/cells",
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
            requestedColumns = app.Columns,
            requestedRows = app.Rows,
        };
    }

    private static int ScaleCoordinate(int coordinate, double scale)
    {
        double scaled = Math.Round(coordinate * scale, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(scaled) || scaled is < int.MinValue or > int.MaxValue)
        {
            throw new HarnessBlockedException("DPI-преобразование HWND geometry вышло за допустимый диапазон.");
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

    private static string? TextProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    private static int IntProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : -1;
    }

    private static long Int64Property(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : -1;
    }

    private static int? OptionalInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : null;
    }

    private static double? OptionalDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) ? result : null;
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
                return record ?? throw new HarnessBlockedException("Physical child identity record пуст.");
            }

            string failurePath = Path.Combine(Path.GetDirectoryName(path)!, "app-start-error.json");
            if (File.Exists(failurePath))
            {
                HarnessResponse? failure = JsonSerializer.Deserialize<HarnessResponse>(await File.ReadAllTextAsync(failurePath), HarnessJson.Options);
                throw new HarnessBlockedException(failure?.Message ?? "Physical child не удалось запустить.");
            }

            await Task.Delay(100);
        }

        throw new HarnessBlockedException("Physical app process не сообщил PID до истечения срока запуска.");
    }

    private static Process? StartWindowsTerminal(string wt, string marker, HarnessRequest request,
        TerminalAppearance appearance, string launchRequest)
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
        if (!string.IsNullOrWhiteSpace(appearance.ProfileName))
        {
            start.ArgumentList.Add("--profile");
            start.ArgumentList.Add(appearance.ProfileName);
        }

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
            || record.ExeSha256 != expectedHash || !string.IsNullOrEmpty(record.TerminalSessionId)
                && record.TerminalSessionId.Length > 128)
        {
            throw new HarnessBlockedException("Child identity record не совпал с запрошенным EXE/args.");
        }

        using Process process = Process.GetProcessById(record.ProcessId);
        string? path = process.MainModule?.FileName;
        if (!Path.GetFullPath(path ?? string.Empty).Equals(expectedExe, StringComparison.OrdinalIgnoreCase)
            || process.HasExited)
        {
            throw new HarnessBlockedException("Фактический PID не принадлежит запущенному AzurPilot.App.exe.");
        }
    }

    private static void VerifyWindow(WinAppWindow window, string marker)
    {
        if (window.Hwnd <= 0 || window.ProcessId <= 0 || window.Title != marker
            || !window.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessBlockedException("Выбранное HWND не принадлежит точному owned Windows Terminal окну.");
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
            throw new HarnessBlockedException("Win32 HWND ownership/geometry не совпала с WinApp exact window.");
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
            throw new HarnessBlockedException("DWM не подтвердил физические visible bounds выбранного HWND.");
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            windowCloseStatus = "BLOCKED: " + exception.Message;
        }

        List<string> processCleanup = [];
        if (app is not null)
        {
            if (expectedExe is not null)
            {
                KillOwnedProcess(app.ProcessId, expectedExe, processCleanup, "AzurPilot.App");
            }

            string helperPath = Environment.ProcessPath ?? string.Empty;
            KillOwnedProcess(app.HelperPid, helperPath, processCleanup, "physical-helper");
        }

        bool markerAbsent = FindExistingMarkerWindows(marker).Count == 0;
        bool windowCloseConfirmed = window is null
            ? markerAbsent && (windowCloseStatus is "NOT_TESTED" or "PASS")
            : windowCloseStatus == "PASS" && !Native.IsWindow((nint)window.Hwnd);
        bool processesConfirmed = app is null
            ? processCleanup.Count == 0
            : processCleanup.Count == 2 && processCleanup.All(ProcessCleanupConfirmed);
        return new
        {
            status = windowCloseConfirmed && processesConfirmed ? "PASS" : "NOT_TESTED",
            ownedWindowCloseRequested = windowCloseRequested,
            ownedWindowCloseStatus = windowCloseStatus,
            ownedWindowClosed = windowCloseConfirmed,
            exactWindowTitleRequired = marker,
            processes = processCleanup,
            note = "WindowsTerminal.exe не завершается целиком; cleanup адресует только HWND с точным marker и собственные child/helper PID.",
        };
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

    private static void KillOwnedProcess(int processId, string expectedPath, List<string> results, string label)
    {
        if (processId <= 0 || string.IsNullOrWhiteSpace(expectedPath))
        {
            results.Add(label + ":NOT_TESTED");
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            string actualPath = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
            if (!actualPath.Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                results.Add(label + ":BLOCKED:pid-path-mismatch");
                return;
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                if (!process.WaitForExit(WindowCleanupMs))
                {
                    results.Add(label + ":BLOCKED:cleanup-timeout");
                    return;
                }
            }

            results.Add(label + ":PASS");
        }
        catch (ArgumentException)
        {
            results.Add(label + ":PASS:already-exited");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            results.Add(label + ":BLOCKED:" + exception.Message);
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

        object profile = new
        {
            name = $"Physical comparison: {appearance.SchemeName}",
            fontFamily = appearance.FontFamily ?? "Consolas",
            fontSize = appearance.FontSize ?? 16,
            fontWeight = "normal",
            fontWeightBold = "bold",
            lineHeight = 1,
            letterSpacing = 0,
            cursorStyle = "bar",
            theme = appearance.Theme,
        };
        string path = Path.Combine(directory, "comparison-profile.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile, HarnessJson.Options));
        return path;
    }

    private static TerminalAppearance ReadTerminalAppearance(string requestedScheme)
    {
        string? settingsPath = FindTerminalSettings();
        if (settingsPath is null)
        {
            return TerminalAppearance.Unknown(requestedScheme, "Не найден активный settings.json Windows Terminal; схема/шрифт не подтверждены.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settingsPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32,
            });
            JsonElement root = document.RootElement;
            JsonElement defaults = root.TryGetProperty("profiles", out JsonElement profilesElement)
                && profilesElement.ValueKind == JsonValueKind.Object
                && profilesElement.TryGetProperty("defaults", out JsonElement defaultProfile)
                ? defaultProfile : default;
            JsonElement profiles = profilesElement.ValueKind == JsonValueKind.Object
                && profilesElement.TryGetProperty("list", out JsonElement list) ? list : default;
            string? defaultProfileId = TextProperty(root, "defaultProfile");
            JsonElement selected = default;
            if (profiles.ValueKind == JsonValueKind.Array && defaultProfileId is not null)
            {
                selected = profiles.EnumerateArray().FirstOrDefault(profile =>
                    string.Equals(TextProperty(profile, "guid"), defaultProfileId, StringComparison.OrdinalIgnoreCase));
            }

            string? profileName = TextProperty(selected, "name");
            string? profileScheme = TextProperty(selected, "colorScheme") ?? TextProperty(defaults, "colorScheme");
            string schemeName = requestedScheme == "current" ? profileScheme ?? "UNKNOWN" : requestedScheme;
            JsonElement scheme = default;
            if (root.TryGetProperty("schemes", out JsonElement schemes) && schemes.ValueKind == JsonValueKind.Array)
            {
                scheme = schemes.EnumerateArray().FirstOrDefault(item =>
                    string.Equals(TextProperty(item, "name"), schemeName, StringComparison.Ordinal));
            }

            Dictionary<string, string>? theme = ReadPalette(scheme);
            string? paletteSource = theme is null ? null : "Windows Terminal settings.json scheme";
            string? paletteSourcePath = theme is null ? null : settingsPath;
            string? paletteSourceHash = theme is null ? null : HashFile(settingsPath);
            if (theme is null && requestedScheme is "Campbell" or "Campbell Light")
            {
                (theme, paletteSourcePath, paletteSourceHash) = ReadRepositoryPalette(requestedScheme);
                if (theme is not null)
                {
                    paletteSource = "TUI Harness built-in comparison profile; requested Windows Terminal built-in scheme";
                }
            }

            string? fontFamily = ReadNestedText(selected, defaults, "font", "face") ?? "Consolas";
            int? fontSize = ReadNestedInt(selected, defaults, "font", "size") ?? 16;
            bool acrylic = ReadNestedBoolean(selected, defaults, "useAcrylic") ?? false;
            double opacity = ReadNestedDouble(selected, defaults, "opacity") ?? 1;
            bool backgroundImage = ReadNestedText(selected, defaults, "backgroundImage", null) is not null;
            bool opaque = theme is not null && !acrylic && opacity >= 1 && !backgroundImage;
            string? limitation = theme is null
                ? $"Цветовая схема '{schemeName}' не найдена или не содержит полной 16-color palette."
                : opaque ? null : "Фон/композиция может включать прозрачность, Acrylic/Mica или background image; фактический RGB будет измеряться после композиции.";
            return new TerminalAppearance(
                requestedScheme,
                schemeName == "UNKNOWN" ? null : schemeName,
                profileName,
                fontFamily,
                fontSize,
                theme,
                settingsPath,
                HashFile(settingsPath),
                paletteSource,
                paletteSourcePath,
                paletteSourceHash,
                opaque,
                theme is not null && fontFamily is not null && fontSize is not null,
                limitation,
                new
                {
                    status = theme is null ? "NOT_TESTED" : "PASS",
                    settingsPath,
                    settingsSha256 = HashFile(settingsPath),
                    profileName,
                    defaultProfileId,
                    schemeName,
                    fontFamily,
                    fontSize,
                    useAcrylic = acrylic,
                    opacity,
                    backgroundImage,
                    opaqueBackgroundConfirmed = opaque,
                    palette = theme,
                    paletteSource,
                    paletteSourcePath,
                    paletteSourceSha256 = paletteSourceHash,
                    limitation,
                    settingsWereReadOnly = true,
                });
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return TerminalAppearance.Unknown(requestedScheme, "settings.json невозможно прочитать: " + exception.Message, settingsPath);
        }
    }

    private static (Dictionary<string, string>? Theme, string? Path, string? Hash) ReadRepositoryPalette(string schemeName)
    {
        string fileName = schemeName == "Campbell" ? "campbell.json" : "campbell-light.json";
        string path = Path.Combine(AppContext.BaseDirectory, "profiles", fileName);
        if (!File.Exists(path))
        {
            return (null, null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            Dictionary<string, string>? theme = root.TryGetProperty("theme", out JsonElement profileTheme)
                ? ReadPalette(profileTheme) : null;
            return (theme, path, HashFile(path));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, path, null);
        }
    }

    private static Dictionary<string, string>? ReadPalette(JsonElement scheme)
    {
        if (scheme.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string[] keys = ["foreground", "background", "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite"];
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            string source = key switch { "magenta" => "purple", "brightMagenta" => "brightPurple", _ => key };
            string? value = TextProperty(scheme, source) ?? (source == key ? null : TextProperty(scheme, key));
            if (!IsOpaqueRgb(value))
            {
                return null;
            }

            result.Add(key, value!);
        }

        result["cursor"] = TextProperty(scheme, "cursorColor") ?? result["foreground"];
        result["cursorAccent"] = result["background"];
        result["selectionBackground"] = TextProperty(scheme, "selectionBackground") ?? "#ffffff";
        return result;
    }

    private static bool IsOpaqueRgb(string? value)
    {
        return value is { Length: 7 } && value[0] == '#'
            && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    }

    private static string? FindTerminalSettings()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(local, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(local, "Packages", "Microsoft.WindowsTerminalDev_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ReadNestedText(JsonElement profile, JsonElement defaults, string parent, string? child)
    {
        string? Read(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement source = element;
            if (child is not null)
            {
                if (!element.TryGetProperty(parent, out source))
                {
                    return null;
                }

                return TextProperty(source, child);
            }

            return TextProperty(source, parent);
        }

        return Read(profile) ?? Read(defaults);
    }

    private static int? ReadNestedInt(JsonElement profile, JsonElement defaults, string parent, string child)
    {
        return ReadNested<int>(profile, defaults, parent, child, element => element.TryGetInt32(out int value) ? value : null);
    }

    private static double? ReadNestedDouble(JsonElement profile, JsonElement defaults, string property)
    {
        return ReadNested<double>(profile, defaults, property, null, element => element.TryGetDouble(out double value) ? value : null);
    }

    private static bool? ReadNestedBoolean(JsonElement profile, JsonElement defaults, string property)
    {
        return ReadNested<bool>(profile, defaults, property, null, element => element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean() : null);
    }

    private static T? ReadNested<T>(JsonElement profile, JsonElement defaults, string parent, string? child,
        Func<JsonElement, T?> parse) where T : struct
    {
        JsonElement Read(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            if (child is not null)
            {
                return element.TryGetProperty(parent, out JsonElement nested) && nested.TryGetProperty(child, out JsonElement value)
                    ? value : default;
            }

            return element.TryGetProperty(parent, out JsonElement direct) ? direct : default;
        }

        JsonElement profileValue = Read(profile);
        if (profileValue.ValueKind != JsonValueKind.Undefined)
        {
            return parse(profileValue);
        }

        JsonElement defaultValue = Read(defaults);
        return defaultValue.ValueKind == JsonValueKind.Undefined ? null : parse(defaultValue);
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

    private static PhysicalRgb ThemeColor(JsonElement theme, string name)
    {
        string value = theme.GetProperty(name).GetString()!;
        return ParseRgb(value[1..]);
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
            throw new HarnessBlockedException("Windows не предоставила текущий input desktop.");
        }

        const int Capacity = 256;
        nint buffer = Marshal.AllocHGlobal(Capacity * sizeof(char));
        try
        {
            if (!Native.GetUserObjectInformation(desktop, UserObjectName, buffer,
                Capacity * sizeof(char), out _))
            {
                throw new HarnessBlockedException("Не удалось определить имя текущего input desktop.");
            }

            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    private sealed record PhysicalAppLaunchRequest(string Exe, string IsolationRoot, string WorkingDirectory,
        string AppStartPath, string ExpectedExeSha256, string? ExpectedAppDllSha256);

    private sealed record PhysicalAppStartRecord(int ProcessId, int HelperPid, string Exe, string ExeSha256,
        string? AppDllSha256, string[] Arguments, string? TerminalSessionId, DateTimeOffset StartedUtc,
        int? Columns, int? Rows);

    private sealed record ContentRectangle(int X, int Y, int Width, int Height);
    private sealed record UiRectangle(int X, int Y, int Width, int Height, string Name);
    private sealed record Win32Window(long Hwnd, int ProcessId);
    private sealed record PhysicalPngAnalysis(string Status, string Reason,
        IReadOnlyList<PhysicalPixelMeasurement> Measurements, object Evidence);

    private sealed record TerminalAppearance(string RequestedScheme, string? SchemeName, string? ProfileName,
        string? FontFamily, int? FontSize, Dictionary<string, string>? Theme, string? SettingsPath,
        string? SettingsHash, string? PaletteSource, string? PaletteSourcePath, string? PaletteSourceHash,
        bool OpaqueBackgroundConfirmed, bool ComparisonReady, string? Limitation, object Evidence)
    {
        internal static TerminalAppearance Unknown(string requestedScheme, string reason, string? settingsPath = null)
        {
            return new(requestedScheme, null, null, null, null, null, settingsPath, null, null, null, null, false, false, reason,
                new { status = "NOT_TESTED", requestedScheme, settingsPath, reason, settingsWereReadOnly = true });
        }
    }

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
