namespace AzurPilot.App;

/// <summary>Итог запуска интерактивной сессии и startup-диагностики application host.</summary>
/// <param name="Startup">Типизированный результат startup.</param>
/// <param name="InteractiveSessionStarted">Запускалась ли интерактивная сессия.</param>
public sealed record AzurPilotInteractiveRunResult(
    AzurPilotStartupResult Startup,
    bool InteractiveSessionStarted);
