using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Операция изменения состояния экземпляра, которую adapter вызывает у control surface.
/// </summary>
/// <remarks>
/// <para>
/// Перечень закрыт формой control surface, доказанной на реальной установке. Провайдерский verb
/// перезапуска существует и подтверждён разведкой, но в перечне отсутствует намеренно: lifecycle
/// подтверждает переход наблюдением авторитетного состояния, поэтому перезапуск выражается композицией
/// остановки и запуска, а не отдельной операцией, которую никто не выполняет.
/// </para>
/// <para>
/// Значения совпадают с host-примитивами Core по смыслу, но остаются отдельным типом платформенной
/// стороны: adapter не объявляет capability-флагов и не решает, какие операции существуют на уровне
/// публичной операции сервиса.
/// </para>
/// </remarks>
public enum MuMuControlCommand
{
    /// <summary>Запуск экземпляра.</summary>
    Launch = 0,

    /// <summary>Остановка экземпляра.</summary>
    Shutdown = 1,
}

/// <summary>
/// Результат операции изменения состояния экземпляра, сообщённый провайдером.
/// </summary>
/// <remarks>
/// <para>
/// Принятие операции провайдером не является доказательством нового состояния: провайдер отвечает о
/// приёме команды, а состояние подтверждается отдельным наблюдением. Поэтому
/// <see cref="IsAccepted"/> описывает только ответ провайдера, а не postcondition.
/// </para>
/// <para>
/// Отказ провайдера возвращается значением вместе с его кодом: код выхода процесса равен коду в теле
/// ответа, поэтому <see cref="ExitCode"/> сохраняет этот факт и передаётся дальше как evidence.
/// </para>
/// </remarks>
public sealed record MuMuControlOutcome
{
    /// <summary>Identity экземпляра, к которому относилась операция.</summary>
    public required MuMuInstanceId Id { get; init; }

    /// <summary>Выполненная операция.</summary>
    public required MuMuControlCommand Command { get; init; }

    /// <summary>Код выхода процесса control utility.</summary>
    /// <remarks>
    /// Код выхода равен коду в теле ответа: parser не принимает ответ, в котором эти значения
    /// расходятся. Нулевой код выхода означает принятие операции, а не доказанный postcondition.
    /// </remarks>
    public required int ExitCode { get; init; }

    /// <summary>Bounded вывод control utility, пригодный для диагностики.</summary>
    /// <remarks>
    /// Полный вывод процесса в него не попадает: значение ограничено <see cref="MuMuBoundedText"/>.
    /// </remarks>
    public required string BoundedOutput { get; init; }

    /// <summary>Ошибка провайдера или <see langword="null"/>, если операция принята.</summary>
    public MuMuProviderError? ProviderError { get; init; }

    /// <summary>Признак того, что провайдер принял операцию без ошибки.</summary>
    public bool IsAccepted => ProviderError is null;

    /// <summary>Признак доказанного отказа «запрошенного номера экземпляра нет».</summary>
    public bool IsIndexNotFound => ProviderError is { Code: MuMuProviderCodes.PlayerIndexNotFound };
}
