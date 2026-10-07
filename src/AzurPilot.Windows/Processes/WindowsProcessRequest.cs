namespace AzurPilot.Windows.Processes;

/// <summary>
/// Запрос на запуск внешнего процесса Windows-границей запуска.
/// </summary>
/// <remarks>
/// <para>
/// Запуск описывается точным путём к исполняемому файлу и списком аргументов: командной строки,
/// собираемой из строки, не существует, поэтому значение аргумента не может быть переинтерпретировано
/// как ещё один аргумент, перенаправление или разделитель команд.
/// </para>
/// <para>
/// <see cref="WorkingDirectory"/> необязателен: проверенные формы внешних утилит работают без него и
/// наследуют рабочий каталог процесса приложения. Поле существует для случая, когда каталог
/// действительно требуется, и передаётся процессу как есть.
/// </para>
/// </remarks>
public sealed record WindowsProcessRequest
{
    /// <summary>Абсолютный путь к исполняемому файлу.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Аргументы процесса в порядке передачи; каждый аргумент — отдельный элемент списка.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Рабочий каталог процесса или <see langword="null"/>, если он не требуется.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Предельное время работы процесса.</summary>
    public required TimeSpan Timeout { get; init; }
}

/// <summary>
/// Результат завершившегося внешнего процесса.
/// </summary>
/// <remarks>
/// <see cref="ExitCode"/> — evidence, а не признак успеха операции: успешность определяет потребитель по
/// разобранному ответу. <see cref="StandardOutput"/> и <see cref="StandardError"/> ограничены по длине;
/// признак усечения сообщается явно и не скрывается.
/// </remarks>
public sealed record WindowsProcessOutcome
{
    /// <summary>Код выхода процесса.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Ограниченный захваченный stdout процесса.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Ограниченный захваченный stderr процесса.</summary>
    public required string StandardError { get; init; }

    /// <summary>Длительность работы процесса.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Признак того, что stdout был усечён предельной длиной захвата.</summary>
    public required bool StandardOutputTruncated { get; init; }

    /// <summary>Признак того, что stderr был усечён предельной длиной захвата.</summary>
    public required bool StandardErrorTruncated { get; init; }
}
