using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Единственный владелец встроенных значений конфигурации по умолчанию схемы v3.
/// </summary>
/// <remarks>
/// <para>
/// Встроенная конфигурация существует ровно в одном месте: сценарий «файла нет», нормализация
/// legacy-документов v1 и v2 к схеме v3 и чтение YAML обязаны обращаться сюда, а не повторять значения.
/// Чтение YAML строит результат поверх этих defaults и переопределяет только явно заданные ключи,
/// поэтому необязательные ключи (<c>mumu.instance</c>, <c>diagnostics.minimumLevel</c>) не дублируют
/// значения по умолчанию в ридере.
/// </para>
/// <para>
/// Отдельный example-config файл не создаётся: он стал бы вторым владельцем тех же значений, который
/// пришлось бы синхронизировать.
/// </para>
/// <para>
/// Встроенные значения описывают ту же схему v3, что и канонический YAML-файл, и потому проходят те же
/// проверки при загрузке файла с теми же значениями.
/// </para>
/// </remarks>
public static class AzurPilotConfigurationDefaults
{
    /// <summary>Создаёт встроенную конфигурацию по умолчанию.</summary>
    /// <returns>Неизменяемый экземпляр конфигурации схемы v3 со значениями по умолчанию.</returns>
    /// <remarks>
    /// <para>
    /// Значения по умолчанию: <c>ui.mode = normal</c>, <c>mumu.instance = "auto"</c>,
    /// <c>diagnostics.minimumLevel = Information</c>, <c>logging.viewer.enabled = false</c>.
    /// </para>
    /// <para>
    /// <c>mumu.instance = "auto"</c> не означает «взять первый попавшийся экземпляр»: разрешение выбора
    /// принадлежит <c>AzurPilot.Core.MuMu</c> и при нуле подходящих экземпляров сообщает «не найден», а
    /// при двух и более — «неоднозначен».
    /// </para>
    /// </remarks>
    public static AzurPilotConfiguration Create()
    {
        return new AzurPilotConfiguration
        {
            SchemaVersion = AzurPilotConfiguration.CurrentSchemaVersion,
            Ui = new UiConfiguration
            {
                Mode = OperatorUiMode.Normal,
            },
            MuMu = new MuMuConfiguration
            {
                Instance = MuMuInstanceValue.AutoValue,
            },
            Diagnostics = new DiagnosticsConfiguration
            {
                MinimumLevel = LogLevel.Information,
            },
            Logging = new LoggingConfiguration
            {
                Viewer = new LogViewerConfiguration
                {
                    Enabled = false,
                },
            },
        };
    }
}
