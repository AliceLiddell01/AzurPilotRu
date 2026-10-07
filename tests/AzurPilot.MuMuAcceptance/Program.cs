using System.Text;
using AzurPilot.Core.MuMu;
using AzurPilot.MuMuAcceptance;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;
using Microsoft.Extensions.Logging;

// Инструмент реальной приёмки MuMu на Windows.
//
// Это тестовый инструмент, а не продукт: аргументы разбирает только он, в AzurPilot.App ничего не
// добавляется. Прогон выполняет production-код (IMuMuHost из AzurPilot.Windows и MuMuLifecycleService из
// AzurPilot.Core) и доказывает переходы независимым наблюдением состояния.
//
// Коды выхода: 0 — матрица и восстановление начального состояния доказаны; 1 — приёмка не доказана;
// 2 — прогон отклонён до начала работы (не задан exact instance или неизвестный аргумент).
//
// Эксплуатационное замечание для вызывающей стороны: команда запуска экземпляра порождает процессы
// эмулятора как потомков инструмента и наследует его дескрипторы вывода. Оболочка, которая ждёт всё
// дерево процессов вместе с перенаправлением потоков (например, PowerShell
// Start-Process -Wait с -RedirectStandardOutput), не завершится, пока работает эмулятор: вывод нужно
// собирать способом, который ждёт только сам инструмент (например, через cmd с перенаправлением в файл).

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

AcceptanceArguments arguments = AcceptanceArgumentParser.Parse(args);
if (arguments.Options is not AcceptanceOptions options)
{
    Console.Error.WriteLine("AzurPilot MuMu acceptance: " + arguments.Error);
    Console.Error.WriteLine(AcceptanceArgumentParser.Usage);
    return AcceptanceExitCode.UsageRejected;
}

// Прерывание не завершает процесс мгновенно: восстановление начального состояния выполняется в
// finally, поэтому отмена останавливает только матрицу.
using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

AcceptanceReport report = new();

// Отчёт не должен раскрывать конкретную машину: профиль пользователя и рабочий каталог защищаются на
// случай, если попадут в текст из внешних источников.
report.Sanitizer.Protect(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    AcceptanceSanitizer.UserProfilePlaceholder);
report.Sanitizer.Protect(Environment.CurrentDirectory, AcceptanceSanitizer.WorkingDirectoryPlaceholder);

// Structured log уходит в stderr, человекочитаемый отчёт — в stdout: поверхности не смешиваются.
using StderrLoggerFactory loggerFactory = new();

MuMuCommandAudit audit = new();
IWindowsProcessRunner processRunner =
    new AuditingProcessRunner(new WindowsProcessRunner(new MuMuProcessFailureProjection()), audit);
WindowsMuMuFileSystemProbe fileSystemProbe = new();

MuMuWindowsHost host = new(
    new WindowsMuMuInstallationRegistrySource(),
    new WindowsMuMuInstallMetadataSource(fileSystemProbe),
    fileSystemProbe,
    processRunner,
    loggerFactory.CreateLogger<MuMuWindowsHost>());

MuMuLifecycleService lifecycle = new(
    host,
    new MuMuInstanceMutationGate(),
    TimeProvider.System,
    MuMuLifecycleTimings.Default,
    loggerFactory.CreateLogger<MuMuLifecycleService>());

MuMuAcceptanceRunner runner = new(host, lifecycle, processRunner, fileSystemProbe, audit, report);

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
    Console.Error.WriteLine("AzurPilot MuMu acceptance: непредвиденный сбой — " + diagnostics);
    result = AcceptanceResult.NotProven(
        "acceptance_unexpected_failure",
        "acceptance_unexpected_failure: " + exception.GetType().Name + ": "
        + report.Sanitizer.Sanitize(exception.Message));
}

AcceptanceReportText rendered = report.Render(options, result);
Console.Out.WriteLine(rendered.Text);

return result.IsProven && rendered.IsSanitized
    ? AcceptanceExitCode.Success
    : AcceptanceExitCode.NotProven;
