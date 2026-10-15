using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AzurPilot.App.LogViewer;

/// <summary>
/// Единственный владелец канала «application host → отдельная лог-консоль»: пределы полей,
/// санитизация внешнего текста, построчный JSON-формат и handshake.
/// </summary>
/// <remarks>
/// <para>
/// Протокол однонаправленный: host пишет строки JSON, viewer их только читает. Единственное сообщение
/// в обратную сторону — handshake-строка: она сообщает готовность клиента и больше ни на что не влияет.
/// </para>
/// <para>
/// Текст журнала — недоверенный вход: сообщение может содержать ANSI/terminal escape и управляющие
/// последовательности. Они удаляются здесь, до всякой сериализации и отрисовки, поэтому escape не
/// попадает ни в канал, ни в чужой терминал.
/// </para>
/// <para>
/// Пределы полей заданы здесь же, потому что это части одного формата: строка канала ограничена по
/// длине, а превышение отклоняется детерминированно, а не приводит к неограниченному буферу.
/// </para>
/// </remarks>
internal static partial class AzurPilotLogViewerProtocol
{
    /// <summary>Версия протокола канала.</summary>
    internal const int Version = 1;

    /// <summary>Префикс имени канала: дальше идёт уникальный идентификатор запуска.</summary>
    internal const string PipeNamePrefix = "azurpilot-log-viewer-";

    /// <summary>Предельная длина имени канала.</summary>
    internal const int MaximumPipeNameLength = 128;

    /// <summary>Строка готовности клиента: единственное сообщение viewer-а в сторону host-а.</summary>
    internal const string HandshakeLine = "azurpilot-log-viewer/1 ready";

    /// <summary>Предельная длина одной строки канала в байтах UTF-8.</summary>
    internal const int MaximumLineBytes = 32 * 1024;

    /// <summary>Предельная длина сообщения записи.</summary>
    internal const int MaximumMessageLength = 2_048;

    /// <summary>Предельная длина категории логирования.</summary>
    internal const int MaximumCategoryLength = 160;

    /// <summary>Предельная длина идентификаторов записи (event id и correlation identifier).</summary>
    internal const int MaximumIdentifierLength = 96;

    /// <summary>Предельное число передаваемых structured properties.</summary>
    internal const int MaximumPropertyCount = 8;

    /// <summary>Предельная длина имени structured property.</summary>
    internal const int MaximumPropertyKeyLength = 64;

    /// <summary>Предельная длина значения structured property.</summary>
    internal const int MaximumPropertyValueLength = 256;

    /// <summary>Имя structured property, несущего correlation identifier операции.</summary>
    internal const string CorrelationPropertyName = "CorrelationId";

    /// <summary>Имя structured property с исходным шаблоном сообщения: в канал оно не передаётся.</summary>
    internal const string OriginalFormatPropertyName = "{OriginalFormat}";

