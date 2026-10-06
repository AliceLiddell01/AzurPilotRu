using AzurPilot.Core.Failures;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Узкая граница запуска внешнего процесса: единственная точка, где adapter порождает процесс.
/// </summary>
/// <remarks>
/// <para>
/// Граница существует, чтобы production-логика клиента control surface проверялась на подменяемой
/// внешней границе, без реально установленной MuMu и без реальных процессов.
/// </para>
/// <para>
/// Успешный результат означает, что процесс был запущен и завершился сам: код выхода остаётся
/// данными. Ожидаемые отказы сообщаются значением и не подменяются общим кодом: недостижимость
/// запуска и усечённый захваченный вывод — как
/// <see cref="ApplicationFailure.MuMuControlSurfaceUnsupported"/>, превышение дедлайна команды — как
/// <see cref="ApplicationFailure.MuMuLifecycleTimeout"/> с <c>IsRetryable = true</c>, отмена — как
/// <see cref="ApplicationFailure.OperationCancelled"/>. Коды берутся из констант
/// <see cref="ApplicationFailure"/>: строковые литералы кодов здесь не дублируются.
/// </para>
/// <para>
/// <see cref="ApplicationFailure.InternalError"/> остаётся только для непредвиденной ошибки платформы
/// (чтение реестра, обращение к файловой системе) — ровно так же, как это делает
/// <see cref="MuMuPlatformFailureMapper"/>. Ожидаемый MuMu-отказ не сообщается как
/// <see cref="ApplicationFailure.InternalError"/>: иначе вызывающая сторона не отличила бы ожидаемое
/// условие MuMu от неожиданного нарушения контракта.
/// </para>
/// </remarks>
public interface IMuMuProcessRunner
{
    /// <summary>Запускает процесс по точному пути и списку аргументов и дожидается его завершения.</summary>
    /// <param name="request">Описание запуска: путь, аргументы, рабочий каталог и дедлайн.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Результат завершившегося процесса либо application-level отказ.</returns>
    Task<ApplicationResult<MuMuProcessOutcome>> RunAsync(
        MuMuProcessRequest request,
        CancellationToken cancellationToken);
}
