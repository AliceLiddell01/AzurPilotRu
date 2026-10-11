using AzurPilot.App.OperatorServices;

namespace AzurPilot.App.OperatorUi;

/// <summary>Потокобезопасный источник ограниченного снимка структурированных записей журнала.</summary>
public interface IOperatorUiLogSource
{
    /// <summary>Возвращает независимый снимок последних записей; UI не изменяет полученную коллекцию.</summary>
    /// <returns>Записи в порядке от старых к новым.</returns>
    IReadOnlyList<AzurPilotOperatorLogEntry> ReadSnapshot();
}

/// <summary>Подключает существующий bounded log buffer без второго владельца логирования.</summary>
public sealed class OperatorUiLogSourceAdapter : IOperatorUiLogSource
{
    private readonly Func<IReadOnlyList<AzurPilotOperatorLogEntry>> _readSnapshot;

    /// <summary>Принимает функцию, возвращающую потокобезопасный независимый снимок buffer.</summary>
    /// <param name="readSnapshot">Владелец снимка логов.</param>
    public OperatorUiLogSourceAdapter(Func<IReadOnlyList<AzurPilotOperatorLogEntry>> readSnapshot)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        _readSnapshot = readSnapshot;
    }

    /// <inheritdoc />
    public IReadOnlyList<AzurPilotOperatorLogEntry> ReadSnapshot() => _readSnapshot();
}
