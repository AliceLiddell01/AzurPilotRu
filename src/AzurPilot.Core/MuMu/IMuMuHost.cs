using AzurPilot.Core.Failures;

namespace AzurPilot.Core.MuMu;

/// <summary>
/// Host-side поверхность MuMu: обнаружение установки, перечисление экземпляров, наблюдение состояния и
/// выполнение mutation.
/// </summary>
/// <remarks>
/// <para>
/// Контракт платформенно-независим: он не знает ни о Win32, ни о реестре, ни о процессах, ни о
/// конкретных путях установки. Реализация принадлежит платформенной boundary <c>AzurPilot.Windows</c>.
/// </para>
/// <para>
/// Ожидаемый отказ возвращается значением через <see cref="ApplicationResult{T}"/>; исключение означает
/// ошибку программирования вызывающей стороны или нарушение контракта реализацией. Ожидаемые
/// MuMu-отказы, возвращённые host-ом, orchestration пробрасывает без изменений: это уже точные коды, и
/// подменять их общим отказом нельзя.
/// </para>
/// <para>
/// Наблюдение состояния авторитетно только для запрошенного экземпляра: реализация обязана отвечать про
/// конкретный vmindex, а не про «какой-то запущенный процесс MuMu».
/// </para>
/// <para>
/// Методы синхронные: они сообщают результат одного host-side действия, а bounded polling, deadline и
/// process-local сериализацию выполняет orchestration.
/// </para>
/// <para>
/// Токен отмены несёт только mutation: команды чтения ограничены собственным дедлайном границы, а отмена
/// проверяется orchestration между шагами операции.
/// </para>
/// </remarks>
public interface IMuMuHost
{
    /// <summary>Обнаруживает установку MuMuPlayer.</summary>
    /// <returns>
    /// Успешный результат с найденной установкой либо ожидаемый отказ (например,
    /// <see cref="ApplicationFailure.MuMuInstallationNotFound"/>).
    /// </returns>
    ApplicationResult<MuMuInstallation> DiscoverInstallation();

    /// <summary>Перечисляет Android-экземпляры обнаруженной установки.</summary>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <returns>
    /// Успешный результат со всеми экземплярами установки (возможно пустой список) либо ожидаемый отказ.
    /// </returns>
    ApplicationResult<IReadOnlyList<MuMuInstance>> EnumerateInstances(MuMuInstallation installation);

    /// <summary>Наблюдает host-side состояние конкретного Android-экземпляра.</summary>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Identity экземпляра, состояние которого запрашивается.</param>
    /// <returns>Успешное наблюдение состояния либо ожидаемый отказ.</returns>
    ApplicationResult<MuMuInstanceState> ObserveInstanceState(MuMuInstallation installation, MuMuInstanceId instance);

    /// <summary>Выполняет однократную mutation над конкретным экземпляром.</summary>
    /// <remarks>
    /// <para>
    /// Метод выполняет ровно одну mutation: повторные вызовы, ожидание postcondition и deadline
    /// принадлежат orchestration. Реализация обязана ограничить собственный вывод и не останавливать
    /// другие экземпляры MuMu.
    /// </para>
    /// <para>
    /// Код выхода control utility не является доказательством успеха: реализация сообщает его как
    /// факт, а переход доказывает orchestration наблюдением состояния.
    /// </para>
    /// </remarks>
    /// <param name="installation">Обнаруженная установка MuMuPlayer.</param>
    /// <param name="instance">Identity экземпляра, над которым выполняется mutation.</param>
    /// <param name="mutation">Запрошенная mutation.</param>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Успешный результат с кодом выхода control utility либо ожидаемый отказ.</returns>
    ApplicationResult<MuMuLifecycleCommandOutcome> RequestMutation(
        MuMuInstallation installation,
        MuMuInstanceId instance,
        MuMuLifecycleMutation mutation,
        CancellationToken cancellationToken);
}
