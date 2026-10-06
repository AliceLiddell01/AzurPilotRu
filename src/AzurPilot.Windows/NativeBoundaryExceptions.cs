namespace AzurPilot.Windows;

/// <summary>
/// Ошибка на границе managed/native: native библиотека недоступна или её ABI несовместим.
/// </summary>
/// <remarks>
/// Тип наследуется от <see cref="InvalidOperationException"/>: вызывающая сторона может обработать
/// ошибку границы как обычную ошибку операции, не зная деталей interop.
/// </remarks>
public class AzurPilotNativeBoundaryException : InvalidOperationException
{
    /// <summary>Создаёт исключение с диагностическим сообщением.</summary>
    /// <param name="message">Диагностическое сообщение на русском языке.</param>
    public AzurPilotNativeBoundaryException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с диагностическим сообщением и исходной ошибкой.</summary>
    /// <param name="message">Диагностическое сообщение на русском языке.</param>
    /// <param name="innerException">Исходная ошибка interop.</param>
    public AzurPilotNativeBoundaryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Native библиотека не загружена: файл не найден, не является корректной библиотекой x64 или не
/// найдена одна из её runtime-зависимостей.
/// </summary>
public sealed class NativeBoundaryUnavailableException : AzurPilotNativeBoundaryException
{
    /// <summary>Создаёт исключение с диагностическим сообщением.</summary>
    /// <param name="message">Диагностическое сообщение на русском языке.</param>
    public NativeBoundaryUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с диагностическим сообщением и исходной ошибкой.</summary>
    /// <param name="message">Диагностическое сообщение на русском языке.</param>
    /// <param name="innerException">Исходная ошибка загрузки библиотеки.</param>
    public NativeBoundaryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Native библиотека загружена, но её версия ABI не совпадает с ожидаемой.
/// </summary>
public sealed class NativeAbiMismatchException : AzurPilotNativeBoundaryException
{
    /// <summary>Создаёт исключение с диагностическим сообщением.</summary>
    /// <param name="message">Диагностическое сообщение на русском языке.</param>
    public NativeAbiMismatchException(string message)
        : base(message)
    {
    }
}
