using System.Collections.Frozen;
using System.ComponentModel;
using System.Globalization;
using AzurPilot.Core.Failures;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Ключи bounded details для отказов Windows-адаптеров MuMu.
/// </summary>
/// <remarks>
/// Единственный владелец имён ключей: adapter и тесты не дублируют строковые литералы. В details
/// попадают только ограниченные диагностические факты — длина захваченного вывода, код выхода, код
/// ошибки провайдера и тип исходного исключения. Полный stdout/stderr, полный дамп исключения и полное
/// содержимое ответа провайдера в details не попадают.
/// </remarks>
public static class MuMuFailureDetailKeys
{
    /// <summary>Причина отказа адаптера из набора <see cref="MuMuFailureReasons"/>.</summary>
    public const string Reason = "mumu_reason";

    /// <summary>CLR-имя типа исходного исключения платформы.</summary>
    public const string ExceptionType = "exception_type";

    /// <summary>Код ошибки Win32/ОС, если он доступен.</summary>
    public const string NativeErrorCode = "native_error_code";

    /// <summary>Код выхода процесса control surface.</summary>
    public const string ExitCode = "process_exit_code";

    /// <summary>Длительность операции в миллисекундах.</summary>
    public const string DurationMilliseconds = "duration_ms";

    /// <summary>Имя исполняемого файла без каталога.</summary>
    public const string ExecutableName = "executable_name";

    /// <summary>Число захваченных символов stdout.</summary>
    public const string StandardOutputCharacters = "stdout_characters";

    /// <summary>Число захваченных символов stderr.</summary>
    public const string StandardErrorCharacters = "stderr_characters";

    /// <summary>Код ошибки, который вернул провайдер в теле ответа.</summary>
    public const string ProviderErrorCode = "mumu_error_code";

    /// <summary>Каноническая identity экземпляра, к которому относилась операция.</summary>
    public const string InstanceId = "instance_id";

    /// <summary>Сколько установок MuMu обнаружено.</summary>
    public const string InstallationsFound = "installations_found";

    /// <summary>Сколько кандидатов установки не удалось разрешить.</summary>
    public const string RejectedCandidates = "rejected_candidates";

    /// <summary>Причины отклонения кандидатов установки из набора <see cref="MuMuInstallationRejectionReasons"/>.</summary>
    public const string RejectionReasons = "rejection_reasons";
}

/// <summary>
/// Причины отказов Windows-адаптеров MuMu.
/// </summary>
/// <remarks>
/// Причина — диагностический факт внутри application-level отказа, а не второй набор кодов отказа.
/// Набор закрыт: причина появляется вместе с местом, которое её порождает.
/// </remarks>
public static class MuMuFailureReasons
{
    /// <summary>Процесс control surface не удалось запустить.</summary>
    public const string ProcessStartFailed = "process_start_failed";

    /// <summary>Процесс control surface не завершился в пределах дедлайна.</summary>
    public const string ProcessTimeout = "process_timeout";

    /// <summary>Захваченный вывод процесса усечён, поэтому ответ провайдера не может быть разобран.</summary>
    public const string OutputTruncated = "output_truncated";

    /// <summary>Форма ответа провайдера не распознана.</summary>
    public const string ResponseUnrecognized = "response_unrecognized";

    /// <summary>Чтение uninstall-записей реестра завершилось ошибкой платформы.</summary>
    public const string RegistryAccessFailed = "registry_access_failed";

    /// <summary>Обращение к файловой системе завершилось ошибкой платформы.</summary>
    public const string FileSystemAccessFailed = "file_system_access_failed";

    /// <summary>Обнаружено несколько установок MuMu: однозначная установка не определена.</summary>
    public const string InstallationAmbiguous = "installation_ambiguous";

    /// <summary>Установка MuMu есть, но поддерживаемой точки входа control surface в ней нет.</summary>
    public const string ControlSurfaceMissing = "control_surface_missing";

    /// <summary>Провайдер отказал с кодом, смысл которого не доказан.</summary>
    public const string ProviderErrorUnrecognized = "provider_error_unrecognized";
}

