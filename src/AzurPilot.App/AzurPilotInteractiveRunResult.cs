namespace AzurPilot.App;

/// <summary>Итог запуска интерактивной сессии и диагностики запуска хоста приложения.</summary>
/// <param name="Startup">Типизированный результат запуска.</param>
/// <param name="InteractiveSessionStarted">Запускалась ли интерактивная сессия.</param>
public sealed record AzurPilotInteractiveRunResult(
    AzurPilotStartupResult Startup,
    bool InteractiveSessionStarted);
