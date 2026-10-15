using System.Security;
using System.Text;
using AzurPilot.Core.Failures;

namespace AzurPilot.Core.Configuration;

/// <summary>Явное сохранение настроек в YAML с валидацией, backup и атомарной заменой.</summary>
public static class AzurPilotConfigurationStore
{
    /// <summary>Сохраняет настройки, если источник не изменился с момента последнего чтения.</summary>
    /// <param name="yamlPath">Абсолютный путь канонического YAML.</param>
    /// <param name="jsonPath">Абсолютный путь legacy JSON.</param>
    /// <param name="expected">Последний известный snapshot источника.</param>
    /// <param name="configuration">Желаемые настройки следующего запуска.</param>
    /// <returns>Повторно прочитанный snapshot либо отказ без подстановки defaults.</returns>
    public static ApplicationResult<AzurPilotConfigurationSnapshot> Save(
        string yamlPath,
        string jsonPath,
        AzurPilotConfigurationSnapshot expected,
        AzurPilotConfiguration configuration)
        => Save(yamlPath, jsonPath, expected, configuration, out _);

    /// <summary>Сохраняет настройки и возвращает путь фактически созданной резервной копии.</summary>
    public static ApplicationResult<AzurPilotConfigurationSnapshot> Save(
        string yamlPath, string jsonPath, AzurPilotConfigurationSnapshot expected,
        AzurPilotConfiguration configuration, out string? backupPath)
    {
        backupPath = null;
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(configuration);
        ApplicationResult<AzurPilotConfigurationSnapshot> current =
            AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);
        if (current.IsFailure)
        {
            return current;
        }

        if (current.Value!.Configuration != expected.Configuration
            || current.Value.SourceFormat != expected.SourceFormat
            || current.Value.Source != expected.Source)
        {
            return Invalid("Конфигурация изменилась вне приложения. Перезапустите приложение перед сохранением.");
        }

        string? directory = Path.GetDirectoryName(yamlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string temporary = Path.Combine(directory, $".config-{Guid.NewGuid():N}.yaml");
        try
        {
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, AzurPilotConfigurationYamlWriter.Write(configuration), new UTF8Encoding(false));
            ApplicationResult<AzurPilotConfigurationSnapshot> validated = AzurPilotConfigurationLoader.Load(temporary);
            if (validated.IsFailure || validated.Value!.Configuration != configuration)
            {
                return Invalid("Настройки не соответствуют канонической схеме; исходный файл сохранён.");
            }

            if (current.Value.SourceFormat == AzurPilotConfigurationFormat.Json)
            {
                ApplicationResult<AzurPilotConfigurationMigrationResult> migrated =
                    AzurPilotConfigurationMigration.MigrateLegacyJsonToYaml(jsonPath, yamlPath);
                if (migrated.IsFailure)
                {
                    return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(migrated.FailureInfo!);
                }

                backupPath = migrated.Value!.BackupFilePath;
            }
            else if (current.Value.SourceFormat == AzurPilotConfigurationFormat.Yaml)
            {
                backupPath = yamlPath + $".bak-{Guid.NewGuid():N}";
                File.Copy(yamlPath, backupPath, overwrite: false);
            }

            File.Move(temporary, yamlPath, overwrite: current.Value.Source != AzurPilotConfigurationSource.BuiltInDefaults);
            return AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Invalid("Не удалось сохранить настройки: " + exception.Message);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Ошибка удаления временного файла не подменяет исходный результат сохранения.
            }
        }

        ApplicationResult<AzurPilotConfigurationSnapshot> Invalid(string message)
        {
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(
                AzurPilotConfigurationFailures.Invalid(message, yamlPath));
        }
    }
}
