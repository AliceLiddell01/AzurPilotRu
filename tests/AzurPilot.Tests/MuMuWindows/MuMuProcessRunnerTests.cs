using System.Diagnostics;
using AzurPilot.Core.Failures;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Доказательства границы запуска процесса: точный исполняемый файл и список аргументов, отсутствие
/// оболочки, ограниченный захват вывода, дедлайн, отмена и завершение только собственного процесса.
/// </summary>
/// <remarks>
/// <para>
/// Проверки используют только нейтральные системные исполняемые файлы, полученные из окружения, — ни
/// MuMu, ни её control utility не запускаются. Абсолютные пути собираются в runtime, поэтому
/// machine-specific констант в исходниках проверок нет.
/// </para>
/// <para>
/// Завершение процессов проверяется отдельно: посторонний процесс, запущенный вне границы, не должен
/// быть затронут превышением дедлайна у процесса границы.
/// </para>
/// </remarks>
[Trait("Category", "MuMuWindows")]
public sealed class MuMuProcessRunnerTests
{
    private static readonly MuMuProcessRunner Runner = new();
    private static readonly string SystemDirectory = Environment.SystemDirectory;

    [Fact(DisplayName = "Относительный путь к исполняемому файлу — ошибка программирования")]
    public async Task RelativeExecutablePathIsProgrammingError()
    {
        MuMuProcessRequest request = new()
        {
            ExecutablePath = "MuMuManager.exe",
            Arguments = [],
            Timeout = TimeSpan.FromSeconds(1),
        };

        _ = await Assert.ThrowsAsync<ArgumentException>(() => Runner.RunAsync(request, CancellationToken.None));
    }

    [Fact(DisplayName = "Неположительный дедлайн — ошибка программирования")]
    public async Task NonPositiveTimeoutIsProgrammingError()
    {
        MuMuProcessRequest request = new()
        {
            ExecutablePath = Path.Combine(SystemDirectory, "ping.exe"),
            Arguments = ["-n", "1", "127.0.0.1"],
            Timeout = TimeSpan.Zero,
        };

        _ = await Assert.ThrowsAsync<ArgumentException>(() => Runner.RunAsync(request, CancellationToken.None));
    }

