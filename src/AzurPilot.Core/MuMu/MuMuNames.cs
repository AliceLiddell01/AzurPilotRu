using System.Globalization;
using AzurPilot.Core.Configuration;

namespace AzurPilot.Core.MuMu;

/// <summary>
/// Bounded текстовые имена значений MuMu-контракта для логов и structured details.
/// </summary>
/// <remarks>
/// Имена machine-readable и не зависят от языка: сообщения для оператора принадлежат владельцу
/// конкретного отказа, а здесь задаются только стабильные значения полей.
/// </remarks>
internal static class MuMuNames
{
    /// <summary>Возвращает machine-readable имя запрошенной lifecycle-операции.</summary>
    /// <param name="operation">Запрошенная lifecycle-операция.</param>
    /// <returns>Одно из значений <c>start</c>, <c>stop</c>, <c>restart</c>.</returns>
    internal static string OperationName(MuMuLifecycleOperation operation) => operation switch
    {
        MuMuLifecycleOperation.Start => "start",
        MuMuLifecycleOperation.Stop => "stop",
        MuMuLifecycleOperation.Restart => "restart",
        _ => UnknownName,
    };

    /// <summary>Возвращает machine-readable имя host-side состояния.</summary>
    /// <param name="state">Host-side состояние экземпляра.</param>
    /// <returns>Одно из значений <c>unknown</c>, <c>stopped</c>, <c>running</c>.</returns>
    internal static string StateName(MuMuLifecycleState state) => state switch
    {
        MuMuLifecycleState.Unknown => "unknown",
        MuMuLifecycleState.Stopped => "stopped",
        MuMuLifecycleState.Running => "running",
        _ => UnknownName,
    };

    /// <summary>Возвращает machine-readable имя mutation.</summary>
    /// <param name="mutation">Выполненная mutation.</param>
    /// <returns>Одно из значений <c>start</c>, <c>stop</c>.</returns>
    internal static string MutationName(MuMuLifecycleMutation mutation) => mutation switch
    {
        MuMuLifecycleMutation.Start => "start",
        MuMuLifecycleMutation.Stop => "stop",
        _ => UnknownName,
    };

    /// <summary>Возвращает machine-readable режим выбора экземпляра.</summary>
    /// <remarks>
    /// Токен автоматического выбора читается у <see cref="MuMuInstanceValue.AutoValue"/>: то же значение
    /// задаёт выбор экземпляра в конфигурации, поэтому второго владельца токена не заводится.
    /// </remarks>
    /// <param name="isAutomatic">Признак автоматического выбора.</param>
    /// <returns>Одно из значений <c>auto</c>, <c>explicit</c>.</returns>
    internal static string SelectionModeName(bool isAutomatic)
        => isAutomatic ? MuMuInstanceValue.AutoValue : ExplicitSelectionModeName;

    /// <summary>Возвращает bounded описание выполненной mutation.</summary>
    /// <param name="mutation">Выполненная mutation.</param>
    /// <param name="exitCode">Код выхода control utility.</param>
    /// <returns>Строка вида <c>start(exit=0)</c>.</returns>
    internal static string Mutation(MuMuLifecycleMutation mutation, int exitCode)
        => MutationName(mutation)
            + "(exit="
            + exitCode.ToString(CultureInfo.InvariantCulture)
            + ")";

    private const string ExplicitSelectionModeName = "explicit";

    private const string UnknownName = "unknown";
}
