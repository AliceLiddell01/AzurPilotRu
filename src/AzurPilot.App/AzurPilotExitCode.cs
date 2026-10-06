using AzurPilot.Core.Failures;

namespace AzurPilot.App;

/// <summary>Коды выхода процесса application host.</summary>
/// <remarks>
/// <para>
/// Код выхода — часть контракта startup: ожидаемый отказ завершает запуск явным предсказуемым
/// ненулевым кодом, поэтому «нездоровый» запуск нельзя спутать с успешным.
/// </para>
/// <para>
/// Соответствие «application-level код отказа → код выхода» задано ровно в одном месте — здесь;
/// второго каталога application-кодов не появляется, потому что сами коды отказа принадлежат
/// <see cref="ApplicationFailure"/>.
/// </para>
/// </remarks>
public static class AzurPilotExitCode
{
    /// <summary>Запуск завершён успешно.</summary>
    public const int Success = 0;

    /// <summary>Внутренняя ошибка application host, не сводимая к ожидаемым отказам.</summary>
    public const int InternalError = 1;

    /// <summary>Конфигурация существует, но не соответствует схеме.</summary>
    public const int ConfigurationInvalid = 2;

    /// <summary>Версия схемы конфигурации не поддерживается этой сборкой.</summary>
    public const int ConfigurationSchemaUnsupported = 3;

    /// <summary>Native boundary недоступна.</summary>
    public const int NativeUnavailable = 4;

    /// <summary>Native boundary несовместима с ожидаемым ABI.</summary>
    public const int NativeIncompatible = 5;

    /// <summary>Операция отменена.</summary>
    public const int OperationCancelled = 6;

    /// <summary>Установка MuMuPlayer не обнаружена.</summary>
    public const int MuMuInstallationNotFound = 7;

    /// <summary>Выбранный Android-экземпляр MuMu не найден в установке.</summary>
    public const int MuMuInstanceNotFound = 8;

    /// <summary>Автоматический выбор Android-экземпляра MuMu неоднозначен.</summary>
    public const int MuMuInstanceAmbiguous = 9;

    /// <summary>Control surface установки MuMu не поддерживает запрошенный примитив.</summary>
    public const int MuMuControlSurfaceUnsupported = 10;

    /// <summary>Lifecycle-операция MuMu не привела к требуемому postcondition.</summary>
    public const int MuMuLifecyclePostconditionNotMet = 11;

    /// <summary>Deadline lifecycle-операции MuMu достигнут без требуемого состояния.</summary>
    public const int MuMuLifecycleTimeout = 12;

    /// <summary>
    /// Обнаружено несколько установок MuMuPlayer, а доказуемого выбора между ними нет.
    /// </summary>
    /// <remarks>
    /// Новый код получает следующее свободное значение и добавляется в конец блока: значения уже
    /// опубликованных кодов не меняются, потому что код выхода — часть контракта startup.
    /// </remarks>
    public const int MuMuInstallationAmbiguous = 13;

    /// <summary>Возвращает код выхода для application-level отказа.</summary>
    /// <param name="failure">Ожидаемый отказ application boundary.</param>
    /// <returns>Код выхода процесса, соответствующий коду отказа.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> равен <see langword="null"/>.</exception>
    public static int FromFailure(ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.Code switch
        {
            ApplicationFailure.ConfigurationInvalid => ConfigurationInvalid,
            ApplicationFailure.ConfigurationSchemaUnsupported => ConfigurationSchemaUnsupported,
            ApplicationFailure.NativeUnavailable => NativeUnavailable,
            ApplicationFailure.NativeIncompatible => NativeIncompatible,
            ApplicationFailure.OperationCancelled => OperationCancelled,
            ApplicationFailure.MuMuInstallationNotFound => MuMuInstallationNotFound,
            ApplicationFailure.MuMuInstallationAmbiguous => MuMuInstallationAmbiguous,
            ApplicationFailure.MuMuInstanceNotFound => MuMuInstanceNotFound,
            ApplicationFailure.MuMuInstanceAmbiguous => MuMuInstanceAmbiguous,
            ApplicationFailure.MuMuControlSurfaceUnsupported => MuMuControlSurfaceUnsupported,
            ApplicationFailure.MuMuLifecyclePostconditionNotMet => MuMuLifecyclePostconditionNotMet,
            ApplicationFailure.MuMuLifecycleTimeout => MuMuLifecycleTimeout,
            _ => InternalError,
        };
    }
}
