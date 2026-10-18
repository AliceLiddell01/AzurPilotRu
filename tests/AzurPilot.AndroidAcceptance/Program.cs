using System.Text;
using AzurPilot.AndroidAcceptance;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;
using Microsoft.Extensions.Logging;
using AcceptanceSanitizer = AzurPilot.MuMuAcceptance.AcceptanceSanitizer;

// Инструмент реальной приёмки Android-слоя на Windows.
//
// Существующий `--instance` режим проверяет lifecycle игры и восстанавливает исходное состояние.
// `--screenshot-live` отдельно проверяет read-only exact-target screenshot на уже готовом foreground
// target-е; `--prepare-transport` — отдельная явная подготовка transport; `--screenshot-samples`
// декодирует локальный набор PNG без доступа к устройству. Все режимы остаются тестовым инструментом:
// продуктовый CLI в AzurPilot.App не добавляется.

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

ScreenshotLiveOptions? screenshotLiveOptions = null;
AndroidTransportPreparationOptions? transportPreparationOptions = null;
if (AndroidTransportPreparationArgumentParser.IsMode(args))
{
    AndroidTransportPreparationArguments preparationArguments =
        AndroidTransportPreparationArgumentParser.Parse(args);
    if (preparationArguments.Options is null)
    {
        Console.Error.WriteLine("Приёмка Android AzurPilot: " + preparationArguments.Error);
        Console.Error.WriteLine(AndroidTransportPreparationArgumentParser.Usage);
        return AndroidAcceptanceExitCode.UsageRejected;
    }

    transportPreparationOptions = preparationArguments.Options;
}

if (ScreenshotAcceptanceArgumentParser.IsScreenshotMode(args))
{
    ScreenshotAcceptanceArguments screenshotArguments = ScreenshotAcceptanceArgumentParser.Parse(args);
    if (screenshotArguments.Options is null)
    {
        Console.Error.WriteLine("Приёмка screenshot AzurPilot: " + screenshotArguments.Error);
        Console.Error.WriteLine(ScreenshotAcceptanceArgumentParser.Usage);
        return AndroidAcceptanceExitCode.UsageRejected;
    }

    if (screenshotArguments.Options is ScreenshotSamplesOptions samplesOptions)
    {
        ScreenshotSampleRunReport sampleReport;
        try
        {
            sampleReport = ScreenshotSampleRunner.Run(samplesOptions.DirectoryPath);
        }
        catch (Exception exception)
        {
            object failureEvidence = new
            {
                mode = "local_samples",
                isProven = false,
                failureCode = "screenshot_sample_runner_failed",
                exceptionType = ScreenshotAcceptanceRunner.GetSafeExceptionType(exception),
            };
            string failureJson = ScreenshotAcceptanceJson.Serialize(failureEvidence);
            bool artifactWritten = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
                samplesOptions.ArtifactName, failureJson, out string artifactReason);
            ScreenshotAcceptanceOutput<object> failureOutput = new(
                "local_samples",
                false,
                "screenshot_sample_runner_failed",
                samplesOptions.ArtifactName is not null,
                samplesOptions.ArtifactName is not null && artifactWritten,
                artifactWritten ? null : artifactReason,
                failureEvidence);
            Console.Out.WriteLine(ScreenshotAcceptanceJson.Serialize(failureOutput));
            if (!artifactWritten)
            {
                Console.Error.WriteLine("Приёмка screenshot AzurPilot: не удалось сохранить отчёт (" + artifactReason + ").");
            }

            return failureOutput.ExitCode;
        }

        string sampleEvidenceJson = ScreenshotAcceptanceJson.Serialize(sampleReport);
        bool sampleArtifactWritten = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
            samplesOptions.ArtifactName, sampleEvidenceJson, out string sampleArtifactReason);
        ScreenshotAcceptanceOutput<ScreenshotSampleRunReport> sampleOutput = new(
            "local_samples",
            sampleReport.IsProven,
            sampleReport.IsProven ? null : "screenshot_sample_scan_not_proven",
            samplesOptions.ArtifactName is not null,
            samplesOptions.ArtifactName is not null && sampleArtifactWritten,
            sampleArtifactWritten ? null : sampleArtifactReason,
            sampleReport);
        Console.Out.WriteLine(ScreenshotAcceptanceJson.Serialize(sampleOutput));
        if (!sampleArtifactWritten)
        {
            Console.Error.WriteLine(
                "Приёмка screenshot AzurPilot: не удалось сохранить отчёт (" + sampleArtifactReason + ").");
        }

        return sampleOutput.ExitCode;
    }

    screenshotLiveOptions = screenshotArguments.Options as ScreenshotLiveOptions;
}

