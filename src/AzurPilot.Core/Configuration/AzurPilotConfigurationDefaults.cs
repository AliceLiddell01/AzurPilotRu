using Microsoft.Extensions.Logging;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Единственный владелец встроенных значений конфигурации по умолчанию.
/// </summary>
/// <remarks>
/// <para>
/// Встроенная конфигурация существует ровно в одном месте: сценарий «файла нет» и любое будущее
/// использование defaults обязаны обращаться сюда, а не повторять значения. Отдельный example-config
/// файл не создаётся: он стал бы вторым владельцем тех же значений, который пришлось бы синхронизировать.
/// </para>
/// <para>
/// Встроенные значения описывают ту же схему v1, что и файл, и потому проходят те же проверки при
/// загрузке файла с теми же значениями.
/// </para>
/// </remarks>
public static class AzurPilotConfigurationDefaults
{
    /// <summary>Создаёт встроенную конфигурацию по умолчанию.</summary>
    /// <returns>Неизменяемый экземпляр конфигурации схемы v1 со значениями по умолчанию.</returns>
    public static AzurPilotConfiguration Create()
    {
        return new AzurPilotConfiguration
        {
            SchemaVersion = AzurPilotConfiguration.CurrentSchemaVersion,
            Diagnostics = new DiagnosticsConfiguration
            {
                MinimumLevel = LogLevel.Information,
            },
        };
    }
}
