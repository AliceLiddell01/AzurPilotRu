using AzurPilot.Core.MuMu;
using AzurPilot.Core.Configuration;

namespace AzurPilot.App.Agent;

/// <summary>Единственный каталог discovery, parser и исполняемых обработчиков.</summary>
internal static class AgentCommands
{
    internal static readonly IReadOnlyList<AgentCommand> All = Array.AsReadOnly<AgentCommand>([
        new("discover", false, false, "Машинное описание интерфейса без конфигурации, MuMu и ADB.",
            (c, t) => Task.FromResult(new AgentPayload(Describe()))),
        new("status", false, false, "Краткое состояние runtime и сохранённого target.", (c, t) => c.DiagnosticsAsync(false, t)),
        new("diagnostics", false, false, "Read-only диагностика runtime и сохранённого target.", (c, t) => c.DiagnosticsAsync(true, t)),
        new("settings", false, false, "Эффективные безопасные настройки без записи.",
            (c, t) => Task.FromResult(new AgentPayload(c.Service.Configuration.Configuration))),
        new("instances", false, false, "Inventory MuMu и наблюдённые состояния.", (c, t) => c.InventoryAsync(t)),
        new("resolve", false, true, "Разрешить exact MuMu identity без mutation.",
            (c, t) => Task.FromResult(new AgentPayload(new { instanceId = c.Target!.Value.ToString() }))),
        new("mumu-observe", false, true, "Наблюдать host-side состояние MuMu.",
            (c, t) => Task.FromResult(AgentContext.Result(c.Service.ObserveMuMu(c.Target!.Value)))),
        new("mumu-start", true, true, "Запустить exact MuMu и доказать Running.", (c, t) => c.MuMuAsync(MuMuLifecycleOperation.Start, t)),
        new("mumu-stop", true, true, "Остановить exact MuMu и доказать Stopped.", (c, t) => c.MuMuAsync(MuMuLifecycleOperation.Stop, t)),
        new("mumu-restart", true, true, "Перезапустить exact MuMu с проверкой postcondition.", (c, t) => c.MuMuAsync(MuMuLifecycleOperation.Restart, t)),
        new("android-observe", false, true, "Read-only Android readiness без ADB connect.",
            (c, t) => Task.FromResult(AgentContext.Result(c.Service.ObserveAndroid(c.Target!.Value)))),
        new("environment-prepare", true, true, "Обеспечить готовность MuMu и Android без запуска игры.", (c, t) => c.PrepareAsync(t)),
        new("game-observe", false, true, "Наблюдать tri-state факты Azur Lane Global/EN.",
            (c, t) => Task.FromResult(AgentContext.Result(c.Service.ObserveAzurLane(c.Target!.Value)))),
        new("game-start", true, true, "Подготовить окружение и запустить Azur Lane Global/EN.", (c, t) => c.GameAsync("start", t)),
        new("game-stop", true, true, "Остановить Azur Lane Global/EN с проверкой postcondition.", (c, t) => c.GameAsync("stop", t)),
        new("game-restart", true, true, "Перезапустить Azur Lane Global/EN с проверкой postcondition.", (c, t) => c.GameAsync("restart", t)),
        new("screenshot-scan", false, true, "Один capture и native RGB statistics FullFrame/Center без экспорта файла.",
            async (c, t) => AgentContext.Result(await c.Service.CaptureAndScanAsync(c.Target!.Value, t).ConfigureAwait(false))),
    ]);

    internal static object Describe()
    {
        return new
        {
            schemaVersion = AgentJson.SchemaVersion,
            maximumOutputBytes = AgentJson.MaximumOutputBytes,
            maximumInventoryInstances = AgentRequest.MaximumInventoryInstances,
            argumentTypes = new
            {
                MuMuInstanceValue = new
                {
                    type = "string",
                    automaticValue = MuMuInstanceValue.AutoValue,
                    identityPrefix = MuMuInstanceValue.ProviderPrefix,
                    index = "Десятичные ASCII-цифры: ноль либо положительное целое без ведущих нулей.",
                },
            },
            commands = All.Select(c => new { c.Name, c.Mutation, requiresResolvedTarget = c.Target, c.Description, c.Arguments }),
            effects = new[] { "none", "changed", "mayHaveChanged" },
            artifacts = Array.Empty<string>(),
        };
    }
}
