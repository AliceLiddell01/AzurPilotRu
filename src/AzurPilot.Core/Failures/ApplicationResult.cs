using System.Diagnostics.CodeAnalysis;

namespace AzurPilot.Core.Failures;

/// <summary>
/// Минимальный типизированный результат операции со значением: либо успех со значением, либо
/// ожидаемый отказ <see cref="ApplicationFailure"/>.
/// </summary>
/// <remarks>
/// <para>
/// Экземпляры создаются только фабриками <see cref="Success"/> и <see cref="Failure"/>: ожидаемый
/// отказ возвращается значением и не выражается исключением. Обращение к <see cref="Value"/> или
/// <see cref="FailureInfo"/> в неподходящем состоянии — ошибка программирования, а не ожидаемый
/// отказ, поэтому она сообщается исключением.
/// </para>
/// <para>
/// Состояние отказа читается свойством <see cref="FailureInfo"/>, а не <c>Failure</c>: в C# имя
/// члена должно быть уникальным внутри типа, поэтому статическая фабрика <see cref="Failure"/> и
/// свойство состояния не могут называться одинаково. Фабрика сохраняет требуемое имя, свойство
/// получает это имя.
/// </para>
/// </remarks>
/// <typeparam name="T">Тип значения успешного результата.</typeparam>
public sealed class ApplicationResult<T>
{
    private readonly bool _isSuccess;
    private readonly T? _value;
    private readonly ApplicationFailure? _failure;

    private ApplicationResult(bool isSuccess, T? value, ApplicationFailure? failure)
    {
        _isSuccess = isSuccess;
        _value = value;
        _failure = failure;
    }

    /// <summary>Создаёт успешный результат с указанным значением.</summary>
    /// <param name="value">Значение успешного результата; для ссылочного типа не должно быть <see langword="null"/>.</param>
    /// <returns>Успешный результат.</returns>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "Статическая фабрика — часть зафиксированного контракта application failure.")]
    public static ApplicationResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ApplicationResult<T>(true, value, null);
    }

    /// <summary>Создаёт результат-отказ без значения.</summary>
    /// <param name="failure">Описание ожидаемого отказа.</param>
    /// <returns>Результат-отказ.</returns>
    [SuppressMessage(
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification = "Статическая фабрика — часть зафиксированного контракта application failure.")]
    public static ApplicationResult<T> Failure(ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new ApplicationResult<T>(false, default, failure);
    }

    /// <summary>Признак успешного завершения операции.</summary>
    public bool IsSuccess => _isSuccess;

    /// <summary>Признак отказа операции.</summary>
    public bool IsFailure => !_isSuccess;

    /// <summary>Значение успешного результата.</summary>
    /// <value>Значение, переданное в <see cref="Success"/>.</value>
    /// <exception cref="InvalidOperationException">Результат является отказом: значения нет.</exception>
    public T? Value
    {
        get
        {
            if (!_isSuccess)
            {
                throw new InvalidOperationException(
                    "Результат-отказ не содержит значения; проверьте IsSuccess перед обращением к Value.");
            }

            return _value;
        }
    }

    /// <summary>Описание ожидаемого отказа.</summary>
    /// <value>Отказ, переданный в <see cref="Failure"/>; для успешного результата — <see langword="null"/>.</value>
    /// <exception cref="InvalidOperationException">Результат успешен: отказа нет.</exception>
    public ApplicationFailure? FailureInfo
    {
        get
        {
            if (_isSuccess)
            {
                throw new InvalidOperationException(
                    "Успешный результат не содержит описания отказа; проверьте IsSuccess перед обращением к FailureInfo.");
            }

            return _failure;
        }
    }

    /// <summary>Возвращает строковое представление результата для диагностики.</summary>
    /// <returns>Код отказа либо признак успеха.</returns>
    public override string ToString()
        => _isSuccess
            ? "success"
            : $"failure:{_failure!.Code}";

    /// <summary>Разбирает результат на признак успеха и значение.</summary>
    /// <param name="isSuccess">Признак успешного завершения операции.</param>
    /// <param name="value">Значение успешного результата.</param>
    /// <exception cref="InvalidOperationException">Результат является отказом: значения нет.</exception>
    public void Deconstruct(out bool isSuccess, [MaybeNullWhen(false)] out T value)
    {
        isSuccess = _isSuccess;
        value = Value!;
    }
}

/// <summary>
/// Минимальный результат операции без значения: либо успех, либо ожидаемый отказ
/// <see cref="ApplicationFailure"/>.
/// </summary>
/// <remarks>
/// <para>
/// Экземпляры создаются только фабриками <see cref="Success()"/> и <see cref="Failure"/>: ожидаемый
/// отказ возвращается значением и не выражается исключением. Обращение к <see cref="FailureInfo"/>
/// при успешном результате — ошибка программирования и сообщается исключением.
/// </para>
/// <para>
/// Причина имени <see cref="FailureInfo"/> вместо <c>Failure</c> — та же, что и у
/// <see cref="ApplicationResult{T}"/>: имя члена уникально внутри типа.
/// </para>
/// </remarks>
public sealed class ApplicationResult
{
    private static readonly ApplicationResult SuccessInstance = new(true, null);

    private readonly bool _isSuccess;
    private readonly ApplicationFailure? _failure;

    private ApplicationResult(bool isSuccess, ApplicationFailure? failure)
    {
        _isSuccess = isSuccess;
        _failure = failure;
    }

    /// <summary>Создаёт успешный результат операции без значения.</summary>
    /// <returns>Успешный результат.</returns>
    public static ApplicationResult Success() => SuccessInstance;

    /// <summary>Создаёт результат-отказ.</summary>
    /// <param name="failure">Описание ожидаемого отказа.</param>
    /// <returns>Результат-отказ.</returns>
    public static ApplicationResult Failure(ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new ApplicationResult(false, failure);
    }

    /// <summary>Признак успешного завершения операции.</summary>
    public bool IsSuccess => _isSuccess;

    /// <summary>Признак отказа операции.</summary>
    public bool IsFailure => !_isSuccess;

    /// <summary>Описание ожидаемого отказа.</summary>
    /// <value>Отказ, переданный в <see cref="Failure"/>; для успешного результата — <see langword="null"/>.</value>
    /// <exception cref="InvalidOperationException">Результат успешен: отказа нет.</exception>
    public ApplicationFailure? FailureInfo
    {
        get
        {
            if (_isSuccess)
            {
                throw new InvalidOperationException(
                    "Успешный результат не содержит описания отказа; проверьте IsSuccess перед обращением к FailureInfo.");
            }

            return _failure;
        }
    }

    /// <summary>Возвращает строковое представление результата для диагностики.</summary>
    /// <returns>Код отказа либо признак успеха.</returns>
    public override string ToString()
        => _isSuccess
            ? "success"
            : $"failure:{_failure!.Code}";
}
