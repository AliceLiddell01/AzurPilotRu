using System.Collections.Frozen;
using System.Text.RegularExpressions;
using AzurPilot.Core.Failures;

namespace AzurPilot.Windows;

/// <summary>
/// Проекция исключений platform/native boundary в стабильный application-level отказ
/// <see cref="ApplicationFailure"/>.
/// </summary>
/// <remarks>
/// <para>
/// Маппер — чистая функция без состояния: он не бросает исключений, ничего не логирует и не решает,
/// что делать с отказом. Application host использует его, чтобы превратить ошибку границы в
/// значение <see cref="ApplicationResult"/>, на которое опираются диагностика и логирование.
/// </para>
/// <para>
/// Коды отказа берутся из <see cref="ApplicationFailure"/>: строковые литералы кодов в этом файле не
/// дублируются, и второй каталог кодов не заводится. Маппер не добавляет новых кодов.
/// </para>
/// <para>
/// <see cref="ApplicationFailure.Message"/> сохраняет человекочитаемую диагностику исключения целиком,
/// чтобы не терять машинно-читаемые данные (код возврата native стороны, имя библиотеки). Дополнительно
/// эти данные извлекаются в ограниченные <see cref="ApplicationFailure.Details"/>; полный дамп
/// сообщения или данных исключения в details не попадает.
/// </para>
/// </remarks>
public static class NativeBoundaryFailureMapper
{
    /// <summary>Ключ details с CLR-именем типа исходного исключения.</summary>
    public const string ExceptionTypeKey = "exception_type";

    /// <summary>Ключ details с именем native библиотеки, замороженным контрактом ABI.</summary>
    public const string NativeLibraryKey = "native_library";

    /// <summary>Ключ details с кодом возврата native стороны.</summary>
    public const string NativeStatusCodeKey = "native_status_code";

    /// <summary>
    /// Извлекает код возврата native стороны из диагностического сообщения исключения границы.
    /// </summary>
    /// <remarks>
    /// Форма сообщения принадлежит <see cref="AzurPilotNativeBridge"/>: она заморожена и содержит код
    /// возврата. Регулярное выражение только читает это значение; при несовпадении код возврата в
    /// details не попадает, а не подменяется догадкой.
    /// </remarks>
    private static readonly Regex NativeStatusCodePattern = new(
        @"завершилась с кодом (?<code>-?\d+)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Проецирует исключение границы в application-level отказ.</summary>
    /// <param name="exception">Исключение, полученное от native/platform boundary.</param>
    /// <returns>Отказ с одним из стабильных кодов <see cref="ApplicationFailure"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> равен <see langword="null"/>.</exception>
    public static ApplicationFailure Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // Native библиотека недоступна: файл отсутствует, повреждён или не загружается.
            // Повтор имеет смысл после восстановления native runtime.
            NativeBoundaryUnavailableException => new ApplicationFailure
            {
                Code = ApplicationFailure.NativeUnavailable,
                Message = exception.Message,
                IsRetryable = true,
                Details = BuildDetails(exception, includeLibrary: true, includeStatusCode: false),
            },

            // Native библиотека загружена, но её ABI несовместим: повтор без пересборки бесполезен.
            NativeAbiMismatchException => new ApplicationFailure
            {
                Code = ApplicationFailure.NativeIncompatible,
                Message = exception.Message,
                IsRetryable = false,
                Details = BuildDetails(exception, includeLibrary: true, includeStatusCode: false),
            },

            // Отмена — ожидаемый исход операции, а не ошибка: повтор допустим по решению вызывающей стороны.
            OperationCanceledException => new ApplicationFailure
            {
                Code = ApplicationFailure.OperationCancelled,
                Message = exception.Message,
                IsRetryable = true,
            },

            // Прочие ошибки границы (например, код возврата native стороны): внутренняя ошибка.
            AzurPilotNativeBoundaryException => new ApplicationFailure
            {
                Code = ApplicationFailure.InternalError,
                Message = exception.Message,
                IsRetryable = false,
                Details = BuildDetails(exception, includeLibrary: true, includeStatusCode: true),
            },

            // Неожиданное исключение вне контракта границы: сообщение формирует маппер, чтобы
            // ограничить details типом исключения и не смешивать языки диагностики.
            _ => new ApplicationFailure
            {
                Code = ApplicationFailure.InternalError,
                Message = $"Неожиданная ошибка платформенной границы: {exception.GetType().Name}.",
                IsRetryable = false,
                Details = BuildDetails(exception, includeLibrary: false, includeStatusCode: false),
            },
        };
    }

    /// <summary>
    /// Проецирует несовместимость native boundary в application-level отказ.
    /// </summary>
    /// <remarks>
    /// Второй вход той же проекции: контракт границы может быть не подтверждён значением, а не
    /// исключением — проверка совместимости сообщает причину данными. Код отказа выбирает эта проекция,
    /// поэтому application host не решает его повторно и не собирает отказ вручную.
    /// </remarks>
    /// <param name="reason">Причина несовместимости, полученная из проверки контракта границы.</param>
    /// <returns>
    /// Отказ с кодом <see cref="ApplicationFailure.NativeIncompatible"/>: повтор без пересборки бесполезен.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> пуст или состоит из пробелов.</exception>
    public static ApplicationFailure MapIncompatibility(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new ApplicationFailure
        {
            Code = ApplicationFailure.NativeIncompatible,
            Message = reason,
            IsRetryable = false,
        };
    }

    /// <summary>Собирает ограниченный набор structured details отказа.</summary>
    /// <param name="exception">Исходное исключение границы.</param>
    /// <param name="includeLibrary">Добавлять ли имя native библиотеки.</param>
    /// <param name="includeStatusCode">Пытаться ли извлечь код возврата native стороны.</param>
    /// <returns>Details с CLR-именем типа исключения и, при необходимости, данными границы.</returns>
    private static FrozenDictionary<string, string> BuildDetails(
        Exception exception,
        bool includeLibrary,
        bool includeStatusCode)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [ExceptionTypeKey] = exception.GetType().Name,
        };

        if (includeLibrary)
        {
            details[NativeLibraryKey] = AzurPilotNativeBridge.LibraryName;
        }

        if (includeStatusCode
            && NativeStatusCodePattern.Match(exception.Message) is { Success: true } match)
        {
            details[NativeStatusCodeKey] = match.Groups["code"].Value;
        }

        return details.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
