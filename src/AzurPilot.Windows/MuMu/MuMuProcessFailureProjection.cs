using AzurPilot.Core.Failures;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Проекция отказов границы запуска процесса в MuMu-отказы платформенной стороны.
/// </summary>
/// <remarks>
/// <para>
/// Реализация ничего не решает сама: она делегирует существующим методам
/// <see cref="MuMuPlatformFailureMapper"/>, поэтому коды отказа, состав bounded details и признак
/// повторяемости остаются принадлежностью одного владельца и не дублируются здесь.
/// </para>
/// <para>
/// Смысл отказа принадлежит владельцу возможности: общая граница запуска процесса сообщает отказ через
/// эту проекцию, поэтому один и тот же код запуска обслуживает MuMu и будущие Windows-возможности без
/// второго набора кодов.
/// </para>
/// </remarks>
public sealed class MuMuProcessFailureProjection : IProcessFailureProjection
{
    /// <inheritdoc />
    public ApplicationFailure StartFailed(string executablePath, Exception? exception)
        => MuMuPlatformFailureMapper.ForProcessStartFailure(executablePath, exception);

    /// <inheritdoc />
    public ApplicationFailure TimedOut(
        string executablePath,
        TimeSpan elapsed,
        int standardOutputCharacters,
        int standardErrorCharacters)
        => MuMuPlatformFailureMapper.ForProcessTimeout(
            executablePath,
            elapsed,
            standardOutputCharacters,
            standardErrorCharacters);

    /// <inheritdoc />
    public ApplicationFailure Cancelled() => MuMuPlatformFailureMapper.ForCancellation();
}
