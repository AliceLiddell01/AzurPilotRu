namespace AzurPilot.Core.Android.Orchestration;

/// <summary>Сведения об эффектах одной операции, сохраняемые и при исключении внешней границы.</summary>
public sealed class AndroidMutationProgress
{
    /// <summary>Вызов внешней границы mutation начат; её эффект может быть неизвестен.</summary>
    public bool MutationAttempted { get; internal set; }

    /// <summary>После mutation независимо доказано требуемое изменение состояния.</summary>
    public bool ChangeProven { get; internal set; }
}
