namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Имена полей JSON, которые adapter читает из вывода <c>MuMuManager</c>.
/// </summary>
/// <remarks>
/// Единственный владелец wire-имён: parser и DTO не дублируют строковые литералы. Поля, которых здесь
/// нет, adapter не читает и на них не опирается.
/// </remarks>
public static class MuMuManagerJsonNames
{
    /// <summary>Версия player, возвращаемая подкомандой <c>version</c>.</summary>
    public const string Version = "version";

    /// <summary>Числовой код ошибки провайдера.</summary>
    public const string ErrorCode = "errcode";

    /// <summary>Текстовое сообщение об ошибке провайдера.</summary>
    public const string ErrorMessage = "errmsg";

    /// <summary>Номер экземпляра внутри ответа об экземпляре.</summary>
    public const string Index = "index";

    /// <summary>Display name экземпляра (изменяемое отображаемое имя).</summary>
    public const string Name = "name";

    /// <summary>Признак запущенного процесса экземпляра.</summary>
    public const string IsProcessStarted = "is_process_started";

    /// <summary>Признак запущенного Android внутри экземпляра.</summary>
    public const string IsAndroidStarted = "is_android_started";

    /// <summary>Текущее переходное/итоговое состояние провайдера.</summary>
    public const string PlayerState = "player_state";

    /// <summary>Версия Android экземпляра.</summary>
    public const string AndroidVersion = "android_version";

    /// <summary>Идентификатор процесса экземпляра.</summary>
    public const string ProcessId = "pid";

    /// <summary>Host ADB endpoint экземпляра.</summary>
    public const string AdbHostIp = "adb_host_ip";

    /// <summary>Порт ADB экземпляра.</summary>
    public const string AdbPort = "adb_port";

    /// <summary>Метка создания экземпляра, сохраняемая между запусками.</summary>
    public const string CreatedTimestamp = "created_timestamp";

    /// <summary>Код ошибки операции над экземпляром.</summary>
    public const string InstanceErrorCode = "error_code";

    /// <summary>Код ошибки последнего запуска экземпляра.</summary>
    public const string LaunchErrorCode = "launch_err_code";

    /// <summary>Источник сведений об экземпляре (например, локальный или удалённый).</summary>
    public const string InfoSource = "info_source";
}