    [Fact(DisplayName = "Недостижимый исполняемый файл проецируется в отказ control surface")]
    public async Task UnreachableExecutableBecomesControlSurfaceFailure()
    {
        string executablePath = Path.Combine(SystemDirectory, "azurpilot-mumu-no-such-utility.exe");

        MuMuProcessRequest request = new()
        {
            ExecutablePath = executablePath,
            Arguments = ["version"],
            Timeout = TimeSpan.FromSeconds(5),
        };

        ApplicationResult<MuMuProcessOutcome> result = await Runner.RunAsync(request, CancellationToken.None);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.MuMuControlSurfaceUnsupported, failure.Code);
        Assert.Equal(MuMuFailureReasons.ProcessStartFailed, failure.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal(Path.GetFileName(executablePath), failure.Details![MuMuFailureDetailKeys.ExecutableName]);
        Assert.DoesNotContain(SystemDirectory, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Завершившийся процесс отдаёт код выхода, вывод и длительность")]
    public async Task CompletedProcessReturnsOutcome()
    {
        ApplicationResult<MuMuProcessOutcome> result = await Runner.RunAsync(
            CreateRequest("ping.exe", TimeSpan.FromSeconds(30), "-n", "1", "127.0.0.1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        MuMuProcessOutcome outcome = result.Value!;

        Assert.Equal(0, outcome.ExitCode);
        Assert.NotEmpty(outcome.StandardOutput);
        Assert.False(outcome.StandardOutputTruncated);
        Assert.False(outcome.StandardErrorTruncated);
        Assert.True(outcome.Duration > TimeSpan.Zero);
    }

    [Fact(DisplayName = "Аргументы передаются списком и не интерпретируются оболочкой")]
    public async Task ArgumentsArePassedWithoutShellInterpolation()
    {
        string directory = MuMuWindowsTestPaths.Create("shell-free");
        _ = Directory.CreateDirectory(directory);

        // Имя файла содержит пробел и метасимвол оболочки: строка командной строки, собранная через
        // оболочку, разобрала бы такой аргумент как несколько.
        string payloadPath = Path.Combine(directory, "payload & marker.txt");
        await File.WriteAllTextAsync(payloadPath, "azurpilot-mumu-marker", CancellationToken.None);

        try
        {
            ApplicationResult<MuMuProcessOutcome> result = await Runner.RunAsync(
                CreateRequest("findstr.exe", TimeSpan.FromSeconds(30), "/R", "/N", "^", payloadPath),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(0, result.Value!.ExitCode);
            Assert.Contains("azurpilot-mumu-marker", result.Value!.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(payloadPath);
        }
    }

    [Fact(DisplayName = "Захваченный вывод ограничен, а усечение сообщается явно")]
    public async Task CapturedOutputIsBounded()
    {
        string directory = MuMuWindowsTestPaths.Create("bounds");
        _ = Directory.CreateDirectory(directory);
        string payloadPath = Path.Combine(directory, "payload.txt");

        string[] lines = [.. Enumerable.Range(0, 5000).Select(index => $"line-{index}-{new string('x', 32)}")];
        await File.WriteAllLinesAsync(payloadPath, lines, CancellationToken.None);

        try
        {
            ApplicationResult<MuMuProcessOutcome> result = await Runner.RunAsync(
                CreateRequest("findstr.exe", TimeSpan.FromSeconds(60), "/R", "/N", "^", payloadPath),
                CancellationToken.None);

            Assert.True(result.IsSuccess);

            MuMuProcessOutcome outcome = result.Value!;

            Assert.Equal(0, outcome.ExitCode);
            Assert.True(outcome.StandardOutputTruncated);
            Assert.Equal(
                MuMuProcessRunner.MaximumCapturedCharactersPerStream,
                outcome.StandardOutput.Length);
        }
        finally
        {
            File.Delete(payloadPath);
        }
    }

    [Fact(DisplayName = "Дедлайн завершает только собственный процесс и даёт отказ таймаута")]
    public async Task DeadlineIsEnforcedOnOwnProcessOnly()
    {
        using Process foreign = StartForeignProcess();

        try
        {
            ApplicationResult<MuMuProcessOutcome> result = await Runner.RunAsync(
                CreateRequest("ping.exe", TimeSpan.FromSeconds(1), "-n", "30", "-w", "1000", "127.0.0.1"),
                CancellationToken.None);

            Assert.True(result.IsFailure);

            ApplicationFailure failure = result.FailureInfo!;

            Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, failure.Code);
            Assert.True(failure.IsRetryable);
            Assert.Equal(MuMuFailureReasons.ProcessTimeout, failure.Details![MuMuFailureDetailKeys.Reason]);
            Assert.True(failure.Details.ContainsKey(MuMuFailureDetailKeys.DurationMilliseconds));

            // Посторонний процесс запущен вне границы и не должен быть затронут.
            Assert.False(foreign.HasExited, "Посторонний процесс был завершён границей запуска.");
        }
        finally
        {
            KillForeignProcess(foreign);
        }
    }

    [Fact(DisplayName = "Отмена операции завершает собственный процесс и сообщается кодом отмены")]
    public async Task CancellationTerminatesOwnProcess()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

        ApplicationResult<MuMuProcessOutcome> result = await Runner.RunAsync(
            CreateRequest("ping.exe", TimeSpan.FromSeconds(60), "-n", "30", "-w", "1000", "127.0.0.1"),
            cancellation.Token);

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.OperationCancelled, failure.Code);
        Assert.False(failure.IsRetryable);
    }

    [Fact(DisplayName = "Отмена до завершения процесса сообщается кодом отмены")]
    public async Task CancellationBeforeStartReturnsCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        ApplicationResult<MuMuProcessOutcome> result = await Runner.RunAsync(
            CreateRequest("ping.exe", TimeSpan.FromSeconds(30), "-n", "1", "127.0.0.1"),
            cancellation.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationFailure.OperationCancelled, result.FailureInfo!.Code);
    }

    private static MuMuProcessRequest CreateRequest(
        string executableName,
        TimeSpan timeout,
        params string[] arguments)
        => new()
        {
            ExecutablePath = Path.Combine(SystemDirectory, executableName),
            Arguments = arguments,
            Timeout = timeout,
        };

    private static Process StartForeignProcess()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(SystemDirectory, "ping.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("20");
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("1000");
        startInfo.ArgumentList.Add("127.0.0.1");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить посторонний процесс проверки.");

        // Потоки не читаются: процесс должен жить, пока проверяется поведение границы.
        return process;
    }

    private static void KillForeignProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }

            _ = process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
