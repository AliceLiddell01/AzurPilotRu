using AzurPilot.Core.Failures;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Клиент bundled ADB: выполняет ровно одну команду через общую границу запуска процесса.
/// </summary>
/// <remarks>
/// <para>
/// Клиент намеренно узкий: он не собирает строку командной строки, не добавляет к запрошенной команде
/// повторного подключения и не выполняет глобальных операций над сервером ADB — в том числе
/// <c>adb kill-server</c>. Операции над другими target не выполняются: точный target приходит в
/// аргументах, которые строит <see cref="AdbCommandBuilder"/>.
/// </para>
/// <para>
/// Одна команда — один вызов границы запуска процесса. Захваченный вывод ограничен границей, а признак
/// усечения сообщается вызывающей стороне и передаётся разбору: усечённый вывод полноценным ответом не
/// считается.
/// </para>
/// <para>
/// Ожидаемые отказы возвращаются значением: смысл отказа выбирает проекция владельца возможности, а
/// клиент не подменяет её код своим.
/// </para>
/// </remarks>
public sealed class AdbClient
{
    /// <summary>Дедлайн одной команды ADB по умолчанию.</summary>
    /// <remarks>
    /// Значение выбрано с запасом относительно наблюдавшихся на реальной установке длительностей команд
    /// ADB: команда адресуется конкретному target-у и не ожидает готовности устройства, поэтому дедлайн
    /// ограничивает именно отклик клиента ADB, а ожидание готовности выполняет orchestration.
    /// </remarks>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly IWindowsProcessRunner _runner;
    private readonly string _adbExecutablePath;
    private readonly TimeSpan _commandTimeout;

    /// <summary>Создаёт клиент с дедлайном команды по умолчанию.</summary>
    /// <param name="runner">Общая граница запуска процесса.</param>
    /// <param name="adbExecutablePath">Абсолютный путь к bundled ADB обнаруженной установки.</param>
    /// <exception cref="ArgumentNullException">Один из аргументов равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Путь к ADB пуст.</exception>
    public AdbClient(IWindowsProcessRunner runner, string adbExecutablePath)
        : this(runner, adbExecutablePath, DefaultCommandTimeout)
    {
    }

    /// <summary>Создаёт клиент с явным дедлайном команды.</summary>
    /// <param name="runner">Общая граница запуска процесса.</param>
    /// <param name="adbExecutablePath">Абсолютный путь к bundled ADB обнаруженной установки.</param>
    /// <param name="commandTimeout">Дедлайн одной команды ADB.</param>
    /// <exception cref="ArgumentNullException">Один из аргументов равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Путь к ADB пуст.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Дедлайн команды не положителен.</exception>
    public AdbClient(IWindowsProcessRunner runner, string adbExecutablePath, TimeSpan commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(adbExecutablePath);

        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout), commandTimeout, "Дедлайн команды ADB должен быть положительным.");
        }

        _runner = runner;
        _adbExecutablePath = adbExecutablePath;
        _commandTimeout = commandTimeout;
    }

    /// <summary>Абсолютный путь к bundled ADB, которым адресуются команды клиента.</summary>
    public string AdbExecutablePath => _adbExecutablePath;

    /// <summary>Дедлайн одной команды ADB.</summary>
    public TimeSpan CommandTimeout => _commandTimeout;

    /// <summary>Выполняет ровно одну команду ADB и дожидается её завершения.</summary>
    /// <param name="arguments">Точные аргументы команды в порядке передачи.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Результат завершившейся команды либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> равен <see langword="null"/>.</exception>
    public Task<ApplicationResult<WindowsProcessOutcome>> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return _runner.RunAsync(
            new WindowsProcessRequest
            {
                ExecutablePath = _adbExecutablePath,
                Arguments = arguments,
                Timeout = _commandTimeout,
            },
            cancellationToken);
    }
}
