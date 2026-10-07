using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Разрешает точный ADB endpoint выбранного Android-экземпляра из сведений установки.
/// </summary>
/// <remarks>
/// <para>
/// Источник значения — тот же ответ control surface, который разбирает MuMu-адаптер: резолвер вызывает
/// <see cref="MuMuManagerClient.ForInstallation"/> и существующий
/// <see cref="MuMuManagerClient.QueryInstanceAsync"/>, поэтому форма команды, запуск процесса и разбор
/// ответа остаются у своих владельцев, а второго parser-а и второй пары wire-имён не заводится.
/// </para>
/// <para>
/// Значение по умолчанию не подставляется: если host или порт не сообщены, сообщены в непригодной форме
/// или порт вне диапазона <see cref="AndroidEndpoint.MinPort"/>..<see cref="AndroidEndpoint.MaxPort"/>,
/// endpoint не разрешается, а операция сообщает
/// <see cref="ApplicationFailure.AndroidEndpointUnavailable"/> с machine-stable причиной. Порт не
/// вычисляется по номеру экземпляра и не берётся у другого экземпляра.
/// </para>
/// <para>
/// Резолвер выполняет ровно один запрос сведений: он не выполняет повторных попыток, не подключается к
/// устройству и не выполняет никаких mutation.
/// </para>
/// <para>
/// Отказ чтения сведений не превращается в состояние endpoint-а и не переписывается: отмена запроса
/// пробрасывается без изменений, а причина остальных отказов читается у её владельца
/// (<see cref="MuMuFailureReasons"/>) и сохраняется в details отказа разрешения. Поэтому недостижимый
/// запуск control surface или достигнутый дедлайн процесса не сообщаются как нераспознанный ответ.
/// </para>
/// </remarks>
public sealed class AndroidEndpointResolver
{
    /// <summary>Дедлайн одной команды чтения сведений об экземпляре по умолчанию.</summary>
    /// <remarks>
    /// Значение ограничивает именно отклик control surface: запуск экземпляра здесь не выполняется,
    /// поэтому дедлайн lifecycle MuMu не переиспользуется.
    /// </remarks>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly IWindowsProcessRunner _runner;
    private readonly TimeSpan _commandTimeout;

    /// <summary>Создаёт резолвер с дедлайном команды по умолчанию.</summary>
    /// <param name="runner">Общая граница запуска процесса.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runner"/> равен <see langword="null"/>.</exception>
    public AndroidEndpointResolver(IWindowsProcessRunner runner)
        : this(runner, DefaultCommandTimeout)
    {
    }

    /// <summary>Создаёт резолвер с явным дедлайном команды.</summary>
    /// <param name="runner">Общая граница запуска процесса.</param>
    /// <param name="commandTimeout">Дедлайн одной команды чтения сведений об экземпляре.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runner"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Дедлайн команды не положителен.</exception>
    public AndroidEndpointResolver(IWindowsProcessRunner runner, TimeSpan commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(runner);

        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout),
                commandTimeout,
                "Дедлайн команды чтения сведений об экземпляре должен быть положительным.");
        }

        _runner = runner;
        _commandTimeout = commandTimeout;
    }

    /// <summary>Разрешает точный ADB endpoint запрошенной identity экземпляра.</summary>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Identity экземпляра, для которого запрашивается endpoint.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Успешный результат с точным endpoint-ом либо ожидаемый отказ.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="installation"/> равен <see langword="null"/>.</exception>
    public async Task<ApplicationResult<AndroidEndpoint>> ResolveAsync(
        MuMuInstallation installation,
        MuMuInstanceId instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);

        MuMuManagerClient client = MuMuManagerClient.ForInstallation(_runner, installation, _commandTimeout);
        ApplicationResult<MuMuInstanceQueryResult> query =
            await client.QueryInstanceAsync(instance, cancellationToken).ConfigureAwait(false);

        if (query.IsFailure)
        {
            ApplicationFailure failure = query.FailureInfo!;

            // Отмена — ожидаемый исход запроса отмены, а не состояние endpoint-а: она пробрасывается без
            // изменений, как и любой другой ожидаемый отказ host-а.
            if (failure.Code == ApplicationFailure.OperationCancelled)
            {
                return ApplicationResult<AndroidEndpoint>.Failure(failure);
            }

            // Отказ чтения сведений означает ровно одно: точный адрес не разрешён. Причина при этом
            // остаётся у своего владельца, поэтому недостижимый запуск control surface и достигнутый
            // дедлайн процесса не выдаются за нераспознанный ответ.
            return Unavailable(instance, ProjectedReason(failure));
        }

        MuMuInstanceInfo? info = query.Value!.Instance;

        if (info is null)
        {
            // Провайдер не сообщил сведений об экземпляре: адрес не доказан и не додумывается.
            return Unavailable(instance, AndroidHostFailures.ResponseUnrecognizedReason);
        }

        string? host = info.AdbHostIp;

        if (string.IsNullOrWhiteSpace(host))
        {
            return Unavailable(instance, AndroidHostFailures.HostMissingReason);
        }

        if (!IsUsableHost(host))
        {
            return Unavailable(instance, AndroidHostFailures.HostInvalidReason);
        }

        int? port = info.AdbPort;

        if (port is null)
        {
            return Unavailable(instance, AndroidHostFailures.PortMissingReason);
        }

        if (port is < AndroidEndpoint.MinPort or > AndroidEndpoint.MaxPort)
        {
            return Unavailable(instance, AndroidHostFailures.PortOutOfRangeReason);
        }

        return ApplicationResult<AndroidEndpoint>.Success(
            AndroidEndpoint.FromHostPort(host.Trim(), port.Value));
    }

    private static ApplicationResult<AndroidEndpoint> Unavailable(MuMuInstanceId instance, string reason)
        => ApplicationResult<AndroidEndpoint>.Failure(
            AndroidHostFailures.EndpointUnavailable(instance, reason));

    /// <summary>Читает machine-stable причину отказа чтения сведений у её владельца.</summary>
    /// <remarks>
    /// Причина берётся из details отказа, потому что её владелец — набор <see cref="MuMuFailureReasons"/>,
    /// а не Android-адаптер: резолвер не выводит причину заново и не подменяет её своей. Отказ без
    /// сообщённой причины описывается как нераспознанный ответ — другой причины адаптер не знает.
    /// </remarks>
    /// <param name="failure">Отказ чтения сведений об экземпляре.</param>
    /// <returns>Machine-stable причина для отказа разрешения endpoint-а.</returns>
    private static string ProjectedReason(ApplicationFailure failure)
        => failure.Details is { } details
            && details.TryGetValue(MuMuFailureDetailKeys.Reason, out string? reason)
            && !string.IsNullOrWhiteSpace(reason)
                ? reason
                : AndroidHostFailures.ResponseUnrecognizedReason;

    private static bool IsUsableHost(string host)
    {
        if (host.Contains(':', StringComparison.Ordinal))
        {
            // Каноническая форма endpoint-а — host:port: host с двоеточием сделал бы её неоднозначной.
            return false;
        }

        foreach (char symbol in host)
        {
            if (char.IsWhiteSpace(symbol))
            {
                return false;
            }
        }

        return true;
    }
}
