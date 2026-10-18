namespace AzurPilot.Core.Android.Orchestration;

/// <summary>Сведения об эффектах одной операции, сохраняемые и при исключении внешней границы.</summary>
public sealed class AndroidMutationProgress
{
    /// <summary>Вызов внешней границы mutation начат; её эффект может быть неизвестен.</summary>
    public bool MutationAttempted { get; internal set; }

    /// <summary>После mutation независимо доказано требуемое изменение состояния.</summary>
    public bool ChangeProven { get; internal set; }

    /// <summary>Исходное наблюдение transport при его подготовке; lifecycle игры его не заполняет.</summary>
    public AndroidTransportState? InitialTransport { get; internal set; }

    /// <summary>Последние независимо наблюдённые факты подготовки, в том числе при частичном отказе.</summary>
    public AndroidReadinessFacts? LastReadiness { get; internal set; }
}
