using AzurPilot.Core.Failures;

namespace AzurPilot.Core.MuMu;

/// <summary>
/// Семантика выбора Android-экземпляра MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Тип описывает только семантику выбора: автоматический выбор ровно одного подходящего экземпляра либо
/// явный выбор по stable identity. Синтаксис строки конфигурации принадлежит конфигурации и здесь не
/// дублируется: orchestration не разбирает строки и не знает их формы.
/// </para>
/// <para>
/// Автоматический выбор не является «выбором наиболее вероятного»: при нуле экземпляров он даёт
/// <see cref="ApplicationFailure.MuMuInstanceNotFound"/>, при двух и более —
/// <see cref="ApplicationFailure.MuMuInstanceAmbiguous"/>. Первый, последний и vmindex <c>0</c> не
/// выбираются никогда.
/// </para>
/// <para>
/// Экземпляры создаются фабриками <see cref="Auto"/> и <see cref="Explicit"/>: явный выбор без identity
/// не определён, поэтому такой экземпляр считается ошибкой программирования вызывающей стороны.
/// </para>
/// </remarks>
/// <param name="IsAutomatic">Признак автоматического выбора ровно одного подходящего экземпляра.</param>
/// <param name="ExplicitId">Явно выбранная identity экземпляра; для автоматического выбора — отсутствует.</param>
public sealed record MuMuInstanceSelection(bool IsAutomatic, MuMuInstanceId? ExplicitId)
{
    /// <summary>Создаёт автоматический выбор ровно одного подходящего экземпляра.</summary>
    /// <returns>Семантику автоматического выбора.</returns>
    public static MuMuInstanceSelection Auto() => new(true, null);

    /// <summary>Создаёт явный выбор экземпляра по stable identity.</summary>
    /// <param name="id">Identity выбранного экземпляра.</param>
    /// <returns>Семантику явного выбора.</returns>
    public static MuMuInstanceSelection Explicit(MuMuInstanceId id) => new(false, id);
}
