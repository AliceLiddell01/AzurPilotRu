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
// Это тестовый инструмент, а не продукт: аргументы разбирает только он, в AzurPilot.App ничего не
// добавляется. Прогон выполняет production-код (IMuMuHost и MuMuLifecycleService из AzurPilot.Core и
// AzurPilot.Windows для предусловия, AndroidReadinessService, AzurLaneGameStateService и
// AzurLaneGameLifecycleService для readiness и lifecycle игры) и доказывает переходы независимым
// наблюдением состояния.
//
// Коды выхода: 0 — цепочка и восстановление начального состояния игры доказаны; 1 — приёмка не доказана;
// 2 — прогон отклонён до начала работы (не задан exact instance или неизвестный аргумент).
//
// Эксплуатационное замечание для вызывающей стороны: прогон запускает игру внутри уже работающего
// экземпляра эмулятора, поэтому эмулятор наследует дескрипторы вывода инструмента. Оболочка, которая ждёт
// всё дерево процессов вместе с перенаправлением потоков, не завершится, пока работает эмулятор: вывод
// нужно собирать способом, который ждёт только сам инструмент (например, через cmd с перенаправлением в
// файл).

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

AcceptanceArguments arguments = AndroidAcceptanceArgumentParser.Parse(args);
if (arguments.Options is not AcceptanceOptions options)
{
    Console.Error.WriteLine("AzurPilot Android acceptance: " + arguments.Error);
    Console.Error.WriteLine(AndroidAcceptanceArgumentParser.Usage);
    return AndroidAcceptanceExitCode.UsageRejected;
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
AndroidCommandAudit audit = new();
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

AndroidWindowsHost androidHost = new(adbProcessRunner);

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

AndroidAcceptanceRunner runner = new(
    muMuHost, muMuLifecycle, androidHost, readiness, stateService, lifecycle, audit, report);

AcceptanceResult result;
try
{
    result = await runner.RunAsync(options, cancellation.Token).ConfigureAwait(false);
}
catch (Exception exception)
{
    // Непредвиденный сбой инструмента не должен выглядеть как доказанная приёмка: отчёт получает
    // локальный отказ, а полная диагностика уходит в stderr в санитизированном виде.
    string diagnostics = report.Sanitizer.Sanitize(exception.ToString());
    Console.Error.WriteLine("AzurPilot Android acceptance: непредвиденный сбой — " + diagnostics);
    result = AcceptanceResult.NotProven(
        "acceptance_unexpected_failure",
        "acceptance_unexpected_failure: " + exception.GetType().Name + ": "
        + report.Sanitizer.Sanitize(exception.Message));
}

AcceptanceReportText rendered = report.Render(options, result);
Console.Out.WriteLine(rendered.Text);

return result.IsProven && rendered.IsSanitized
    ? AndroidAcceptanceExitCode.Success
    : AndroidAcceptanceExitCode.NotProven;
