using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurPilot.AndroidAcceptance;

internal static class ScreenshotAcceptanceJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
