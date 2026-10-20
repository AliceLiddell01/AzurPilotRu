using System.Text.Json;
using System.Text.Json.Serialization;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Configuration;

namespace AzurPilot.Windows.Capture;

/// <summary>Результат выбора <c>auto</c>, доказанный измерением на главном экране точного target.</summary>
/// <param name="Fingerprint">Отпечаток условий: identity, версии MuMu/Android, зависимостей и ABI.</param>
/// <param name="Method">Выбранный метод.</param>
/// <param name="Width">Ширина кадров измерения.</param>
/// <param name="Height">Высота кадров измерения.</param>
/// <param name="MeasuredAtUtc">Момент измерения.</param>
/// <param name="RunId">Идентификатор отчёта измерения.</param>
/// <param name="P95Milliseconds">p95 end-to-end выбранного метода.</param>
public sealed record ScreenshotCalibration(
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("measuredAtUtc")] DateTimeOffset MeasuredAtUtc,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("p95Ms")] double P95Milliseconds);

/// <summary>
/// Кэш выбора <c>auto</c> вне пользовательской конфигурации: <c>%LOCALAPPDATA%\AzurPilot\capture-calibration.json</c>.
/// Запись выполняет только измерение методов; изменение отпечатка делает запись недействительной.
/// </summary>
public sealed class ScreenshotCalibrationStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AzurPilotConfigurationPath.ApplicationFolderName, "capture-calibration.json");
    private readonly Lock _gate = new();

    /// <summary>Возвращает действительную запись для отпечатка либо null.</summary>
    public ScreenshotCalibration? Find(string fingerprint)
    {
        lock (_gate)
        {
            return Read().SingleOrDefault(entry => string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal)
                && ScreenshotMethodNames.TryParse(entry.Method, out _));
        }
    }

    /// <summary>Сохраняет запись, заменяя прежнюю для того же отпечатка.</summary>
    public void Save(ScreenshotCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        lock (_gate)
        {
            List<ScreenshotCalibration> entries = [.. Read().Where(entry => entry.Fingerprint != calibration.Fingerprint)];
            entries.Add(calibration);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    /// <summary>Удаляет запись отпечатка (например, после смены разрешения).</summary>
    public void Invalidate(string fingerprint)
    {
        lock (_gate)
        {
            List<ScreenshotCalibration> entries = Read();
            if (entries.RemoveAll(entry => entry.Fingerprint == fingerprint) > 0)
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(entries));
            }
        }
    }

    private List<ScreenshotCalibration> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<ScreenshotCalibration>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённый кэш равен отсутствию калибровки: auto вернётся к ADB, а не к догадке.
            return [];
        }
    }
}
