using System.Text.Json;

namespace AzurPilot.TuiHarness;

internal sealed record ScenarioDefinition(string Name, ScenarioStep[] Steps);

internal sealed record ScenarioStep
{
    public string Command { get; init; } = "capture";
    public string? Value { get; init; }
    public string? Contains { get; init; }
    public string? Name { get; init; }
    public string[] Absent { get; init; } = [];
    public int Columns { get; init; } = 120;
    public int Rows { get; init; } = 30;
    public int X { get; init; }
    public int Y { get; init; }
    public string? HistoryChangedFrom { get; init; }
    public string? HistoryMarkerPrefix { get; init; }
    public string? MouseHistoryFrom { get; init; }
    public string? HistoryEquals { get; init; }
    public bool CheckSize { get; init; }
}

internal static class Scenarios
{
    internal static async Task<HarnessResponse> RunAsync(HarnessRequest request)
    {
        if (request.Scenario is null)
        {
            throw new ArgumentException("Требуется --scenario JSON.");
        }

        if (new FileInfo(request.Scenario).Length > 262144)
        {
            throw new ArgumentException("Сценарий превышает 256 KiB.");
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(request.Scenario));
        ScenarioDefinition definition = document.RootElement.Deserialize<ScenarioDefinition>(HarnessJson.Options)!;
        if (definition.Steps.Length is < 1 or > 300)
        {
            throw new ArgumentException("Сценарий должен содержать 1–300 шагов.");
        }

        HarnessResponse started = await Program.StartAsync(request);
        if (started.Status != "PASS")
        {
            return started;
        }

        string id = started.Session!;
        List<object> results = [];
        Dictionary<string, JsonElement> checkpoints = new(StringComparer.Ordinal);
        string status = "PASS";
        string message = "Сценарий выполнен; PNG требует отдельного визуального осмотра.";
        try
        {
            foreach (ScenarioStep step in definition.Steps)
            {
                (int x, int y) = step.MouseHistoryFrom is string historyCheckpoint
                    ? TerminalUiObservation.HistoryPoint(checkpoints[historyCheckpoint]) : (step.X, step.Y);
                HarnessRequest operation = request with
                {
                    Command = step.Command,
                    Session = id,
                    Value = step.Value,
                    Contains = step.Contains,
                    Name = step.Name,
                    Columns = step.Columns,
                    Rows = step.Rows,
                    X = x,
                    Y = y,
                };
                HarnessResponse response = await Program.SendAsync(operation);
                results.Add(new { step = step.Name ?? step.Command, response });
                if (response.Status != "PASS")
                {
                    status = response.Status;
                    message = response.Message;
                    break;
                }

                if (step.Command == "capture")
                {
                    JsonElement paths = JsonSerializer.SerializeToElement(response.Data, HarnessJson.Options);
                    using JsonDocument cells = JsonDocument.Parse(await File.ReadAllTextAsync(paths.GetProperty("cells").GetString()!));
                    JsonElement snapshot = cells.RootElement.Clone();
                    string text = TerminalRenderer.VisibleText(snapshot);
                    foreach (string absent in step.Absent)
                    {
                        if (text.Contains(absent, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("Запрещённый текст в ячейках: " + absent);
                        }
                    }

                    if (step.CheckSize && (snapshot.GetProperty("columns").GetInt32() != step.Columns
                        || snapshot.GetProperty("rows").GetInt32() != step.Rows))
                    {
                        throw new InvalidOperationException("Размер ячеек не совпал с resize ConPTY.");
                    }

                    if (step.HistoryChangedFrom is string prior)
                    {
                        TerminalUiObservation.RequireHistoryScrolledUp(checkpoints[prior], snapshot, step.HistoryMarkerPrefix);
                    }

                    if (step.HistoryEquals is string original)
                    {
                        TerminalUiObservation.RequireHistoryEqual(checkpoints[original], snapshot);
                    }

                    checkpoints.Add(step.Name ?? results.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot);
                }
            }
        }
        catch (Exception exception)
        {
            status = exception is HarnessBlockedException ? "BLOCKED" : "FAIL";
            message = exception.Message;
        }
        finally
        {
            HarnessResponse closed;
            try
            {
                closed = await Program.SendAsync(new HarnessRequest { Command = "close", Session = id });
            }
            catch (Exception exception)
            {
                closed = SessionOwner.Failure(exception, id);
            }

            results.Add(new { step = "cleanup", response = closed });
            if (closed.Status != "PASS")
            {
                status = "BLOCKED";
                message = "Не подтверждено освобождение ресурсов: " + closed.Message;
            }
        }

        JsonElement startData = JsonSerializer.SerializeToElement(started.Data, HarnessJson.Options);
        string directory = startData.GetProperty("directory").GetString()!;
        string report = Path.Combine(directory, "scenario.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { status, message, definition.Name, results }, HarnessJson.Options));
        return new(status, id, message, new { report, directory, stepCount = results.Count, visualAssessment = "NOT_TESTED" });
    }
}
