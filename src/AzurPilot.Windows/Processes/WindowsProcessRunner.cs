using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AzurPilot.Core.Failures;

namespace AzurPilot.Windows.Processes;

/// <summary>
/// Production-реализация границы запуска процесса: точный исполняемый файл и список аргументов.
/// </summary>
/// <remarks>
/// <para>
/// Реализация намеренно узкая. Процесс запускается с <c>UseShellExecute = false</c>, без командной
/// строки, без <c>cmd</c> и без PowerShell как слоя оркестрации: оболочка не участвует в запуске,
/// поэтому аргумент не может быть переинтерпретирован. Захваченный вывод ограничен по длине; полный
/// stdout/stderr никуда не логируется, а признак усечения сообщается вызывающей стороне.
/// </para>
/// <para>
/// По истечении дедлайна завершается ровно тот процесс, который создала эта реализация. Массовое
/// завершение процессов, поиск процессов по имени и завершение дерева процессов не выполняются:
/// граница не управляет чужими процессами.
/// </para>
/// <para>
/// Смысл ожидаемых отказов реализация не выбирает: она сообщает их через обязательную
/// <see cref="IProcessFailureProjection"/> владельца возможности, поэтому один и тот же код запуска
/// обслуживает разные возможности без второго набора кодов и без скрытого значения по умолчанию.
/// </para>
/// <para>
/// Отмена операции и превышение дедлайна — ожидаемые отказы операции, а не исключения: они
/// возвращаются значением <see cref="ApplicationResult{T}"/>.
/// </para>
/// </remarks>
public sealed class WindowsProcessRunner : IWindowsProcessRunner
{
    /// <summary>Предельное число символов, захватываемых из каждого потока вывода процесса.</summary>
    public const int MaximumCapturedCharactersPerStream = 131072;

    private const int ReadBufferCharacters = 4096;

    private readonly IProcessFailureProjection _failureProjection;

    /// <summary>Создаёт границу запуска процесса с проекцией отказов её владельца.</summary>
    /// <param name="failureProjection">
    /// Проекция отказов запуска, дедлайна и отмены в application-level отказ.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="failureProjection"/> равен <see langword="null"/>.
    /// </exception>
    public WindowsProcessRunner(IProcessFailureProjection failureProjection)
    {
        ArgumentNullException.ThrowIfNull(failureProjection);
        _failureProjection = failureProjection;
    }

    /// <summary>Запускает процесс и дожидается его завершения в пределах дедлайна.</summary>
    /// <param name="request">Описание запуска.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Результат завершившегося процесса либо application-level отказ.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Запрос не является корректным описанием запуска.</exception>
    public async Task<ApplicationResult<WindowsProcessOutcome>> RunAsync(
        WindowsProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        ProcessStartInfo startInfo = new()
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!string.IsNullOrEmpty(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                return ApplicationResult<WindowsProcessOutcome>.Failure(
                    _failureProjection.StartFailed(request.ExecutablePath, null));
            }
        }
        catch (Win32Exception exception)
        {
            return ApplicationResult<WindowsProcessOutcome>.Failure(
                _failureProjection.StartFailed(request.ExecutablePath, exception));
        }
        catch (InvalidOperationException exception)
        {
            return ApplicationResult<WindowsProcessOutcome>.Failure(
                _failureProjection.StartFailed(request.ExecutablePath, exception));
        }

        Task<BoundedText> standardOutputTask = ReadBoundedAsync(process.StandardOutput);
        Task<BoundedText> standardErrorTask = ReadBoundedAsync(process.StandardError);

        using CancellationTokenSource deadlineSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(request.Timeout);

        bool deadlineExpired = false;
        try
        {
            await process.WaitForExitAsync(deadlineSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            deadlineExpired = !cancellationToken.IsCancellationRequested;
            TerminateOwnProcess(process);
            await WaitForExitAfterTerminationAsync(process).ConfigureAwait(false);
        }

        BoundedText standardOutput = await standardOutputTask.ConfigureAwait(false);
        BoundedText standardError = await standardErrorTask.ConfigureAwait(false);
        stopwatch.Stop();

        if (deadlineExpired)
        {
            return ApplicationResult<WindowsProcessOutcome>.Failure(
                _failureProjection.TimedOut(
                    request.ExecutablePath,
                    stopwatch.Elapsed,
                    standardOutput.TotalCharacters,
                    standardError.TotalCharacters));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ApplicationResult<WindowsProcessOutcome>.Failure(_failureProjection.Cancelled());
        }

        return ApplicationResult<WindowsProcessOutcome>.Success(new WindowsProcessOutcome
        {
            ExitCode = process.ExitCode,
            StandardOutput = standardOutput.Text,
            StandardError = standardError.Text,
            Duration = stopwatch.Elapsed,
            StandardOutputTruncated = standardOutput.IsTruncated,
            StandardErrorTruncated = standardError.IsTruncated,
        });
    }

    private static void Validate(WindowsProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            throw new ArgumentException("Путь к исполняемому файлу не задан.", nameof(request));
        }

        if (!Path.IsPathFullyQualified(request.ExecutablePath))
        {
            throw new ArgumentException(
                "Путь к исполняемому файлу должен быть абсолютным.", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Arguments);

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Дедлайн запуска процесса должен быть положительным.", nameof(request));
        }

        if (request.WorkingDirectory is not null && !Path.IsPathFullyQualified(request.WorkingDirectory))
        {
            throw new ArgumentException("Рабочий каталог процесса должен быть абсолютным путём.", nameof(request));
        }
    }

    private static async Task<BoundedText> ReadBoundedAsync(StreamReader reader)
    {
        StringBuilder builder = new();
        char[] buffer = new char[ReadBufferCharacters];
        int totalCharacters = 0;
        bool isTruncated = false;

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Поток закрылся вместе с процессом: то, что успело накопиться, остаётся доказательством.
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            totalCharacters += read;
            int free = MaximumCapturedCharactersPerStream - builder.Length;
            if (free > 0)
            {
                _ = builder.Append(buffer, 0, Math.Min(read, free));
            }

            if (totalCharacters > MaximumCapturedCharactersPerStream)
            {
                isTruncated = true;
            }
        }

        return new BoundedText(builder.ToString(), isTruncated, totalCharacters);
    }

    private static void TerminateOwnProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // Завершается только процесс, созданный этой реализацией, и только он сам:
                // дерево процессов и чужие процессы не затрагиваются.
                process.Kill(entireProcessTree: false);
            }
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершился между проверкой и завершением.
        }
        catch (Win32Exception)
        {
            // Процесс завершился сам; состояние процесса читается дальше как обычно.
        }
        catch (NotSupportedException)
        {
            // Платформа не поддерживает завершение: процесс остаётся жив, отказ уже сформирован.
        }
    }

    private static async Task WaitForExitAfterTerminationAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Процесс уже перестал существовать.
        }
    }

    private readonly record struct BoundedText(string Text, bool IsTruncated, int TotalCharacters);
}
