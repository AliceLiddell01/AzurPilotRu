using AzurPilot.Core.Failures;

namespace AzurPilot.Windows.Processes;

/// <summary>
/// Узкая граница запуска внешнего процесса: единственная точка, где Windows-возможность порождает
/// процесс.
/// </summary>
/// <remarks>
/// <para>
/// Граница существует, чтобы production-логика возможности проверялась на подменяемой внешней границе,
/// без реально установленного внешнего продукта и без реальных процессов. Одна граница обслуживает все
/// возможности приложения: второго runner-а, второй формы request/outcome и второй обёртки над
/// <c>cmd</c>, PowerShell или иной оболочкой не заводится.
/// </para>
/// <para>
/// Успешный результат означает, что процесс был запущен и завершился сам: код выхода остаётся данными.
/// Ожидаемые отказы сообщаются значением и не подменяются общим кодом, а их смысл принадлежит владельцу
/// возможности: он передаёт границе свою <see cref="IProcessFailureProjection"/>, поэтому одна механика
/// запуска сообщает точные коды каждого владельца.
/// </para>
/// </remarks>
public interface IWindowsProcessRunner
{
    /// <summary>Запускает процесс по точному пути и списку аргументов и дожидается его завершения.</summary>
    /// <param name="request">Описание запуска: путь, аргументы, рабочий каталог и дедлайн.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Результат завершившегося процесса либо application-level отказ.</returns>
    Task<ApplicationResult<WindowsProcessOutcome>> RunAsync(
        WindowsProcessRequest request,
        CancellationToken cancellationToken);
}
