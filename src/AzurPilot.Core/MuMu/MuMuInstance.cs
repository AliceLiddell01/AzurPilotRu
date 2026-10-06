namespace AzurPilot.Core.MuMu;

/// <summary>
/// Android-экземпляр MuMu, найденный в установке.
/// </summary>
/// <remarks>
/// <see cref="DisplayName"/> и <see cref="AndroidVersion"/> — сведения для оператора. Identity
/// экземпляра определяется только <see cref="Id"/>: отображаемое имя не участвует ни в выборе, ни в
/// поиске, ни в сравнении экземпляров.
/// </remarks>
/// <param name="Id">Стабильная identity экземпляра (provider-owned vmindex MuMu).</param>
/// <param name="DisplayName">Отображаемое имя экземпляра для оператора.</param>
/// <param name="AndroidVersion">Версия Android внутри экземпляра.</param>
public sealed record MuMuInstance(MuMuInstanceId Id, string DisplayName, string AndroidVersion);
