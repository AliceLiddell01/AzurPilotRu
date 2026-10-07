namespace AzurPilot.Core.Android;

/// <summary>
/// Точный ADB endpoint, которым адресуется конкретное Android-устройство: host и порт.
/// </summary>
/// <remarks>
/// <para>
/// Endpoint — это identity target: и наблюдение, и mutation адресуются ровно этой паре «host + порт», а
/// не «какому-то устройству в списке». Поэтому каноническая текстовая форма значения — <c>host:port</c>,
/// её возвращает <see cref="ToString"/> и используют структурированные логи и bounded details отказов.
/// </para>
/// <para>
/// Значение неизменяемо и сравнивается по значению: два endpoint-а равны тогда и только тогда, когда
/// совпадают host и порт. Порт хранится числом, поэтому <c>127.0.0.1:5555</c> и <c>127.0.0.1:05555</c>
/// дают одно и то же значение, а не два разных target-а.
/// </para>
/// <para>
/// Пустой host и порт вне диапазона <c>1..65535</c> — ошибка программирования вызывающей стороны, а не
/// ожидаемый отказ Android: значение приходит из уже проверенной установки и выбранной identity
/// экземпляра, поэтому его отсутствие означает нарушение контракта, а не состояние устройства.
/// </para>
/// <para>
/// Конструктор по умолчанию <c>default</c> не является валидным endpoint-ом: у него пустой host и
/// нулевой порт. Валидное значение создаётся только явным конструктором или <see cref="FromHostPort"/>.
/// </para>
/// </remarks>
public readonly record struct AndroidEndpoint
{
    /// <summary>Минимальный допустимый номер порта ADB endpoint.</summary>
    public const int MinPort = 1;

    /// <summary>Максимальный допустимый номер порта ADB endpoint.</summary>
    public const int MaxPort = 65535;

    /// <summary>Создаёт ADB endpoint по host и порту.</summary>
    /// <param name="host">Host endpoint-а, например <c>127.0.0.1</c>.</param>
    /// <param name="port">Порт endpoint-а в диапазоне <see cref="MinPort"/>..<see cref="MaxPort"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="host"/> пуст, состоит из пробелов или содержит пробельные символы либо <c>':'</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="port"/> вне диапазона <see cref="MinPort"/>..<see cref="MaxPort"/>.
    /// </exception>
    public AndroidEndpoint(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = ValidateHost(host);
        Port = ValidatePort(port);
    }

    /// <summary>Host ADB endpoint-а.</summary>
    /// <value>Непустой host без пробельных символов и без <c>':'</c>.</value>
    public string Host { get; }

    /// <summary>Порт ADB endpoint-а.</summary>
    /// <value>Значение в диапазоне <see cref="MinPort"/>..<see cref="MaxPort"/>.</value>
    public int Port { get; }

    /// <summary>Создаёт ADB endpoint по host и порту.</summary>
    /// <param name="host">Host endpoint-а, например <c>127.0.0.1</c>.</param>
    /// <param name="port">Порт endpoint-а в диапазоне <see cref="MinPort"/>..<see cref="MaxPort"/>.</param>
    /// <returns>ADB endpoint точной пары «host + порт».</returns>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="host"/> пуст, состоит из пробелов или содержит пробельные символы либо <c>':'</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="port"/> вне диапазона <see cref="MinPort"/>..<see cref="MaxPort"/>.
    /// </exception>
    public static AndroidEndpoint FromHostPort(string host, int port) => new(host, port);

    /// <summary>Возвращает каноническую текстовую форму endpoint-а.</summary>
    /// <returns>Строка вида <c>host:port</c>.</returns>
    public override string ToString() => Host + ":" + Port;

    private static string ValidateHost(string host)
    {
        string trimmed = host.Trim();

        if (trimmed.Length == 0)
        {
            throw new ArgumentException(
                "Host ADB endpoint не может быть пустым или состоять из пробелов.",
                nameof(host));
        }

        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            // Каноническая форма endpoint-а — host:port, поэтому host с двоеточием сделал бы форму
            // неоднозначной: значение приходит раздельно и двоеточия в host не содержит.
            throw new ArgumentException(
                "Host ADB endpoint не может содержать ':': каноническая форма endpoint-а — host:port.",
                nameof(host));
        }

        if (ContainsWhitespace(trimmed))
        {
            throw new ArgumentException(
                "Host ADB endpoint не может содержать пробельные символы.",
                nameof(host));
        }

        return trimmed;
    }

    private static int ValidatePort(int port)
    {
        if (port is < MinPort or > MaxPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                port,
                $"Порт ADB endpoint должен быть в диапазоне {MinPort}..{MaxPort}.");
        }

        return port;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (char symbol in value)
        {
            if (char.IsWhiteSpace(symbol))
            {
                return true;
            }
        }

        return false;
    }
}
