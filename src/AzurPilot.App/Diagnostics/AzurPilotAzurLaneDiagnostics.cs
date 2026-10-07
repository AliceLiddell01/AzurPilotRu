using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;

namespace AzurPilot.App;

/// <summary>
/// Bounded секция диагностического snapshot о состоянии игры Azur Lane Global/EN.
/// </summary>
/// <remarks>
/// <para>
/// Секция отвечает на вопросы диагностики и не превращается в дамп: каков продукт и его package identity,
/// установлен ли продукт, запущен ли его процесс и находится ли продукт на переднем плане. Полного списка
/// процессов, полного списка пакетов, полного <c>dumpsys</c> и полного stdout/stderr команд ADB в секции
/// нет.
/// </para>
/// <para>
/// Продукт опознаётся явно: <see cref="Product"/> и <see cref="Package"/> приходят от владельца product
/// identity, а не выводятся из наблюдаемого ответа устройства. Наблюдение выполняется только чтением и
/// только тогда, когда transport доказанно готов к командам.
/// </para>
/// <para>
/// Три факта независимы и трёхзначны: установленный пакет не означает запущенного процесса, а запущенный
/// процесс не означает переднего плана. Недоказанное значение — <see langword="null"/>, доказанное
/// отсутствие — <see langword="false"/>, доказанное присутствие — <see langword="true"/>. Секция не
/// выводит один факт из другого и не подменяет недоказанное значение доказанным отсутствием.
/// </para>
/// <para>
/// Отказ — результат диагностики, а не исключение: он попадает в <see cref="Failure"/> как данные, и
/// startup не превращает его в ненулевой код выхода. Запуск и остановка игры диагностикой не выполняются.
/// </para>
/// <para>
/// Текстовые значения секции приводятся к bounded однострочной форме владельцем ограничения
/// <see cref="BoundedDiagnosticText"/>: evidence наблюдения приходит извне, поэтому переносы строк и
/// произвольная длина до секции не доходят.
/// </para>
/// </remarks>
/// <param name="Product">Отображаемое имя продукта: Azur Lane Global/EN.</param>
/// <param name="Package">Package identity продукта.</param>
/// <param name="IsInstalled">
/// Признак установленного пакета продукта: <see langword="null"/>, если значение не доказано.
/// </param>
/// <param name="IsProcessRunning">
/// Признак запущенного процесса продукта: <see langword="null"/>, если значение не доказано.
/// </param>
/// <param name="IsForeground">
/// Признак того, что продукт находится на переднем плане: <see langword="null"/>, если значение не
/// доказано.
/// </param>
/// <param name="State">
/// Производное доказанное состояние продукта; <see langword="null"/>, если состояние не наблюдалось.
/// Недоказанные факты дают <see cref="AzurLaneGameState.Unknown"/>, а не доказанное отсутствие.
/// </param>
/// <param name="Evidence">Bounded evidence read-only наблюдения состояния продукта.</param>
/// <param name="Failure">
/// Application-level отказ наблюдения; <see langword="null"/>, если отказов не было.
/// </param>
public sealed record AzurPilotAzurLaneDiagnostics(
    string Product,
    string Package,
    bool? IsInstalled,
    bool? IsProcessRunning,
    bool? IsForeground,
    AzurLaneGameState? State,
    string? Evidence,
    ApplicationFailure? Failure)
{
    /// <summary>Собирает bounded секцию состояния игры из read-only пробы.</summary>
    /// <remarks>
    /// <para>
    /// Секция не наблюдает ничего сама: факты уже собраны пробой. Отказ пробы становится
    /// <see cref="Failure"/> секции — в том числе отказ более раннего шага, на котором наблюдение игры не
    /// выполнялось: тогда секция сообщает, почему состояние продукта осталось недоказанным.
    /// </para>
    /// <para>
    /// Факты передаются как есть, вместе с недоказанностью: секция не достраивает недоказанное значение до
    /// <see langword="false"/> и не выводит состояние из своих полей — производное состояние принадлежит
    /// orchestration.
    /// </para>
    /// </remarks>
    /// <param name="probe">Read-only результат Android-диагностики одного запуска.</param>
    /// <returns>Секция с bounded фактами либо с application-level отказом наблюдения.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> равен <see langword="null"/>.</exception>
    internal static AzurPilotAzurLaneDiagnostics Capture(AzurPilotAndroidProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        AzurLaneGameObservation? game = probe.Game;

        return new AzurPilotAzurLaneDiagnostics(
            Product: BoundedDiagnosticText.Bounded(AzurLaneProduct.DisplayName),
            Package: BoundedDiagnosticText.Bounded(AzurLaneProduct.Package),
            IsInstalled: game?.Facts.Installed,
            IsProcessRunning: game?.Facts.ProcessRunning,
            IsForeground: game?.Facts.Foreground,
            State: game?.State,
            Evidence: game is null ? null : BoundedDiagnosticText.Bounded(game.Evidence),
            Failure: probe.Failure);
    }
}