AcceptanceOptions? options = null;
if (screenshotLiveOptions is null && transportPreparationOptions is null)
{
    AcceptanceArguments arguments = AndroidAcceptanceArgumentParser.Parse(args);
    if (arguments.Options is not AcceptanceOptions acceptedOptions)
    {
        Console.Error.WriteLine("Приёмка Android AzurPilot: " + arguments.Error);
        Console.Error.WriteLine(AndroidAcceptanceArgumentParser.Usage);
        return AndroidAcceptanceExitCode.UsageRejected;
    }

    options = acceptedOptions;
}

// Прерывание не завершает процесс мгновенно: восстановление начального состояния выполняется в finally,
// поэтому отмена останавливает только матрицу переходов.
using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

AndroidAcceptanceReport report = new();

// Отчёт не должен раскрывать конкретную машину: профиль пользователя и рабочий каталог защищаются на
// случай, если попадут в текст из внешних источников. Пути установки и bundled ADB защищаются тогда,
// когда станут известны: до обнаружения установки они неизвестны.
report.Sanitizer.Protect(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    AcceptanceSanitizer.UserProfilePlaceholder);
report.Sanitizer.Protect(Environment.CurrentDirectory, AcceptanceSanitizer.WorkingDirectoryPlaceholder);

// Structured log уходит в stderr, человекочитаемый отчёт — в stdout: поверхности не смешиваются.
using StderrLoggerFactory loggerFactory = new();

// Аудит общий для обеих возможностей, а проекция отказа — своя у каждой: смысл отказа запуска процесса
// принадлежит владельцу возможности, поэтому MuMu-команды и ADB-команды идут через один и тот же
// WindowsProcessRunner со своей проекцией. Второй реализации запуска процесса при этом не заводится.
AndroidCommandAuditMode auditMode = transportPreparationOptions is not null
    ? AndroidCommandAuditMode.TransportPreparation
    : screenshotLiveOptions is not null
        ? AndroidCommandAuditMode.ScreenshotReadOnly
        : AndroidCommandAuditMode.LifecycleAcceptance;
AndroidCommandAudit audit = new(auditMode);
IWindowsProcessRunner muMuProcessRunner =
    new AuditingProcessRunner(new WindowsProcessRunner(new MuMuProcessFailureProjection()), audit);
IWindowsProcessRunner adbProcessRunner =
    new AuditingProcessRunner(new WindowsProcessRunner(new AdbProcessFailureProjection()), audit);

WindowsMuMuFileSystemProbe fileSystemProbe = new();

MuMuWindowsHost muMuHost = new(
    new WindowsMuMuInstallationRegistrySource(),
    new WindowsMuMuInstallMetadataSource(fileSystemProbe),
    fileSystemProbe,
    muMuProcessRunner,
    loggerFactory.CreateLogger<MuMuWindowsHost>());

MuMuLifecycleService muMuLifecycle = new(
    muMuHost,
    new MuMuInstanceMutationGate(),
    TimeProvider.System,
    MuMuLifecycleTimings.Default,
    loggerFactory.CreateLogger<MuMuLifecycleService>());

AndroidWindowsHost androidHost = new(adbProcessRunner, TimeProvider.System);

AndroidLifecycleTimings timings = AndroidLifecycleTimings.Default;

AndroidReadinessService readiness = new(
    androidHost,
    timings,
    TimeProvider.System,
    loggerFactory.CreateLogger<AndroidReadinessService>());

AzurLaneGameStateService stateService = new(
    androidHost,
    TimeProvider.System,
    loggerFactory.CreateLogger<AzurLaneGameStateService>());

AzurLaneGameLifecycleService lifecycle = new(
    androidHost,
    stateService,
    new AndroidGameMutationGate(),
    TimeProvider.System,
    timings,
    loggerFactory.CreateLogger<AzurLaneGameLifecycleService>());

