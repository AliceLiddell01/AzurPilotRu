using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;

namespace AzurPilot.App.Agent;

internal sealed record AgentArgument(string Name, string Type, bool Required, string Description);

internal sealed record AgentCommand(
    string Name, bool Mutation, bool Target, string Description,
    Func<AgentContext, CancellationToken, Task<AgentPayload>> Handler)
{
    public IReadOnlyList<AgentArgument> Arguments => Target
        ? [new("--target", "MuMuInstanceValue", false, "Exact identity либо однозначный auto; по умолчанию mumu.instance."),
            AgentRequest.ConfigurationArgument, AgentRequest.TimeoutArgument]
        : Name == "discover" ? [AgentRequest.TimeoutArgument]
        : [AgentRequest.ConfigurationArgument, AgentRequest.TimeoutArgument];
}

internal sealed record AgentRequest(AgentCommand Command, string? Target, int TimeoutMilliseconds, string? ConfigurationPath)
{
    internal const int DefaultTimeoutMilliseconds = 180_000;
    internal const int MaximumTimeoutMilliseconds = 300_000;
    internal const int MaximumInventoryInstances = 64;
    internal static readonly AgentArgument TimeoutArgument = new("--timeout-ms", "integer", false,
        $"Deadline: 1..{MaximumTimeoutMilliseconds}; по умолчанию {DefaultTimeoutMilliseconds} ms.");
    internal static readonly AgentArgument ConfigurationArgument = new("--config", "absolute-path", false,
        "Явный источник строгой конфигурации; по умолчанию runtime YAML/legacy JSON.");

    internal static ApplicationResult<AgentRequest> Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "agent" || args.Length > 8 || args.Any(a => a.Length > 256))
        {
            return Invalid();
        }

        AgentCommand? command = AgentCommands.All.SingleOrDefault(c => c.Name == args[1]);
        if (command is null)
        {
            return Invalid();
        }

        string? target = null;
        string? configurationPath = null;
        int timeout = DefaultTimeoutMilliseconds;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 2; index < args.Length; index += 2)
        {
            string key = args[index];
            if (!seen.Add(key) || index + 1 >= args.Length)
            {
                return Invalid();
            }

            string value = args[index + 1];
            if (key == "--target" && command.Target && MuMuInstanceValue.IsValid(value))
            {
                target = value;
            }
            else if (key == "--config" && command.Name != "discover" && Path.IsPathFullyQualified(value))
            {
                configurationPath = value;
            }
            else if (key == "--timeout-ms"
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                && parsed is >= 1 and <= MaximumTimeoutMilliseconds)
            {
                timeout = parsed;
            }
            else
            {
                return Invalid();
            }
        }

        return ApplicationResult<AgentRequest>.Success(new(command, target, timeout, configurationPath));
    }

    private static ApplicationResult<AgentRequest> Invalid()
    {
        return ApplicationResult<AgentRequest>.Failure(new()
        {
            Code = ApplicationFailure.InvalidArgument,
            Message = "Недопустимый машинный запрос. Используйте agent discover для описания команд и аргументов.",
        });
    }
}

internal sealed record AgentPayload(object? Data, ApplicationFailure? Failure = null, string Effect = "none");

internal sealed record AgentEnvelope(
    int SchemaVersion, string CorrelationId, string? Operation, string? Target,
    string Status, int ExitCode, string Effect, object? Failure, object? Data, string? RequestedTarget = null);

internal static class AgentJson
{
    internal const int SchemaVersion = 1;
    internal const int MaximumOutputBytes = 262_144;
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new AgentFailureConverter() },
    };

    internal static AgentEnvelope Envelope(string correlationId, string? operation, string? target, AgentPayload payload,
        string? requestedTarget = null)
    {
        ApplicationFailure? failure = payload.Failure;
        return new(SchemaVersion, correlationId, operation, target,
            failure?.Code switch
            {
                null => "success",
                ApplicationFailure.OperationCancelled => "cancelled",
                ApplicationFailure.TargetBusy => "busy",
                _ => "failure",
            },
            failure is null ? AzurPilotExitCode.Success : AzurPilotExitCode.FromFailure(failure),
            payload.Effect, Failure(failure), payload.Data, requestedTarget);
    }

    internal static object? Failure(ApplicationFailure? failure)
    {
        return failure is null ? null : new
        {
            failure.Code,
            failure.IsRetryable,
            // Сообщения внешних границ могут содержать локальные пути. Машинный contract опирается на code.
            message = "Операция отклонена: " + failure.Code,
            details = failure.Details?.Take(16).ToDictionary(p => Safe(p.Key), p => Safe(p.Value), StringComparer.Ordinal),
        };
    }

    internal static string Safe(string? value)
    {
        return value is null
            ? string.Empty
            : value.Contains(":\\", StringComparison.Ordinal) || value.Contains(":/", StringComparison.Ordinal)
                || value.Contains("\\\\", StringComparison.Ordinal)
            ? "[путь скрыт]"
            : new string([.. value.Take(256).Select(c => char.IsControl(c) ? ' ' : c)]);
    }

    internal static byte[] Serialize(ref AgentEnvelope envelope)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        if (bytes.Length <= MaximumOutputBytes)
        {
            return bytes;
        }

        envelope = Envelope(envelope.CorrelationId, envelope.Operation, envelope.Target, new(null, new()
        {
            Code = ApplicationFailure.AgentOutputLimitExceeded,
            Message = "Превышено ограничение ответа.",
        }, envelope.Effect), envelope.RequestedTarget);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }
}

// Вложенные отказы подготовки/диагностики подчиняются той же безопасной проекции, что envelope.
internal sealed class AgentFailureConverter : JsonConverter<ApplicationFailure>
{
    public override ApplicationFailure Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("Машинный интерфейс не принимает application failures на вход.");

    public override void Write(Utf8JsonWriter writer, ApplicationFailure value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, AgentJson.Failure(value), options);
}
