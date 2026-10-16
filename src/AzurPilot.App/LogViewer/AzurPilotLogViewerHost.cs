using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AzurPilot.App.LogViewer;

/// <summary>
/// Сторона отдельного процесса: читает канал хоста приложения и рисует журнал в своём окне.
/// </summary>
/// <remarks>
/// <para>
/// Viewer ничего не решает и ничем не управляет: он только отображает уже санитизированные записи.
/// Протокол однонаправленный, единственное сообщение в сторону host-а — handshake о готовности канала.
/// </para>
/// <para>
/// Отдельное окно не переиспользует stdout интерактивного экрана: viewer живёт в собственном процессе
/// и собственном окне терминала, поэтому Terminal.Gui-поверхность не получает чужой вывод.
/// </para>
/// </remarks>
public static class AzurPilotLogViewerHost
{
    /// <summary>Аргумент командной строки, включающий режим отдельной лог-консоли.</summary>
    public const string CommandLineSwitch = "--log-viewer";

    /// <summary>Сколько раз viewer пытается переподключиться к тому же каналу.</summary>
    private const int ReconnectAttempts = 3;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    /// <summary>Запускает отдельную лог-консоль и возвращает код выхода её процесса.</summary>
    /// <param name="args">Аргументы процесса.</param>
    /// <param name="cancellationToken">Внешний запрос завершения.</param>
    /// <returns>Код выхода: 0 при штатном завершении, ненулевой при отказе.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!TryParse(args, out ViewerOptions options, out string? error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            return AzurPilotExitCode.InvalidArgument;
        }

        Console.OutputEncoding = Encoding.UTF8;
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void Cancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            shutdown.Cancel();
        }

        Console.CancelKeyPress += Cancel;
        try
        {
            string? lastError = null;
            for (int attempt = 0; attempt < ReconnectAttempts; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(ReconnectDelay, shutdown.Token).ConfigureAwait(false);
                    }

                    return await RenderAsync(options, shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    return AzurPilotExitCode.OperationCancelled;
                }
                catch (OperationCanceledException)
                {
                    lastError = "Истекло время подключения к хосту приложения.";
                }
                catch (TimeoutException exception)
                {
                    lastError = exception.Message;
                }
                catch (IOException exception)
                {
                    lastError = exception.Message;
                }
            }

            await Console.Error
                .WriteLineAsync("Отдельная лог-консоль не подключилась к каналу: " + lastError)
                .ConfigureAwait(false);
            return AzurPilotExitCode.LogViewerUnavailable;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }
    }

    /// <summary>
    /// Форматирует запись в строку разметки Spectre.Console.
    /// </summary>
    /// <param name="logEvent">Запись канала.</param>
    /// <param name="includeDetails">Добавлять correlation и structured properties по явному запросу.</param>
    /// <returns>Строка разметки с семантическим цветом уровня и подсветкой ключевых значений.</returns>
    /// <remarks>
    /// Функция чистая и не требует терминала, поэтому семантика цвета и безопасность форматирования
    /// проверяются детерминированным тестом. Весь недоверенный текст экранируется: escape-последовательность
    /// из сообщения не станет разметкой и не попадёт в терминал отдельного окна.
    /// </remarks>
    internal static string FormatEvent(AzurPilotLogViewerEvent logEvent, bool includeDetails = false)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        logEvent = AzurPilotLogViewerProtocol.Bound(logEvent);
        StringBuilder markup = new();
        _ = markup.Append("[grey]")
            .Append(logEvent.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append("[/] ");
        _ = markup.Append('[').Append(LevelColor(logEvent.Level)).Append(']')
            .Append(LevelLabel(logEvent.Level).PadRight(5))
            .Append("[/] ");
        _ = markup.Append("[blue]").Append(Markup.Escape(logEvent.Category)).Append("[/]");
        if (includeDetails && !string.IsNullOrWhiteSpace(logEvent.CorrelationId))
        {
            _ = markup.Append(" [grey](").Append(Markup.Escape(logEvent.CorrelationId)).Append(")[/]");
        }

        _ = markup.Append(" — ").Append(Markup.Escape(logEvent.Message));
        if (includeDetails && logEvent.Properties is { Count: > 0 } properties)
        {
            foreach (KeyValuePair<string, string> pair in properties)
            {
                _ = markup.Append(" [grey]").Append(Markup.Escape(pair.Key)).Append('=')
                    .Append(Markup.Escape(pair.Value)).Append("[/]");
            }
        }

        return markup.ToString();
    }

    /// <summary>Локализованное название уровня.</summary>
    /// <param name="level">Уровень записи.</param>
    /// <returns>Короткая подпись уровня.</returns>
    internal static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        LogLevel.None => "NONE",
        _ => "INFO",
    };

    /// <summary>Семантический цвет уровня для отдельной консоли.</summary>
    /// <param name="level">Уровень записи.</param>
    /// <returns>Имя цвета Spectre.Console.</returns>
    internal static string LevelColor(LogLevel level) => level switch
    {
        LogLevel.Warning => "yellow",
        LogLevel.Error or LogLevel.Critical => "red",
        LogLevel.Trace or LogLevel.Debug => "grey",
        LogLevel.Information or LogLevel.None => "default",
        _ => "default",
    };

    private static async Task<int> RenderAsync(ViewerOptions options, CancellationToken cancellationToken)
    {
        using NamedPipeClientStream client = new(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(ConnectTimeout);
        await client.ConnectAsync(connect.Token).ConfigureAwait(false);

        // Handshake: единственное сообщение viewer-а в сторону host-а. Оно сообщает готовность канала и
        // больше ни на что не влияет — протокол остаётся однонаправленным по смыслу.
        await client
            .WriteAsync(AzurPilotLogViewerProtocol.EncodeLine(AzurPilotLogViewerProtocol.HandshakeLine),
                cancellationToken)
            .ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            "[grey]Журнал AzurPilot · уровень [/][blue]{0}[/][grey] · закройте окно для завершения[/]",
            Markup.Escape(options.MinimumLevel.ToString("G")));

        AzurPilotLogViewerLineReader reader = new(
            client,
            AzurPilotLogViewerProtocol.MaximumLineBytes);
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // Host закрыл канал: это штатное завершение, а не отказ.
                AnsiConsole.MarkupLine("[grey]Канал журнала закрыт: хост приложения завершил работу.[/]");
                return AzurPilotExitCode.Success;
            }

            if (!AzurPilotLogViewerProtocol.TryDeserialize(line, out AzurPilotLogViewerEvent? logEvent))
            {
                // Чужая или повреждённая строка канала — ожидаемое состояние, а не ошибка.
                continue;
            }

            if (logEvent.Level < options.MinimumLevel)
            {
                continue;
            }

            AnsiConsole.MarkupLine(FormatEvent(logEvent, options.IncludeDetails));
        }
    }

    private static bool TryParse(string[] args, out ViewerOptions options, out string? error)
    {
        options = new ViewerOptions(string.Empty, LogLevel.Information, false);
        ApplicationArguments parsed = ApplicationArguments.Parse(args);
        error = parsed.Failure?.Message;
        if (!parsed.IsSuccess || !parsed.LogViewer
            || parsed.LogViewerPipe is not string pipe || !AzurPilotLogViewerProtocol.IsValidPipeName(pipe))
        {
            error ??= "Не задано или недопустимо имя канала: укажите --log-viewer --log-viewer-pipe <name>.";
            return false;
        }

        options = new ViewerOptions(pipe, parsed.LogViewerLevel ?? LogLevel.Information, parsed.LogViewerDetails);
        return true;
    }

    private readonly record struct ViewerOptions(string PipeName, LogLevel MinimumLevel, bool IncludeDetails);
}