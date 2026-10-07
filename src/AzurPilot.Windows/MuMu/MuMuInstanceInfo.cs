using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Сведения об Android-экземпляре MuMu, полученные от control surface.
/// </summary>
/// <remarks>
/// <para>
/// Идентичность экземпляра — только <see cref="Id"/>: провайдерский номер, который сохраняется между
/// запусками. <see cref="DisplayName"/> изменяем и в идентичности не участвует;
/// <see cref="ProcessId"/>, <see cref="AdbPort"/> и <see cref="CreatedTimestamp"/> приведены как
/// диагностические сведения, а не как идентификатор.
/// </para>
/// <para>
/// <see cref="State"/> — доказанное host-side состояние по правилу <see cref="MuMuPlayerStateMap"/>;
/// <see cref="RawPlayerState"/> сохраняется рядом как evidence и потребителями состояния не
/// интерпретируется.
/// </para>
/// </remarks>
public sealed record MuMuInstanceInfo
{
    /// <summary>Провайдерская стабильная identity экземпляра.</summary>
    public required MuMuInstanceId Id { get; init; }

    /// <summary>Изменяемое отображаемое имя экземпляра или <see langword="null"/>, если провайдер его не сообщил.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Версия Android экземпляра или <see langword="null"/>, если провайдер её не сообщил.</summary>
    public string? AndroidVersion { get; init; }

    /// <summary>Доказанное host-side состояние экземпляра.</summary>
    public required MuMuLifecycleState State { get; init; }

    /// <summary>Сырое значение <c>player_state</c> или <see langword="null"/>, если поля не было.</summary>
    public string? RawPlayerState { get; init; }

    /// <summary>Значение поля <c>is_process_started</c>.</summary>
    public required bool IsProcessStarted { get; init; }

    /// <summary>Значение поля <c>is_android_started</c>.</summary>
    public required bool IsAndroidStarted { get; init; }

    /// <summary>Идентификатор процесса экземпляра или <see langword="null"/>, если процесса нет.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Host ADB endpoint экземпляра или <see langword="null"/>, если он не сообщён.</summary>
    /// <remarks>
    /// Transport metadata, а не identity: значение изменяемо и в идентичности экземпляра не участвует.
    /// Вместе с <see cref="AdbPort"/> оно образует точный ADB endpoint, которым адресуется устройство
    /// этого экземпляра; значение по умолчанию вместо несообщённого не подставляется.
    /// </remarks>
    public string? AdbHostIp { get; init; }

    /// <summary>Порт ADB экземпляра или <see langword="null"/>, если он не сообщён.</summary>
    public int? AdbPort { get; init; }

    /// <summary>Метка создания экземпляра или <see langword="null"/>, если она не сообщена.</summary>
    public long? CreatedTimestamp { get; init; }
}