    private static readonly Encoding LineEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Создаёт уникальное имя канала для одного запуска.</summary>
    /// <returns>Имя, пригодное для <c>NamedPipeServerStream</c> и аргумента <c>--log-viewer-pipe</c>.</returns>
    internal static string CreatePipeName()
        => string.Concat(PipeNamePrefix, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    /// <summary>Проверяет, что имя канала допустимо для named pipe.</summary>
    /// <param name="name">Имя канала.</param>
    /// <returns><see langword="true"/>, если имя непусто, ограничено по длине и не содержит разделителей пути.</returns>
    internal static bool IsValidPipeName(string name)
        => name.Length is > 0 and <= MaximumPipeNameLength
            && !name.Contains('\\')
            && !name.Contains('/');

    /// <summary>
    /// Ограничивает недоверенный текст: управляющие последовательности заменяются пробелами, длина
    /// ограничивается без обрыва surrogate pair.
    /// </summary>
    /// <param name="value">Внешний текст; <see langword="null"/> даёт пустую строку.</param>
    /// <param name="maximumLength">Предел UTF-16 длины результата.</param>
    /// <returns>Текст без управляющих символов, не длиннее <paramref name="maximumLength"/>.</returns>
    /// <remarks>
    /// Признаком усечения остаётся последний символ результата: получатель видит, что текст обрезан, а
    /// не принимает обрезанное за целое. Замена управляющих символов пробелом, а не удалением, сохраняет
    /// разделение слов исходного сообщения.
    /// </remarks>
    internal static string Sanitize(string? value, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder clean = new(Math.Min(value.Length, maximumLength));
        foreach (Rune rune in value.EnumerateRunes())
        {
            string text = Rune.IsControl(rune) ? " " : rune.ToString();
            if (clean.Length + text.Length > maximumLength)
            {
                if (clean.Length > maximumLength - 1)
                {
                    clean.Length -= clean.Length >= 2 && char.IsLowSurrogate(clean[^1])
                        && char.IsHighSurrogate(clean[^2]) ? 2 : 1;
                }

                _ = clean.Append('…');
                return clean.ToString();
            }

            _ = clean.Append(text);
        }

        return clean.ToString();
    }

    /// <summary>Ограничивает необязательное значение, сохраняя его отсутствие.</summary>
    /// <param name="value">Внешний текст или <see langword="null"/>.</param>
    /// <param name="maximumLength">Предел UTF-16 длины результата.</param>
    /// <returns>Ограниченный текст, пустая строка которого преобразована в <see langword="null"/>.</returns>
    internal static string? SanitizeOptional(string? value, int maximumLength)
    {
        string clean = Sanitize(value, maximumLength);
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>Приводит запись к пределам канала: санитизация полей и обрезка набора properties.</summary>
    /// <param name="logEvent">Запись, построенная логгером application host.</param>
    /// <returns>Запись, сериализация которой гарантированно укладывается в строку канала.</returns>
    internal static AzurPilotLogViewerEvent Bound(AzurPilotLogViewerEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        return logEvent with
        {
            Category = Sanitize(logEvent.Category, MaximumCategoryLength),
            Message = Redact(Sanitize(logEvent.Message, MaximumMessageLength)),
            EventId = SanitizeOptional(logEvent.EventId, MaximumIdentifierLength),
            CorrelationId = SanitizeOptional(logEvent.CorrelationId, MaximumIdentifierLength),
            Properties = BoundProperties(logEvent.Properties),
        };
    }

    /// <summary>Сериализует запись в одну строку JSON без разделителя строки.</summary>
    /// <param name="logEvent">Запись канала.</param>
    /// <returns>JSON-объект записи.</returns>
    internal static string Serialize(AzurPilotLogViewerEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        return JsonSerializer.Serialize(logEvent, AzurPilotLogViewerJsonContext.Default.AzurPilotLogViewerEvent);
    }

    /// <summary>Кодирует строку канала вместе с разделителем <c>\n</c>.</summary>
    /// <param name="line">Строка протокола.</param>
    /// <returns>Байты UTF-8 строки и разделитель.</returns>
    internal static byte[] EncodeLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        byte[] payload = new byte[LineEncoding.GetByteCount(line) + 1];
        int written = LineEncoding.GetBytes(line, payload);
        payload[written] = (byte)'\n';
        return payload;
    }

    /// <summary>Кодирует строку без разделителя.</summary>
    /// <param name="line">Строка протокола.</param>
    /// <returns>Байты UTF-8 строки.</returns>
    internal static byte[] Encode(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return LineEncoding.GetBytes(line);
    }

    /// <summary>Разбирает строку канала как запись журнала.</summary>
    /// <param name="line">Строка протокола без разделителя.</param>
    /// <param name="logEvent">Разобранная запись.</param>
    /// <returns><see langword="true"/>, если строка является записью допустимого формата.</returns>
    /// <remarks>
    /// Разбор не бросает исключений: повреждённая или чужая строка — ожидаемое состояние канала, а не
    /// ошибка программирования. Отсутствующие необязательные поля дают значения по умолчанию.
    /// </remarks>
    internal static bool TryDeserialize(
        string line,
        [NotNullWhen(true)] out AzurPilotLogViewerEvent? logEvent)
    {
        ArgumentNullException.ThrowIfNull(line);
        logEvent = null;

        try
        {
            AzurPilotLogViewerEvent? parsed = JsonSerializer.Deserialize(
                line,
                AzurPilotLogViewerJsonContext.Default.AzurPilotLogViewerEvent);

            if (parsed is null || parsed.Category is null || parsed.Message is null
                || !Enum.IsDefined(parsed.Level))
            {
                return false;
            }

            logEvent = Bound(parsed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ReadOnlyCollection<KeyValuePair<string, string>>? BoundProperties(
        IReadOnlyList<KeyValuePair<string, string>>? properties)
    {
        if (properties is not { Count: > 0 })
        {
            return null;
        }

        List<KeyValuePair<string, string>> bounded = new(Math.Min(properties.Count, MaximumPropertyCount));
        foreach (KeyValuePair<string, string> pair in properties)
        {
            if (bounded.Count == MaximumPropertyCount)
            {
                break;
            }

            string key = Sanitize(pair.Key, MaximumPropertyKeyLength);
            if (key.Length == 0)
            {
                continue;
            }

            bool sensitive = key.Contains("password", StringComparison.OrdinalIgnoreCase)
                || key.Contains("token", StringComparison.OrdinalIgnoreCase)
                || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || key.Contains("key", StringComparison.OrdinalIgnoreCase)
                || key.Contains("authorization", StringComparison.OrdinalIgnoreCase);
            bounded.Add(new KeyValuePair<string, string>(key, sensitive
                ? "[скрыто]" : Redact(Sanitize(pair.Value, MaximumPropertyValueLength))));
        }

        return bounded.Count == 0 ? null : bounded.AsReadOnly();
    }

    private static string Redact(string text)
        => SecretPattern().Replace(BearerPattern().Replace(text, "Bearer [скрыто]"), "$1=[скрыто]");

    [GeneratedRegex(@"\b(password|passwd|token|secret|api[_-]?key|authorization)\s*[:=]\s*.+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BearerPattern();
}

/// <summary>Source-generated метаданные единственного типа, который ходит по каналу.</summary>
[JsonSourceGenerationOptions(
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AzurPilotLogViewerEvent))]
internal sealed partial class AzurPilotLogViewerJsonContext : JsonSerializerContext;
