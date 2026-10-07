using AzurPilot.Core.Failures;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Проекция отказов общей границы запуска процесса в Android-отказы платформенной стороны.
/// </summary>
/// <remarks>
/// <para>
/// Реализация ничего не решает сама: она делегирует фабрикам <see cref="AndroidHostFailures"/>, поэтому
/// коды отказа и состав bounded details остаются принадлежностью одного владельца и не дублируются здесь.
/// </para>
/// <para>
/// Проекция не использует MuMu-коды: отказ запуска ADB — это отказ обнаруженной установки, а не отказ
/// lifecycle MuMu. Недоступность исполняемого файла сообщается как
/// <see cref="ApplicationFailure.AndroidAdbUnavailable"/>, превышение дедлайна команды — как
/// <see cref="ApplicationFailure.AndroidEndpointUnavailable"/> (точный target не ответил), отмена — как
/// <see cref="ApplicationFailure.OperationCancelled"/>.
/// </para>
/// </remarks>
public sealed class AdbProcessFailureProjection : IProcessFailureProjection
{
    /// <inheritdoc />
    public ApplicationFailure StartFailed(string executablePath, Exception? exception)
        => AndroidHostFailures.AdbStartFailed(executablePath, exception);

    /// <inheritdoc />
    public ApplicationFailure TimedOut(
        string executablePath,
        TimeSpan elapsed,
        int standardOutputCharacters,
        int standardErrorCharacters)
        => AndroidHostFailures.CommandTimedOut(
            executablePath,
            elapsed,
            standardOutputCharacters,
            standardErrorCharacters);

    /// <inheritdoc />
    public ApplicationFailure Cancelled() => AndroidHostFailures.Cancelled();
}
