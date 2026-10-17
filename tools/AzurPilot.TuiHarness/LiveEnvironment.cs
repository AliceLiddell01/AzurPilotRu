using System.Diagnostics;
using System.Text.Json;
using AzurPilot.Core.Configuration;

namespace AzurPilot.TuiHarness;

// Ограниченный opt-in: только подготовка окружения и восстановление точного MuMu с исходным Stopped.
internal static class LiveEnvironment
{
    internal static async Task<JsonElement> ObserveAsync(string exe, string config, string instance)
    {
        ProcessStartInfo info = new(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "agent", "mumu-observe", "--config", config, "--target", instance, "--timeout-ms", "15000" })
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            string json = await output;
            _ = await error;
            if (json.Length > 262144)
            {
                throw new HarnessBlockedException("Предварительное наблюдение превысило бюджет.");
            }

            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    internal static void RequireStopped(JsonElement observation, string expectedTarget)
    {
        if (!observation.TryGetProperty("status", out JsonElement status) || status.GetString() != "success"
            || !observation.TryGetProperty("target", out JsonElement target) || target.GetString() != expectedTarget
            || !observation.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("state", out JsonElement state) || state.GetString() != "Stopped")
        {
            throw new HarnessBlockedException("Live environment требует доказанный исходный Stopped точного экземпляра. Unknown/Running, неоднозначность и ошибки не дают права управления.");
        }
    }

    internal static async Task<Exception?> StopAndObserveAsync(
        Func<Task> stop,
        Func<Task<JsonElement>> observe,
        Func<JsonElement, Task> persist,
        string expectedTarget)
    {
        Exception? stopFailure = null;
        try
        {
            await stop();
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        JsonElement postflight = await observe();
        await persist(postflight);
        RequireStopped(postflight, expectedTarget);
        return stopFailure;
    }

    internal static async Task<HarnessResponse> RunAsync(HarnessRequest request)
    {
        request.Validate();
        HarnessResponse start = await Program.StartAsync(request with { Mode = "normal" });
        if (start.Status != "PASS")
        {
            return start;
        }

        string id = start.Session!;
        JsonElement data = JsonSerializer.SerializeToElement(start.Data, HarnessJson.Options);
        string directory = data.GetProperty("directory").GetString()!;
        string config = Path.Combine(
            directory,
            "runtime",
            Path.GetFileName(AzurPilotConfigurationPath.GetDefaultRuntimePath()));
        string exe = Path.GetFullPath(request.Exe!);
        List<HarnessResponse> trace = [];
        string status = "PASS";
        string message = "Подготовка и восстановление подтверждены.";
        bool attempted = false;
        bool restored = false;
        async Task<HarnessResponse> Step(string command, string? value = null, string? contains = null, string? name = null)
        {
            HarnessResponse response = await Program.SendAsync(new HarnessRequest
            {
                Command = command,
                Session = id,
                Value = value,
                Contains = contains,
                Name = name,
                TimeoutMs = 30000
            });
            trace.Add(response);
            return response.Status != "PASS" ? throw new HarnessBlockedException(response.Message) : response;
        }

        async Task Confirm(string command)
        {
            _ = await Step("key", "Escape");
            _ = await Step("key", "Control+a");
            _ = await Step("key", "Backspace");
            _ = await Step("text", command);
            _ = await Step("key", "Enter");
            _ = await Step("capture", contains: "> Отмена", name: command.Contains("stop", StringComparison.Ordinal) ? "restore-confirm" : "prepare-confirm");
            _ = await Step("key", "ArrowDown");
            _ = await Step("key", "Enter");
        }

        try
        {
            _ = await Step("capture", contains: request.LiveInstance, name: "preflight-ui");
            // Право управления известно только после успешной предварительной проверки из CreateAsync.
            attempted = true;
            await Confirm("/environment-start");
            _ = await Step("capture", contains: "Окружение готово.", name: "environment-ready");
        }
        catch (Exception exception)
        {
            status = "BLOCKED";
            message = exception.Message;
        }
        finally
        {
            try
            {
                Exception? stopFailure = await StopAndObserveAsync(
                    async () =>
                    {
                        if (attempted)
                        {
                            _ = await Step("key", "F4");
                            _ = await Step("wait-idle");
                            await Confirm("/environment-stop");
                            _ = await Step("wait-idle");
                        }
                    },
                    () => ObserveAsync(exe, config, request.LiveInstance!),
                    postflight => File.WriteAllTextAsync(Path.Combine(directory, "postflight.json"), postflight.GetRawText()),
                    request.LiveInstance!);
                restored = true;
                if (stopFailure is not null)
                {
                    string earlierFailure = status == "BLOCKED" ? message + " " : string.Empty;
                    status = "BLOCKED";
                    message = earlierFailure + "Остановка через TUI не завершена: "
                        + stopFailure.Message + "; postflight подтвердил Stopped.";
                }
            }
            catch (Exception exception)
            {
                status = "BLOCKED";
                message = "Восстановление НЕ подтверждено: " + exception.Message;
            }

            HarnessResponse closed;
            try
            {
                closed = await Program.SendAsync(new HarnessRequest { Command = "close", Session = id });
            }
            catch (Exception exception)
            {
                closed = SessionOwner.Failure(exception, id);
            }

            trace.Add(closed);
            if (closed.Status != "PASS")
            {
                status = "BLOCKED";
                message += " Освобождение ресурсов владельца не подтверждено: " + closed.Message;
            }
        }

        string report = Path.Combine(directory, "live-environment.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            status,
            message,
            request.LiveInstance,
            allowedMutations = "environment-prepare,mumu-stop",
            attempted,
            restored,
            trace,
            crashRecovery = "При аварийной потере owner внешний MuMu не убивается Job Object; восстановление требует нового exact-target preflight."
        }, HarnessJson.Options));
        return new(status, id, message, new { report, restored });
    }
}