/// <summary>
/// Проекция исключений Windows-платформы и отказов границы процесса в application-level отказ.
/// </summary>
/// <remarks>
/// <para>
/// Маппер — чистая функция без состояния: он не бросает исключений, ничего не логирует и не решает, что
/// делать с отказом. Его задача — не дать исключениям process/registry/filesystem протечь наружу как
/// machine contract.
/// </para>
/// <para>
/// Коды отказа берутся из <see cref="ApplicationFailure"/>: строковые литералы кодов здесь не
/// дублируются, и второй каталог кодов не заводится. Маппер не добавляет новых кодов.
/// </para>
/// <para>
/// Отказ провайдера MuMu (например, «экземпляр не найден») отказом операции не является и в этот
/// маппер не попадает — он возвращается значением рядом с результатом операции. Недостижимая или
/// нераспознанная control surface проецируется в
/// <see cref="ApplicationFailure.MuMuControlSurfaceUnsupported"/>, недостигнутый дедлайн команды — в
/// <see cref="ApplicationFailure.MuMuLifecycleTimeout"/>, непредвиденная ошибка платформы — в
/// <see cref="ApplicationFailure.InternalError"/>.
/// </para>
/// <para>
/// В details попадают только bounded факты: причина, тип исключения, код ошибки ОС, код выхода процесса,
/// длительность, имя исполняемого файла и длины захваченного вывода. Пути установки и полный вывод
/// процесса в details не попадают.
/// </para>
/// </remarks>
public static class MuMuPlatformFailureMapper
{
    /// <summary>Проецирует исключение платформы в application-level отказ с указанной причиной.</summary>
    /// <param name="exception">Исключение process/registry/filesystem границы.</param>
    /// <param name="reason">Причина из набора <see cref="MuMuFailureReasons"/>.</param>
    /// <returns>Отказ с одним из стабильных кодов <see cref="ApplicationFailure"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> равен <see langword="null"/>.</exception>
    public static ApplicationFailure ForPlatformException(Exception exception, string reason)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // Отмена — ожидаемый исход операции, а не ошибка платформы.
        if (exception is OperationCanceledException)
        {
            return ForCancellation();
        }

        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [MuMuFailureDetailKeys.Reason] = reason,
            [MuMuFailureDetailKeys.ExceptionType] = exception.GetType().Name,
        };

        if (exception is Win32Exception { NativeErrorCode: not 0 } win32Exception)
        {
            details[MuMuFailureDetailKeys.NativeErrorCode] =
                win32Exception.NativeErrorCode.ToString(CultureInfo.InvariantCulture);
        }

        return new ApplicationFailure
        {
            Code = ApplicationFailure.InternalError,
            Message = $"Ошибка платформенной границы MuMu ({reason}): {exception.GetType().Name}.",
            IsRetryable = false,
            Details = details.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }

    /// <summary>Создаёт отказ об отменённой операции.</summary>
    /// <returns>Отказ с кодом <see cref="ApplicationFailure.OperationCancelled"/>.</returns>
    public static ApplicationFailure ForCancellation()
        => new()
        {
            Code = ApplicationFailure.OperationCancelled,
            Message = "Операция над MuMu отменена запросом отмены.",
            IsRetryable = false,
        };

    /// <summary>Создаёт отказ о незапустившемся процессе control surface.</summary>
    /// <param name="executablePath">Путь к исполняемому файлу, который не удалось запустить.</param>
    /// <param name="exception">Исходное исключение запуска или <see langword="null"/>.</param>
    /// <returns>Отказ с причиной <see cref="MuMuFailureReasons.ProcessStartFailed"/>.</returns>
    public static ApplicationFailure ForProcessStartFailure(string executablePath, Exception? exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [MuMuFailureDetailKeys.Reason] = MuMuFailureReasons.ProcessStartFailed,
            [MuMuFailureDetailKeys.ExecutableName] = Path.GetFileName(executablePath),
        };

        if (exception is not null)
        {
            details[MuMuFailureDetailKeys.ExceptionType] = exception.GetType().Name;

            if (exception is Win32Exception { NativeErrorCode: not 0 } win32Exception)
            {
                details[MuMuFailureDetailKeys.NativeErrorCode] =
                    win32Exception.NativeErrorCode.ToString(CultureInfo.InvariantCulture);
            }
        }

        return new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuControlSurfaceUnsupported,
            Message = "Не удалось запустить процесс control surface MuMu.",
            IsRetryable = false,
            Details = details.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }

    /// <summary>Создаёт отказ о превышении дедлайна процессом control surface.</summary>
    /// <param name="executablePath">Путь к исполняемому файлу.</param>
    /// <param name="duration">Длительность ожидания до завершения процесса.</param>
    /// <param name="standardOutputCharacters">Число захваченных символов stdout.</param>
    /// <param name="standardErrorCharacters">Число захваченных символов stderr.</param>
    /// <returns>Отказ с причиной <see cref="MuMuFailureReasons.ProcessTimeout"/>.</returns>
    public static ApplicationFailure ForProcessTimeout(
        string executablePath,
        TimeSpan duration,
        int standardOutputCharacters,
        int standardErrorCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [MuMuFailureDetailKeys.Reason] = MuMuFailureReasons.ProcessTimeout,
            [MuMuFailureDetailKeys.ExecutableName] = Path.GetFileName(executablePath),
            [MuMuFailureDetailKeys.DurationMilliseconds] =
                ((long)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            [MuMuFailureDetailKeys.StandardOutputCharacters] =
                standardOutputCharacters.ToString(CultureInfo.InvariantCulture),
            [MuMuFailureDetailKeys.StandardErrorCharacters] =
                standardErrorCharacters.ToString(CultureInfo.InvariantCulture),
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuLifecycleTimeout,
            Message = "Процесс control surface MuMu не завершился в пределах дедлайна.",
            IsRetryable = true,
            Details = details.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }

    /// <summary>Создаёт отказ об усечённом выводе процесса, из которого нельзя получить ответ провайдера.</summary>
    /// <param name="executablePath">Путь к исполняемому файлу.</param>
    /// <param name="standardOutputCharacters">Число захваченных символов stdout.</param>
    /// <param name="standardErrorCharacters">Число захваченных символов stderr.</param>
    /// <returns>Отказ с причиной <see cref="MuMuFailureReasons.OutputTruncated"/>.</returns>
    public static ApplicationFailure ForOutputTruncation(
        string executablePath,
        int standardOutputCharacters,
        int standardErrorCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [MuMuFailureDetailKeys.Reason] = MuMuFailureReasons.OutputTruncated,
            [MuMuFailureDetailKeys.ExecutableName] = Path.GetFileName(executablePath),
            [MuMuFailureDetailKeys.StandardOutputCharacters] =
                standardOutputCharacters.ToString(CultureInfo.InvariantCulture),
            [MuMuFailureDetailKeys.StandardErrorCharacters] =
                standardErrorCharacters.ToString(CultureInfo.InvariantCulture),
        };

        return new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuControlSurfaceUnsupported,
            Message = "Вывод control surface MuMu усечён предельной длиной захвата, ответ не разобран.",
            IsRetryable = false,
            Details = details.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }

    /// <summary>Создаёт отказ о нераспознанной форме ответа control surface.</summary>
    /// <param name="operation">Описание операции, ответ которой не распознан.</param>
    /// <param name="exitCode">Код выхода процесса control surface.</param>
    /// <param name="providerErrorCode">Код ошибки провайдера из тела ответа или <see langword="null"/>.</param>
    /// <returns>Отказ с причиной <see cref="MuMuFailureReasons.ResponseUnrecognized"/>.</returns>
    public static ApplicationFailure ForUnrecognizedResponse(
        string operation,
        int exitCode,
        int? providerErrorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            [MuMuFailureDetailKeys.Reason] = MuMuFailureReasons.ResponseUnrecognized,
            [MuMuFailureDetailKeys.ExitCode] = exitCode.ToString(CultureInfo.InvariantCulture),
        };

        if (providerErrorCode is int code)
        {
            details[MuMuFailureDetailKeys.ProviderErrorCode] =
                code.ToString(CultureInfo.InvariantCulture);
        }

        return new ApplicationFailure
        {
            Code = ApplicationFailure.MuMuControlSurfaceUnsupported,
            Message = $"Форма ответа control surface MuMu не распознана для операции \"{operation}\".",
            IsRetryable = false,
            Details = details.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }
}
