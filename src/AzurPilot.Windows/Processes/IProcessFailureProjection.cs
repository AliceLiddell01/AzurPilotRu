using AzurPilot.Core.Failures;

namespace AzurPilot.Windows.Processes;

/// <summary>
/// Проекция отказов границы запуска процесса в application-level отказ.
/// </summary>
/// <remarks>
/// <para>
/// Граница запуска процесса — общая для Windows-возможностей приложения и не знает, какая именно
/// возможность её использует. Поэтому смысл отказа принадлежит владельцу возможности: он передаёт
/// границе свою проекцию, и одна и та же механика запуска сообщает разные стабильные коды, не заводя
/// второй границы запуска процесса.
/// </para>
/// <para>
/// Реализация проекции — чистая функция без состояния: она не бросает исключений, ничего не логирует и не
/// решает, что делать с отказом. Её задача — не дать исключениям платформенной границы протечь наружу как
/// machine contract.
/// </para>
/// <para>
/// Проекция — обязательная зависимость границы: значения по умолчанию у неё нет, потому что скрытый
/// «общий» код отказа стёр бы различие между ожидаемым условием возможности и непредвиденной ошибкой
/// платформы.
/// </para>
/// </remarks>
public interface IProcessFailureProjection
{
    /// <summary>Проецирует неудачный запуск процесса в application-level отказ.</summary>
    /// <param name="executablePath">Путь к исполняемому файлу, который не удалось запустить.</param>
    /// <param name="exception">Исходное исключение запуска или <see langword="null"/>.</param>
    /// <returns>Отказ с кодом, принадлежащим владельцу возможности.</returns>
    ApplicationFailure StartFailed(string executablePath, Exception? exception);

    /// <summary>Проецирует превышение дедлайна процессом в application-level отказ.</summary>
    /// <param name="executablePath">Путь к исполняемому файлу.</param>
    /// <param name="elapsed">Длительность ожидания до завершения процесса.</param>
    /// <param name="standardOutputCharacters">Число захваченных символов stdout.</param>
    /// <param name="standardErrorCharacters">Число захваченных символов stderr.</param>
    /// <returns>Отказ с кодом, принадлежащим владельцу возможности.</returns>
    ApplicationFailure TimedOut(
        string executablePath,
        TimeSpan elapsed,
        int standardOutputCharacters,
        int standardErrorCharacters);

    /// <summary>Проецирует отмену операции в application-level отказ.</summary>
    /// <returns>Отказ с кодом, принадлежащим владельцу возможности.</returns>
    ApplicationFailure Cancelled();
}
