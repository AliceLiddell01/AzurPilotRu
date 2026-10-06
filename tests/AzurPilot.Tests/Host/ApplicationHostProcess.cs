using System.Diagnostics;
using System.Text;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Запуск реального <c>AzurPilot.App.exe</c> отдельным процессом — так, как его запускает пользователь.
/// </summary>
/// <remarks>
/// <para>
/// Проверки границы stdout/stderr и согласованности кода выхода выполняются только на реальном
/// процессе: перенаправление <c>Console</c> внутри процесса теста не доказывает поведение приложения.
/// </para>
/// <para>
/// Полный output приложения (apphost, managed и native runtime) ставит в подкаталог <c>app-host</c>
/// тестового выхода staging-таргет <c>Directory.Build.targets</c>.
/// </para>
/// <para>
/// Продукт не разбирает аргументы запуска, поэтому путь конфигурации здесь не передаётся: приложение
/// читает runtime-путь по умолчанию. Проверки формулируются свойствами, которые верны и при
/// существующем пользовательском <c>%LOCALAPPDATA%\AzurPilot\config.json</c>, и при его отсутствии:
/// файл пользователя не подменяется, не создаётся и не удаляется.
/// </para>
/// </remarks>
internal sealed class ApplicationHostProcess
{
    private const string StagedFolderName = "app-host";
    private const string ExecutableName = "AzurPilot.App.exe";
    private const int ProcessTimeoutMilliseconds = 60_000;

    private readonly string _root;

    private ApplicationHostProcess(string root) => _root = root;

    /// <summary>Каталог застейдженного полного output приложения внутри тестового выхода.</summary>
    private static string StagedDirectory => Path.Combine(AppContext.BaseDirectory, StagedFolderName);

    /// <summary>Открывает застейдженный output приложения для запуска без изменения каталога.</summary>
    /// <returns>Запуск приложения из застейдженного каталога.</returns>
    internal static ApplicationHostProcess CreateStaged()
    {
        string executable = Path.Combine(StagedDirectory, ExecutableName);
        Assert.True(File.Exists(executable), $"Не застейджен полный output приложения: {executable}.");

        return new ApplicationHostProcess(StagedDirectory);
    }

    /// <summary>Запускает приложение без аргументов.</summary>
    /// <returns>Код выхода, оба потока вывода и прочитанные structured записи stderr.</returns>
    internal ApplicationHostRun Run() => Run([]);

    /// <summary>Запускает приложение с переданными аргументами командной строки.</summary>
    /// <param name="arguments">Аргументы запуска, которые продукт обязан игнорировать.</param>
    /// <returns>Код выхода, оба потока вывода и прочитанные structured записи stderr.</returns>
    internal ApplicationHostRun Run(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo startInfo = new(Path.Combine(_root, ExecutableName))
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить процесс приложения.");

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Процесс приложения не завершился за отведённое время.");
        }

        string standardOutput = output.GetAwaiter().GetResult();
        string standardError = error.GetAwaiter().GetResult();
        return new ApplicationHostRun(
            process.ExitCode,
            standardOutput,
            standardError,
            StructuredLogRecord.ReadFrom(standardError));
    }
}

/// <summary>Результат запуска реального процесса приложения.</summary>
/// <param name="ExitCode">Код выхода процесса.</param>
/// <param name="StandardOutput">Содержимое stdout процесса.</param>
/// <param name="StandardError">Содержимое stderr процесса.</param>
/// <param name="Logs">Structured записи, прочитанные из stderr.</param>
internal sealed record ApplicationHostRun(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    IReadOnlyList<StructuredLogRecord> Logs);
