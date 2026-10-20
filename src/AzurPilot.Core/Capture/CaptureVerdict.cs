namespace AzurPilot.Core.Capture;

/// <summary>Итог проверки метода или измерения; внешние имена принадлежат <see cref="CaptureVerdictNames"/>.</summary>
public enum CaptureVerdict
{
    /// <summary>Проверка выполнена и доказала результат.</summary>
    Pass,

    /// <summary>Метод поддерживается, но попытка завершилась отказом.</summary>
    Failed,

    /// <summary>Метод неприменим к этому target по наблюдённому основанию (не отказ).</summary>
    Unsupported,

    /// <summary>Предусловие измерения не доказано; измерение не выполнялось.</summary>
    Blocked,

    /// <summary>Измерение прервано или ограничено бюджетом; данные неполны.</summary>
    Partial,

    /// <summary>Измерение не запускалось.</summary>
    NotTested,

    /// <summary>Данных недостаточно для вывода.</summary>
    Insufficient,

    /// <summary>Измерение выполнено, но сцена не доказана машинно.</summary>
    UnverifiedScene,
}

/// <summary>Единственный владелец внешних имён <see cref="CaptureVerdict"/>.</summary>
public static class CaptureVerdictNames
{
    /// <summary>Внешнее имя итога.</summary>
    public static string ToName(CaptureVerdict verdict) => verdict switch
    {
        CaptureVerdict.Pass => "PASS",
        CaptureVerdict.Failed => "FAILED",
        CaptureVerdict.Unsupported => "UNSUPPORTED",
        CaptureVerdict.Blocked => "BLOCKED",
        CaptureVerdict.Partial => "PARTIAL",
        CaptureVerdict.NotTested => "NOT_TESTED",
        CaptureVerdict.Insufficient => "INSUFFICIENT",
        CaptureVerdict.UnverifiedScene => "UNVERIFIED_SCENE",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Неизвестный итог."),
    };
}
