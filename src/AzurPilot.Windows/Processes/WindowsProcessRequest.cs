namespace AzurPilot.Windows.Processes;

/// <summary>Форма захвата stdout общей границей запуска.</summary>
public enum WindowsProcessStandardOutputMode
{
    /// <summary>Ограниченный диагностический текст.</summary>
    Text,

    /// <summary>Сырые байты без декодирования и изменения окончаний строк.</summary>
    Binary,
}

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

    /// <summary>Форма stdout; существующие вызовы используют текст.</summary>
    public WindowsProcessStandardOutputMode StandardOutputMode { get; init; }

    /// <summary>Положительный бюджет сохранённых байтов для бинарного stdout.</summary>
    public int MaximumStandardOutputBytes { get; init; }
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
    /// <summary>Фактическая форма захваченного stdout.</summary>
    public WindowsProcessStandardOutputMode Mode { get; init; }

    /// <summary>Ограниченный бинарный stdout; для текстового режима пуст.</summary>
    public byte[] StandardOutputBytes { get; init; } = [];

    /// <summary>Число наблюдавшихся байтов, включая отброшенные при превышении бюджета.</summary>
    public long StandardOutputBytesRead { get; init; }

    /// <summary>Бинарный поток прочитан до EOF без ошибки или отмены.</summary>
    public bool StandardOutputByteStreamEnded { get; init; }

    /// <summary>Бинарный stdout превысил бюджет сохранённых байтов.</summary>
    public bool StandardOutputBytesTruncated { get; init; }

    /// <summary>Бинарный stdout целиком прочитан и помещается в бюджет; валидность PNG не утверждается.</summary>
    public bool StandardOutputBinaryComplete
        => Mode == WindowsProcessStandardOutputMode.Binary
            && StandardOutputByteStreamEnded
            && !StandardOutputBytesTruncated;

    /// <summary>Код выхода процесса.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Ограниченный захваченный stdout процесса; для бинарного режима пуст.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Ограниченный захваченный stderr процесса.</summary>
    public required string StandardError { get; init; }

    /// <summary>Длительность работы процесса.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Признак усечения текстового stdout; бинарный бюджет имеет отдельный признак.</summary>
    public required bool StandardOutputTruncated { get; init; }

    /// <summary>Признак того, что stderr был усечён предельной длиной захвата.</summary>
    public required bool StandardErrorTruncated { get; init; }
}
