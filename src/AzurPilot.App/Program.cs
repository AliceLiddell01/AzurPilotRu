using System.Text;
using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.App.LogViewer;
using AzurPilot.App.OperatorUi;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;

// Точка входа приложения: разбирает минимальный CLI, запускает хост приложения и печатает
// человекочитаемый итог. Вся runtime-логика (composition, диагностика, логирование) живёт в
// AzurPilotHost, поэтому здесь нет ни DI-регистраций, ни сбора диагностики, ни настройки логгеров.
//
// Без аргументов поведение прежнее: интерактивный TUI при подключённом терминале либо одноразовая
// диагностика при перенаправленных потоках. Явный --mode имеет приоритет над ui.mode конфигурации.

// Явный namespace выбирается до терминала, legacy parser и ранней инициализации вывода.
// Namespace занимает первую позицию; значения legacy-опций командами не являются.
if (args.Length > 0 && args[0] == "agent")
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
    Console.WriteLine(
        $"Без аргументов: ui.mode из {Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath())}; по умолчанию normal.");
    Console.WriteLine("При перенаправлении stdin или stdout: одноразовая read-only диагностика.");
    Console.WriteLine("Машинный JSON-интерфейс: AzurPilot.App agent discover.");
    Console.WriteLine("UI для разработки: --isolated-ui <directory>; по умолчанию внешние изменения состояния и окно просмотра журнала отключены.");
    return AzurPilotExitCode.Success;
}

if (arguments.LogViewer)
{
    // Отдельная лог-консоль — собственный процесс того же приложения: он не собирает хост приложения,
    // не открывает TUI и не управляет состоянием MuMu, Android или игры.
    return await AzurPilotLogViewerHost.RunAsync(args).ConfigureAwait(false);
}

string? isolatedConfiguration = arguments.IsolatedUiRoot is string root
    ? Path.Combine(root, Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath())) : null;
if (isolatedConfiguration is not null)
{
    ApplicationResult validation = AzurPilot.Windows.WindowsConfigurationIsolation.TryValidate(isolatedConfiguration);
    if (validation.IsFailure)
    {
        ApplicationFailure failure = validation.FailureInfo!;
        Console.Error.WriteLine(failure.Message);
        return AzurPilotExitCode.FromFailure(failure);
    }
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
        configurationPath: isolatedConfiguration,
        modeOverride: arguments.Mode,
        isolatedUi: arguments.IsolatedUiRoot is not null,
        liveEnvironmentTarget: arguments.LiveEnvironmentTarget).ConfigureAwait(false);

    if (!interactive.InteractiveSessionStarted || interactive.Startup.ExitCode != AzurPilotExitCode.Success)
    {
        foreach (string line in interactive.Startup.ReportLines)
        {
            Console.WriteLine(line);
        }
    }

    return interactive.Startup.ExitCode;
}

if (arguments.IsolatedUiRoot is not null)
{
    Console.Error.WriteLine("--isolated-ui требует интерактивную консоль; диагностический fallback запрещён.");
    return AzurPilotExitCode.InvalidArgument;
}

AzurPilotStartupResult result = await AzurPilotHost.RunAsync().ConfigureAwait(false);

foreach (string line in result.ReportLines)
{
    Console.WriteLine(line);
}

return result.ExitCode;
