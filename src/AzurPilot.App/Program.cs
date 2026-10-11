using System.Text;
using AzurPilot.App;
using AzurPilot.App.OperatorUi;

// Точка входа приложения: запускает application host и печатает человекочитаемый итог. Вся
// runtime-логика (composition, диагностика, логирование) живёт в AzurPilotHost, поэтому здесь нет ни
// DI-регистраций, ни сбора диагностики, ни настройки логгеров.
//
// Аргументы запуска не разбираются: продуктового CLI в этой задаче нет, и пользовательской опции,
// подменяющей путь конфигурации, тоже. Runtime-путь по умолчанию остаётся за единственным владельцем
// AzurPilotConfigurationPath, а явный путь — код-параметр composition, которым пользуются тесты.

Console.OutputEncoding = Encoding.UTF8;

if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
{
    AzurPilotInteractiveRunResult interactive = await AzurPilotHost.RunInteractiveAsync(
        static (service, initialDiagnostics, logBuffer, cancellationToken) =>
        {
            using AzurPilotOperatorUi ui = new(
                service,
                initialDiagnostics,
                new OperatorUiLogSourceAdapter(logBuffer.Snapshot));
            ui.Run(cancellationToken);
            return Task.CompletedTask;
        }).ConfigureAwait(false);

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
