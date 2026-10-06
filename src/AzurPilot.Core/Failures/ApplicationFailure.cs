using System.Collections.Frozen;

namespace AzurPilot.Core.Failures;

/// <summary>
/// Application-level описание ожидаемого отказа: стабильный machine-readable код, человекочитаемое
/// сообщение и ограниченные structured details.
/// </summary>
/// <remarks>
/// <para>
/// Контракт фиксирован: допустимы ровно шесть кодов — <see cref="ConfigurationInvalid"/>,
/// <see cref="ConfigurationSchemaUnsupported"/>, <see cref="NativeUnavailable"/>,
/// <see cref="NativeIncompatible"/>, <see cref="OperationCancelled"/> и <see cref="InternalError"/>.
/// Каталог кодов под будущие capability (adb, package, game, screenshot, vision и подобные) намеренно
/// не заводится: код появляется вместе с реальной capability.
/// </para>
/// <para>
/// Экземпляр неизменяем: <see cref="Details"/> копируется в immutable словарь при создании, поэтому
/// внешний словарь-источник не может изменить уже опубликованный отказ.
/// </para>
/// <para>
/// Равенство — по значению, включая содержимое <see cref="Details"/>: два отказа с одинаковыми кодом,
/// сообщением, признаком повторяемости и набором details равны. Сравнение details выполняется
/// по ключам и значениям с ordinal-семантикой, как и у самого словаря.
/// </para>
/// <para>
/// Ожидаемый отказ не выражается исключением: он возвращается значением через
/// <see cref="ApplicationResult"/> или <see cref="ApplicationResult{T}"/>.
/// </para>
/// </remarks>
public sealed record ApplicationFailure : IEquatable<ApplicationFailure>
{
    /// <summary>Код отказа: существующий файл конфигурации не читается или не соответствует схеме v1.</summary>
    public const string ConfigurationInvalid = "configuration_invalid";

    /// <summary>Код отказа: версия схемы конфигурации не поддерживается этой сборкой.</summary>
    public const string ConfigurationSchemaUnsupported = "configuration_schema_unsupported";

    /// <summary>Код отказа: native библиотека недоступна (не найдена или не загружается).</summary>
    public const string NativeUnavailable = "native_unavailable";

    /// <summary>Код отказа: native библиотека загружена, но несовместима с ожидаемым ABI.</summary>
    public const string NativeIncompatible = "native_incompatible";

    /// <summary>Код отказа: операция отменена запросом отмены.</summary>
    public const string OperationCancelled = "operation_cancelled";

    /// <summary>Код отказа: внутренняя ошибка application host, не сводимая к ожидаемым отказам выше.</summary>
    public const string InternalError = "internal_error";

    private static readonly FrozenSet<string> AllowedCodes =
        new[]
        {
            ConfigurationInvalid,
            ConfigurationSchemaUnsupported,
            NativeUnavailable,
            NativeIncompatible,
            OperationCancelled,
            InternalError,
        }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, string>? _details;

    /// <summary>Стабильный machine-readable код отказа из фиксированного набора кодов.</summary>
    /// <value>
    /// Одно из значений: <see cref="ConfigurationInvalid"/>, <see cref="ConfigurationSchemaUnsupported"/>,
    /// <see cref="NativeUnavailable"/>, <see cref="NativeIncompatible"/>, <see cref="OperationCancelled"/>,
    /// <see cref="InternalError"/>. Любое другое значение — ошибка программирования и приводит
    /// к <see cref="ArgumentException"/> при создании отказа.
    /// </value>
    public required string Code
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!AllowedCodes.Contains(value))
            {
                throw new ArgumentException(
                    $"Код отказа \"{value}\" не входит в фиксированный набор application-level кодов.",
                    nameof(value));
            }

            field = value;
        }
    }

    /// <summary>Человекочитаемое описание отказа для оператора (на русском языке).</summary>
    /// <remarks>
    /// Сообщение не является machine-readable контрактом: программы обязаны опираться на
    /// <see cref="Code"/>, а не на текст сообщения.
    /// </remarks>
    public required string Message
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Признак того, что повтор той же операции имеет смысл.</summary>
    /// <value>
    /// <see langword="true"/>, если отказ может быть временным (например, native библиотека временно
    /// недоступна); <see langword="false"/> по умолчанию.
    /// </value>
    public bool IsRetryable { get; init; }

    /// <summary>Ограниченные structured details отказа в формате ключ-значение.</summary>
    /// <value>
    /// Immutable словарь с ordinal-сравнением ключей или <see langword="null"/>, если дополнительных
    /// сведений нет. Значения должны быть пригодны для structured logging.
    /// </value>
    public IReadOnlyDictionary<string, string>? Details
    {
        get => _details;
        init => _details = value?.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Сравнивает отказы по значению, включая содержимое <see cref="Details"/>.</summary>
    /// <param name="other">Отказ для сравнения.</param>
    /// <returns><see langword="true"/>, если все поля и details совпадают по значению.</returns>
    public bool Equals(ApplicationFailure? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return string.Equals(Code, other.Code, StringComparison.Ordinal)
            && string.Equals(Message, other.Message, StringComparison.Ordinal)
            && IsRetryable == other.IsRetryable
            && DetailsEqual(_details, other._details);
    }

    /// <summary>Возвращает hash-код, согласованный с <see cref="Equals(ApplicationFailure?)"/>.</summary>
    /// <returns>Hash-код по коду, сообщению, признаку повторяемости и содержимому details.</returns>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Code, StringComparer.Ordinal);
        hash.Add(Message, StringComparer.Ordinal);
        hash.Add(IsRetryable);

        if (_details is not null)
        {
            // Порядок обхода не влияет на результат: вклад каждого элемента агрегируется сложением,
            // иначе два равных словаря с разным порядком вставки дали бы разные hash-коды.
            int detailsHash = 0;
            foreach (KeyValuePair<string, string> pair in _details)
            {
                detailsHash += HashCode.Combine(
                    StringComparer.Ordinal.GetHashCode(pair.Key),
                    StringComparer.Ordinal.GetHashCode(pair.Value));
            }

            hash.Add(detailsHash);
        }

        return hash.ToHashCode();
    }

    private static bool DetailsEqual(
        FrozenDictionary<string, string>? left,
        FrozenDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in left)
        {
            if (!right.TryGetValue(pair.Key, out string? otherValue)
                || !string.Equals(pair.Value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
