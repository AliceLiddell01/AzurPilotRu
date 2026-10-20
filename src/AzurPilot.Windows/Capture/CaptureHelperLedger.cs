using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Configuration;

namespace AzurPilot.Windows.Capture;

/// <summary>Ресурс устройства, созданный транспортом: helper-процесс, forward или reverse одного target.</summary>
/// <param name="Kind">helper, forward или reverse.</param>
/// <param name="Value">PID helper-а или порт forward/reverse.</param>
/// <param name="Marker">Маркер cmdline helper-а; для портов пуст.</param>
/// <param name="OwnerProcessId">PID процесса AzurPilot, создавшего ресурс.</param>
/// <param name="OwnerStartedUtc">Момент запуска процесса-владельца (защита от переиспользования PID).</param>
internal sealed record CaptureHelperResource(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] int Value,
    [property: JsonPropertyName("marker")] string Marker,
    [property: JsonPropertyName("ownerProcessId")] int OwnerProcessId,
    [property: JsonPropertyName("ownerStartedUtc")] DateTimeOffset OwnerStartedUtc);

/// <summary>
/// Локальный журнал ресурсов устройства по точному endpoint. Ресурс записывается до использования и
/// удаляется после освобождения; ресурсы завершившегося процесса освобождаются при следующем открытии
/// транспорта на том же endpoint. Чужие процессы и ресурсы других target не затрагиваются.
/// </summary>
internal sealed class CaptureHelperLedger
{
    private static readonly DateTimeOffset CurrentProcessStarted = new(Process.GetCurrentProcess().StartTime.ToUniversalTime());
    private readonly string _path;

    internal CaptureHelperLedger(AndroidEndpoint endpoint, string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AzurPilotConfigurationPath.ApplicationFolderName, "capture-helpers");
        string name = endpoint.ToString().Replace(':', '_').Replace('.', '_');
        _path = Path.Combine(directory, name + ".json");
    }

    internal static CaptureHelperResource Create(string kind, int value, string marker = "")
        => new(kind, value, marker, Environment.ProcessId, CurrentProcessStarted);

    internal void Add(CaptureHelperResource resource) => Update(list => list.Add(resource));

    internal void Remove(CaptureHelperResource resource) => Update(list => list.RemoveAll(item => item == resource));

    /// <summary>Извлекает ресурсы, владелец которых больше не работает; они удаляются из журнала.</summary>
    internal IReadOnlyList<CaptureHelperResource> TakeOrphans()
    {
        List<CaptureHelperResource> orphans = [];
        Update(list =>
        {
            orphans.AddRange(list.Where(item => !IsAlive(item)));
            _ = list.RemoveAll(item => !IsAlive(item));
        });
        return orphans;
    }

    private static bool IsAlive(CaptureHelperResource resource)
    {
        if (resource.OwnerProcessId == Environment.ProcessId)
        {
            return resource.OwnerStartedUtc == CurrentProcessStarted;
        }

        try
        {
            using Process process = Process.GetProcessById(resource.OwnerProcessId);
            return Math.Abs((process.StartTime.ToUniversalTime() - resource.OwnerStartedUtc.UtcDateTime).TotalSeconds) < 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void Update(Action<List<CaptureHelperResource>> change)
    {
        // Журнал — best effort: недоступный файл не блокирует захват, но и не теряет текущий ресурс молча
        // для этого процесса — освобождение в Dispose выполняется независимо от журнала.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using FileStream stream = new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                List<CaptureHelperResource> list = stream.Length == 0 ? []
                    : JsonSerializer.Deserialize<List<CaptureHelperResource>>(stream) ?? [];
                change(list);
                stream.SetLength(0);
                JsonSerializer.Serialize(stream, list);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(25);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return;
            }
        }
    }
}