if (screenshotLiveOptions is not null)
{
    ScreenshotAcceptanceRunner screenshotRunner = new(
        muMuHost,
        muMuLifecycle,
        androidHost,
        androidHost,
        readiness,
        stateService,
        audit);

    ScreenshotLiveAcceptanceReport screenshotReport;
    try
    {
        screenshotReport = await screenshotRunner.RunAsync(
            screenshotLiveOptions, cancellation.Token).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
        bool cancelled = exception is OperationCanceledException;
        string failureCode = cancelled
            ? "screenshot_capture_cancelled"
            : "screenshot_acceptance_unexpected_failure";
        screenshotReport = new ScreenshotLiveAcceptanceReport(
            AcceptanceBuildIdentity.For(typeof(ScreenshotAcceptanceRunner)).Describe(),
            screenshotLiveOptions.InstanceValue,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            screenshotLiveOptions.CaptureCount,
            0,
            [],
            null,
            null,
            "audit_preflight_incomplete",
            cancelled ? "недоступно: отмена" : "недоступно: непредвиденный сбой",
            cancelled ? "недоступно: отмена" : "недоступно: непредвиденный сбой",
            failureCode,
            null,
            false,
            failureCode,
            null,
            cancelled ? null : "exception_type:" + ScreenshotAcceptanceRunner.GetSafeExceptionType(exception));
    }

    string liveEvidenceJson = ScreenshotAcceptanceJson.Serialize(screenshotReport);
    bool liveArtifactWritten = ScreenshotAcceptanceArtifactWriter.TryWriteReport(
        screenshotLiveOptions.ArtifactName, liveEvidenceJson, out string liveArtifactReason);
    ScreenshotAcceptanceOutput<ScreenshotLiveAcceptanceReport> liveOutput = new(
        "live_capture",
        screenshotReport.IsProven,
        screenshotReport.FailureCode,
        screenshotLiveOptions.ArtifactName is not null,
        screenshotLiveOptions.ArtifactName is not null && liveArtifactWritten,
        liveArtifactWritten ? null : liveArtifactReason,
        screenshotReport);
    Console.Out.WriteLine(ScreenshotAcceptanceJson.Serialize(liveOutput));
    if (!liveArtifactWritten)
    {
        Console.Error.WriteLine(
            "Приёмка screenshot AzurPilot: не удалось сохранить отчёт (" + liveArtifactReason + ").");
    }

    return liveOutput.ExitCode;
}

if (transportPreparationOptions is not null)
{
    AndroidTransportPreparationRunner preparationRunner = new(
        muMuHost,
        muMuLifecycle,
        androidHost,
        readiness,
        audit);

    AndroidTransportPreparationReport preparationReport;
    try
    {
        preparationReport = await preparationRunner.RunAsync(
            transportPreparationOptions, cancellation.Token).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
        preparationReport = new AndroidTransportPreparationReport(
            AcceptanceBuildIdentity.For(typeof(AndroidTransportPreparationRunner)).Describe(),
            transportPreparationOptions.InstanceValue,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            "непредвиденный сбой: " + exception.GetType().Name,
            "недоступно: preflight не завершён",
            "недоступно: preflight не завершён",
            "BLOCKED",
            false,
            "android_transport_preparation_unexpected_failure",
            "preparation_runner_failed");
    }

    Console.Out.WriteLine(ScreenshotAcceptanceJson.Serialize(preparationReport));
    return preparationReport.IsProven
        ? AndroidAcceptanceExitCode.Success
        : AndroidAcceptanceExitCode.NotProven;
}

AndroidAcceptanceRunner runner = new(
    muMuHost, muMuLifecycle, androidHost, readiness, stateService, lifecycle, audit, report);

AcceptanceResult result;
try
{
    result = await runner.RunAsync(options!, cancellation.Token).ConfigureAwait(false);
}
catch (Exception exception)
{
    // Непредвиденный сбой инструмента не должен выглядеть как доказанная приёмка: отчёт получает
    // локальный отказ, а полная диагностика уходит в stderr в санитизированном виде.
    string diagnostics = report.Sanitizer.Sanitize(exception.ToString());
    Console.Error.WriteLine("Приёмка Android AzurPilot: непредвиденный сбой — " + diagnostics);
    result = AcceptanceResult.NotProven(
        "acceptance_unexpected_failure",
        "acceptance_unexpected_failure: " + exception.GetType().Name + ": "
        + report.Sanitizer.Sanitize(exception.Message));
}

AcceptanceReportText rendered = report.Render(options!, result);
Console.Out.WriteLine(rendered.Text);

return result.IsProven && rendered.IsSanitized
    ? AndroidAcceptanceExitCode.Success
    : AndroidAcceptanceExitCode.NotProven;
