using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Единственный владелец точной формы аргументов <c>MuMuManager</c>.
/// </summary>
/// <remarks>
/// <para>
/// Форма зафиксирована по реальной установке: подкоманда <c>version</c> вызывается без аргументов;
/// <c>info</c> и <c>control</c> адресуют экземпляр длинной формой <c>--vmindex</c>, которая в справке
/// провайдера объявлена вместе с короткой <c>-v</c> и проверена на установке обеими формами.
/// </para>
/// <para>
/// В аргументе передаётся provider-owned vmindex без канонического префикса: префикс принадлежит
/// текстовой форме identity в конфигурации и логах, а control surface принимает сам номер.
/// </para>
/// <para>
/// Строка командной строки не собирается: возвращается список аргументов, который граница запуска
/// процесса передаёт процессу по одному элементу.
/// </para>
/// </remarks>
public static class MuMuManagerCommandBuilder
{
    /// <summary>Подкоманда, возвращающая версию player.</summary>
    public const string VersionSubcommand = "version";

    /// <summary>Подкоманда, возвращающая сведения об экземплярах.</summary>
    public const string InfoSubcommand = "info";

    /// <summary>Подкоманда управления экземплярами.</summary>
    public const string ControlSubcommand = "control";

    /// <summary>Длинная форма аргумента с номером экземпляра.</summary>
    public const string VmIndexArgument = "--vmindex";

    /// <summary>Значение аргумента, означающее перечисление всех экземпляров.</summary>
    public const string AllInstancesArgumentValue = "all";

    /// <summary>Имя операции запуска экземпляра.</summary>
    public const string LaunchOperation = "launch";

    /// <summary>Имя операции остановки экземпляра.</summary>
    public const string ShutdownOperation = "shutdown";

    /// <summary>Имя провайдерской операции перезапуска экземпляра.</summary>
    /// <remarks>
    /// Операция существует и подтверждена на реальной установке, но в production lifecycle не
    /// вызывается: перезапуск выражается композицией остановки и запуска с подтверждением состояния.
    /// Имя сохранено как часть доказанного контракта control surface.
    /// </remarks>
    public const string RestartOperation = "restart";

    /// <summary>Строит аргументы подкоманды <c>version</c>.</summary>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildVersionArguments() => [VersionSubcommand];

    /// <summary>Строит аргументы запроса сведений об одном экземпляре.</summary>
    /// <param name="id">Identity экземпляра.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildInstanceInfoArguments(MuMuInstanceId id)
        => [InfoSubcommand, VmIndexArgument, id.Index];

    /// <summary>Строит аргументы перечисления всех экземпляров.</summary>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildAllInstancesInfoArguments()
        => [InfoSubcommand, VmIndexArgument, AllInstancesArgumentValue];

    /// <summary>Строит аргументы операции изменения состояния экземпляра.</summary>
    /// <param name="id">Identity экземпляра.</param>
    /// <param name="command">Операция изменения состояния.</param>
    /// <returns>Список аргументов процесса.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="command"/> не входит в поддерживаемый набор.
    /// </exception>
    public static IReadOnlyList<string> BuildControlArguments(MuMuInstanceId id, MuMuControlCommand command)
        => [ControlSubcommand, VmIndexArgument, id.Index, GetOperationName(command)];

    /// <summary>Возвращает имя операции control surface, соответствующее команде.</summary>
    /// <param name="command">Операция изменения состояния.</param>
    /// <returns>Имя операции в форме провайдера.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="command"/> не входит в поддерживаемый набор.
    /// </exception>
    public static string GetOperationName(MuMuControlCommand command)
        => command switch
        {
            MuMuControlCommand.Launch => LaunchOperation,
            MuMuControlCommand.Shutdown => ShutdownOperation,
            _ => throw new ArgumentOutOfRangeException(
                nameof(command), command, "Операция изменения состояния экземпляра MuMu не поддерживается."),
        };
}
