using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.Game;

namespace AzurPilot.Windows.Android;

/// <summary>Один bundled ADB host для проверки координат и одиночного игрового ввода.</summary>
public interface IAndroidGameInputHost
{
    /// <summary>Доказывает точное соответствие capture, focused app и input viewport.</summary>
    Task<ApplicationResult<bool>> ValidateGameInputAsync(GameFrameProvenance frame, CancellationToken cancellationToken);
    /// <summary>Отправляет один tap; успех процесса не доказывает эффект.</summary>
    Task<ApplicationResult<AndroidCommandOutcome>> TapGameAsync(GameTap tap, CancellationToken cancellationToken);
}
