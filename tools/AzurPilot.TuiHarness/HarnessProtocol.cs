using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AzurPilot.TuiHarness;

internal static class HarnessJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

internal sealed partial record HarnessRequest
{
    public string Command { get; init; } = "capture";
    public string? Session { get; init; }
    public string? Exe { get; init; }
    public string? Output { get; init; }
    public string? Profile { get; init; }
    public string Mode { get; init; } = "normal";
    public string? Value { get; init; }
    public string? Contains { get; init; }
    public string? Name { get; init; }
    public string? Scenario { get; init; }
    public int Columns { get; init; } = 120;
    public int Rows { get; init; } = 30;
    public int X { get; init; }
    public int Y { get; init; }
    public int TimeoutMs { get; init; } = HarnessTimeouts.DefaultRequestTimeoutMs;
    public int LifetimeSeconds { get; init; } = HarnessTimeouts.DefaultLifetimeSeconds;
    public bool Calibration { get; init; }
    public string? LiveInstance { get; init; }
    public string? Authorization { get; init; }
    public string Providers { get; init; } = "disabled";
    public int StartupClientPid { get; init; }

    internal static HarnessRequest Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new() { Command = "help" };
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException("Каждый параметр задаётся один раз как --name value.");
            }
        }

        string? Take(string key)
        {
            return values.Remove("--" + key, out string? value) ? value : null;
        }

        int Number(string key, int fallback)
        {
            return Take(key) is string value
                    ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        }

        HarnessRequest request = new()
        {
            Command = args[0],
            Session = Take("session"),
            Exe = Take("exe"),
            Output = Take("output"),
            Profile = Take("profile"),
            LiveInstance = Take("instance"),
            Authorization = Take("authorize"),
            Providers = Take("providers") ?? "disabled",
            Mode = Take("mode") ?? "normal",
            Value = Take("value"),
            Contains = Take("contains"),
            Name = Take("name"),
            Scenario = Take("scenario"),
            Columns = Number("columns", 120),
            Rows = Number("rows", 30),
            X = Number("x", 0),
            Y = Number("y", 0),
            TimeoutMs = Number("timeout-ms", HarnessTimeouts.DefaultRequestTimeoutMs),
            LifetimeSeconds = Number("lifetime-seconds", HarnessTimeouts.DefaultLifetimeSeconds),
        };
        if (values.Count > 0)
        {
            throw new ArgumentException("Неизвестные параметры: " + string.Join(", ", values.Keys));
        }

        request.Validate();
        return request;
    }

    internal void Validate()
    {
        if (Command is not ("start" or "run" or "close" or "status" or "capture" or "wait" or "text" or "key"
            or "resize" or "mouse" or "help" or "install-browser" or "fidelity" or "live-environment" or "wait-idle"))
        {
            throw new ArgumentException("Неизвестная команда harness.");
        }

        if (Session is not null && !SessionIdPattern().IsMatch(Session))
        {
            throw new ArgumentException("session ID должен содержать 32 строчные hex-цифры.");
        }

        if (Name is not null && !CaptureNamePattern().IsMatch(Name))
        {
            throw new ArgumentException("Имя capture: 1–64 буквы ASCII, цифры, '-' или '_'.");
        }

        CheckSize(Columns, Rows);
        if (Providers is not ("disabled" or "read-only"))
        {
            throw new ArgumentException("Providers: disabled или read-only. Политика safe-сессии запрещает изменения состояния.");
        }

        if (TimeoutMs is < HarnessTimeouts.MinimumRequestTimeoutMs or > HarnessTimeouts.MaximumRequestTimeoutMs
            || LifetimeSeconds is < HarnessTimeouts.MinimumLifetimeSeconds or > HarnessTimeouts.MaximumLifetimeSeconds
            || Value?.Length > 4096 || Contains?.Length > 256)
        {
            throw new ArgumentException(
                $"Превышены границы timeout ({HarnessTimeouts.MinimumRequestTimeoutMs}–{HarnessTimeouts.MaximumRequestTimeoutMs} ms), "
                + $"lifetime ({HarnessTimeouts.MinimumLifetimeSeconds}–{HarnessTimeouts.MaximumLifetimeSeconds} s) или ввода (4096).");
        }

        if (Mode is not ("normal" or "manual" or "diagnostic"))
        {
            throw new ArgumentException("Режим: normal, manual или diagnostic.");
        }

        if (Command == "live-environment")
        {
            if (Authorization != "prepare-and-restore" || LiveInstance is null
                || !Core.Configuration.MuMuInstanceValue.IsValid(LiveInstance)
                || LiveInstance == Core.Configuration.MuMuInstanceValue.AutoValue)
            {
                throw new ArgumentException("Live: требуется --instance mumu:<index> --authorize prepare-and-restore.");
            }
        }
        else if (LiveInstance is not null || Authorization is not null)
        {
            throw new ArgumentException("Разрешение live допустимо только в ограниченном сценарии live-environment.");
        }
    }

    internal static void CheckSize(int columns, int rows)
    {
        if (columns is < 20 or > 240 || rows is < 10 or > 100)
        {
            throw new ArgumentException("Размер терминала: 20–240 колонок, 10–100 строк.");
        }
    }

    [GeneratedRegex("\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex("\\A[a-zA-Z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureNamePattern();
}

internal sealed record HarnessResponse(string Status, string? Session, string Message, object? Data = null)
{
    internal int ExitCode => Status switch { "PASS" => 0, "FAIL" => 1, "BLOCKED" => 2, _ => 3 };
}

internal sealed class HarnessBlockedException : Exception
{
    internal HarnessBlockedException(string message) : base(message) { }
}

internal sealed class HarnessAssertionException : Exception
{
    internal HarnessAssertionException(string message) : base(message) { }
}
