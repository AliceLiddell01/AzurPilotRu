namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Коды, которыми control surface MuMu сообщает об отказе в теле ответа.
/// </summary>
/// <remarks>
/// <para>
/// Коды подтверждены на реальной установке и являются частью наблюдаемого контракта ответа: код выхода
/// процесса совпадает с кодом в теле. Перечень не является признаком поддерживаемой версии: он описывает
/// только те отказы, смысл которых доказан, и не даёт права догадываться о смысле остальных.
/// </para>
/// <para>
/// Наблюдавшиеся отказы: <c>player index not found</c> — запрошенного номера экземпляра нет;
/// <c>Missing param &lt;vmindex&gt; error !!!</c> — номер экземпляра не передан; <c>not handle cmd</c> —
/// неизвестная подкоманда. Adapter всегда передаёт номер экземпляра и знает только те подкоманды,
/// которые подтверждены, поэтому production-путь опирается на первый из них.
/// </para>
/// </remarks>
public static class MuMuProviderCodes
{
    /// <summary>Запрошенного номера экземпляра нет.</summary>
    public const int PlayerIndexNotFound = -200;

    /// <summary>Обязательный номер экземпляра не передан.</summary>
    public const int MissingVmIndexParameter = -21;

    /// <summary>Подкоманда не распознана control surface.</summary>
    public const int NotHandledCommand = -1;
}
