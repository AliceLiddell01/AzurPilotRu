using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Ожидаемые отказы, которые синтезирует Windows-адаптер Android.
/// </summary>
/// <remarks>
/// <para>
/// Единственный владелец того, какие ожидаемые отказы синтезирует платформенная сторона: коды берутся из
/// констант <see cref="ApplicationFailure"/>, строковые литералы кодов здесь не дублируются, и ожидаемый
/// Android-отказ никогда не подменяется <see cref="ApplicationFailure.InternalError"/>.
/// </para>
/// <para>
/// Отказы, возвращённые другими границами, здесь не пересоздаются: adapter либо пробрасывает их без
/// изменений, либо, если отказ относится к его собственной операции (разрешение точного endpoint-а),
/// сообщает точный Android-код с machine-stable причиной.
/// </para>
/// <para>
/// Details содержат только bounded machine-stable факты: причину, identity экземпляра, точный endpoint,
/// идентификатор пакета, число совпавших компонентов, длительность и счётчики захваченного вывода.
/// Абсолютные machine-пути (в том числе путь bundled ADB), полный stdout/stderr и полный список
/// процессов устройства в details не попадают — от пути сообщается только имя файла.
/// </para>
/// </remarks>
internal static class AndroidHostFailures
{
    /// <summary>Имя фазы операции, в которой адаптер не смог адресовать запуск игры.</summary>
    /// <remarks>
    /// Значение совпадает с именем фазы mutation, которым пользуется orchestration Core: фаза — часть
    /// machine-stable контракта details, а не свободный текст.
    /// </remarks>
    internal const string MutationPhase = "mutation";

    /// <summary>Причина отказа: процесс bundled ADB не удалось запустить.</summary>
    internal const string ProcessStartFailedReason = "process_start_failed";

    /// <summary>Причина отказа: команда ADB не завершилась в пределах дедлайна.</summary>
    internal const string CommandTimeoutReason = "command_timeout";

    /// <summary>Причина отказа: форма ответа команды ADB не распознана.</summary>
    internal const string ResponseUnrecognizedReason = "response_unrecognized";

    /// <summary>Причина отказа: host ADB endpoint не сообщён сведениями установки.</summary>
    internal const string HostMissingReason = "host_missing";

    /// <summary>Причина отказа: host ADB endpoint сообщён в непригодной форме.</summary>
    internal const string HostInvalidReason = "host_invalid";

    /// <summary>Причина отказа: порт ADB endpoint не сообщён сведениями установки.</summary>
    internal const string PortMissingReason = "port_missing";

    /// <summary>Причина отказа: порт ADB endpoint вне допустимого диапазона.</summary>
    internal const string PortOutOfRangeReason = "port_out_of_range";

    private const string EndpointDetailKey = "endpoint";

    private const string InstanceIdDetailKey = "instance_id";

    private const string PackageDetailKey = "package";

    private const string StateDetailKey = "state";

    private const string ReasonDetailKey = "reason";

    private const string PhaseDetailKey = "phase";

    private const string EvidenceDetailKey = "evidence";

    private const string MatchingComponentCountDetailKey = "matching_components";

    private const string ElapsedMillisecondsDetailKey = "elapsed_ms";

    private const string ExecutableNameDetailKey = "executable_name";

    private const string ExceptionTypeDetailKey = "exception_type";

    private const string StandardOutputCharactersDetailKey = "stdout_characters";

    private const string StandardErrorCharactersDetailKey = "stderr_characters";

    private const string MissingStateName = "missing";

    private const string QueryFailedStateName = "query_failed";

    private const string AmbiguousStateName = "ambiguous";

    /// <summary>Machine-stable состояние разрешения launcher-а: компонент отсутствует.</summary>
    internal const string LauncherMissingStateName = MissingStateName;

    /// <summary>Machine-stable состояние разрешения launcher-а: разрешение не доказано.</summary>
    internal const string LauncherQueryFailedStateName = QueryFailedStateName;

