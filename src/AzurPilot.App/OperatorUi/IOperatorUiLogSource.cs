namespace AzurPilot.App.OperatorUi;

/// <summary>Потокобезопасный источник ограниченного снимка строк логирования для панели оператора.</summary>
public interface IOperatorUiLogSource
{
    /// <summary>Возвращает независимый снимок последних строк; UI не изменяет полученную коллекцию.</summary>
    /// <returns>Строки в порядке от старых к новым.</returns>
    IReadOnlyList<string> ReadSnapshot();
}

/// <summary>Подключает существующий bounded log buffer без второго владельца логирования.</summary>
public sealed class OperatorUiLogSourceAdapter : IOperatorUiLogSource
{
    private readonly Func<IReadOnlyList<string>> _readSnapshot;

    /// <summary>Принимает функцию, возвращающую потокобезопасный независимый снимок buffer.</summary>
    /// <param name="readSnapshot">Владелец снимка логов.</param>
    public OperatorUiLogSourceAdapter(Func<IReadOnlyList<string>> readSnapshot)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        _readSnapshot = readSnapshot;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadSnapshot() => _readSnapshot();
}
