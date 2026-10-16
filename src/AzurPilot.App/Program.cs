using System.Text;
using AzurPilot.App;
using AzurPilot.App.LogViewer;
using AzurPilot.App.OperatorUi;
using AzurPilot.Core.Configuration;
using AzurPilot.App.Agent;

// Точка входа приложения: разбирает минимальный CLI, запускает хост приложения и печатает
// человекочитаемый итог. Вся runtime-логика (composition, диагностика, логирование) живёт в
// AzurPilotHost, поэтому здесь нет ни DI-регистраций, ни сбора диагностики, ни настройки логгеров.
//
// Без аргументов поведение прежнее: интерактивный TUI при подключённом терминале либо одноразовая
// диагностика при перенаправленных потоках. Явный --mode имеет приоритет над ui.mode конфигурации.

// Явный namespace выбирается до терминала, legacy parser и ранней инициализации вывода.
// Неверное положение agent тоже остаётся машинным отказом и не открывает TUI.
if (args.Contains("agent", StringComparer.Ordinal))
{
    return await AgentRunner.RunProcessAsync(args).ConfigureAwait(false);
}

Console.OutputEncoding = Encoding.UTF8;

ApplicationArguments arguments = ApplicationArguments.Parse(args);
if (arguments.Failure is ApplicationArgumentFailure argumentFailure)
{
    foreach (string line in argumentFailure.ReportLines())
    {
        Console.Error.WriteLine(line);
    }

    return ApplicationArguments.ExitCodeFor();
}

if (arguments.Help)
{
    Console.WriteLine("Использование: AzurPilot.App [--mode " + string.Join("|", OperatorUiModeNames.All) + "]");
    Console.WriteLine("Без аргументов: ui.mode из config.yaml; по умолчанию normal.");
    Console.WriteLine("При перенаправлении stdin или stdout: одноразовая read-only диагностика.");
    Console.WriteLine("Машинный JSON-интерфейс: AzurPilot.App agent discover.");
    return AzurPilotExitCode.Success;
}

if (arguments.LogViewer)
{
    // Отдельная лог-консоль — собственный процесс того же приложения: он не собирает хост приложения,
    // не открывает TUI и не управляет состоянием MuMu, Android или игры.
    return await AzurPilotLogViewerHost.RunAsync(args).ConfigureAwait(false);
}

if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
{
    AzurPilotInteractiveRunResult interactive = await AzurPilotHost.RunInteractiveAsync(
        static (service, initialDiagnostics, logBuffer, mode, logViewerFailure, cancellationToken) =>
        {
            using AzurPilotOperatorUi ui = new(
                service,
                initialDiagnostics,
                new OperatorUiLogSourceAdapter(logBuffer.Snapshot),
                mode,
                logViewerFailure);
            ui.Run(cancellationToken);
            return Task.CompletedTask;
        },
        modeOverride: arguments.Mode).ConfigureAwait(false);

    if (!interactive.InteractiveSessionStarted || interactive.Startup.ExitCode != AzurPilotExitCode.Success)
    {
        foreach (string line in interactive.Startup.ReportLines)
        {
            Console.WriteLine(line);
        }
    }

    return interactive.Startup.ExitCode;
}

AzurPilotStartupResult result = await AzurPilotHost.RunAsync().ConfigureAwait(false);

foreach (string line in result.ReportLines)
{
    Console.WriteLine(line);
}

return result.ExitCode;