    /// <summary>Создаёт отказ «bundled ADB установки недоступен».</summary>
    /// <param name="reason">Machine-stable причина, например <c>process_start_failed</c>.</param>
    /// <param name="evidence">Bounded evidence обнаружения; путь executable в него не входит.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AndroidAdbUnavailable"/>.</returns>
    internal static ApplicationFailure AdbUnavailable(string reason, string evidence)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [ReasonDetailKey] = reason,
            [EvidenceDetailKey] = BoundedDiagnosticText.Bounded(evidence),
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AndroidAdbUnavailable,
            Message = "Исполняемый файл bundled ADB обнаруженной установки MuMuPlayer недоступен: "
                + "обнаружение не подтвердило пригодную точку входа.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «процесс bundled ADB не удалось запустить».</summary>
    /// <remarks>
    /// В details попадает только имя исполняемого файла и CLR-имя типа исходного исключения: абсолютный
    /// путь к установке в диагностику не переносится.
    /// </remarks>
    /// <param name="executablePath">Путь к исполняемому файлу ADB.</param>
    /// <param name="exception">Исходное исключение запуска или <see langword="null"/>.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AndroidAdbUnavailable"/>.</returns>
    internal static ApplicationFailure AdbStartFailed(string executablePath, Exception? exception)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [ReasonDetailKey] = ProcessStartFailedReason,
            [ExecutableNameDetailKey] = Path.GetFileName(executablePath),
        };

        if (exception is not null)
        {
            details[ExceptionTypeDetailKey] = exception.GetType().Name;
        }

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AndroidAdbUnavailable,
            Message = "Процесс bundled ADB обнаруженной установки MuMuPlayer не удалось запустить: "
                + "исполняемый файл недоступен.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «точный ADB endpoint выбранного экземпляра не разрешён».</summary>
    /// <param name="instance">Identity экземпляра, для которого запрашивался endpoint.</param>
    /// <param name="reason">Machine-stable причина, например <c>host_missing</c>.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AndroidEndpointUnavailable"/>.</returns>
    internal static ApplicationFailure EndpointUnavailable(MuMuInstanceId instance, string reason)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [InstanceIdDetailKey] = instance.ToString(),
            [ReasonDetailKey] = reason,
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AndroidEndpointUnavailable,
            Message = $"ADB endpoint Android-экземпляра «{instance}» не разрешён: сведения установки не "
                + "содержат точного адреса, а значение по умолчанию не подставляется.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «команда ADB не завершилась в пределах дедлайна».</summary>
    /// <remarks>
    /// Отказ адресуется точному target-у: команда не несёт сведений о том, какой endpoint был запрошен,
    /// поэтому в details попадают только имя исполняемого файла, длительность ожидания и счётчики
    /// захваченного вывода — без абсолютного пути и без полного вывода.
    /// </remarks>
    /// <param name="executablePath">Путь к исполняемому файлу ADB.</param>
    /// <param name="elapsed">Длительность ожидания до завершения процесса.</param>
    /// <param name="standardOutputCharacters">Число захваченных символов stdout.</param>
    /// <param name="standardErrorCharacters">Число захваченных символов stderr.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AndroidEndpointUnavailable"/>.</returns>
    internal static ApplicationFailure CommandTimedOut(
        string executablePath,
        TimeSpan elapsed,
        int standardOutputCharacters,
        int standardErrorCharacters)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [ReasonDetailKey] = CommandTimeoutReason,
            [ExecutableNameDetailKey] = Path.GetFileName(executablePath),
            [ElapsedMillisecondsDetailKey] = Count((long)elapsed.TotalMilliseconds),
            [StandardOutputCharactersDetailKey] = Count(standardOutputCharacters),
            [StandardErrorCharactersDetailKey] = Count(standardErrorCharacters),
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AndroidEndpointUnavailable,
            Message = "Команда ADB не завершилась в пределах дедлайна: точный target не ответил, а "
                + "повторное подключение и завершение сервера ADB не выполняются.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ отмены операции.</summary>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.OperationCancelled"/>.</returns>
    internal static ApplicationFailure Cancelled()
        => new()
        {
            Code = ApplicationFailure.OperationCancelled,
            Message = "Операция ADB отменена запросом отмены.",
        };

    /// <summary>Создаёт отказ «launcher-компонент пакета не разрешён».</summary>
    /// <param name="endpoint">Точный endpoint, у которого разрешался launcher.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <param name="state">Machine-stable состояние разрешения: <c>missing</c> или <c>query_failed</c>.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AzurLaneLauncherUnresolved"/>.</returns>
    internal static ApplicationFailure LauncherUnresolved(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        string state)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [PackageDetailKey] = package.ToString(),
            [StateDetailKey] = state,
            [PhaseDetailKey] = MutationPhase,
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AzurLaneLauncherUnresolved,
            Message = $"У пакета {package} на endpoint-е {endpoint} нет разрешимого launcher-компонента: "
                + "запуск игры не может быть адресован.",
            Details = details,
        };
    }

    /// <summary>Создаёт отказ «launcher-компонент пакета неоднозначен».</summary>
    /// <param name="endpoint">Точный endpoint, у которого разрешался launcher.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <param name="matchingComponentCount">Число компонентов, совпавших с запросом.</param>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.AzurLaneLauncherAmbiguous"/>.</returns>
    internal static ApplicationFailure LauncherAmbiguous(
        AndroidEndpoint endpoint,
        AndroidPackageId package,
        int matchingComponentCount)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [EndpointDetailKey] = endpoint.ToString(),
            [PackageDetailKey] = package.ToString(),
            [StateDetailKey] = AmbiguousStateName,
            [MatchingComponentCountDetailKey] = Count(matchingComponentCount),
            [PhaseDetailKey] = MutationPhase,
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AzurLaneLauncherAmbiguous,
            Message = $"Launcher-компонент пакета {package} на endpoint-е {endpoint} не разрешён: "
                + $"подходящих компонентов {Count(matchingComponentCount)}, а запуск адресуется только "
                + "одному.",
            Details = details,
        };
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);
}
