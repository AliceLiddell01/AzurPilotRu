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
        => Save(yamlPath, jsonPath, expected, configuration, out backupPath, ConfigurationFileOperations.System);

    internal static ApplicationResult<AzurPilotConfigurationSnapshot> Save(
        string yamlPath, string jsonPath, AzurPilotConfigurationSnapshot expected,
        AzurPilotConfiguration configuration, out string? backupPath, ConfigurationFileOperations files)
    {
        lock (ConfigurationFileOperations.WriteGate)
        {
            return SaveCore(yamlPath, jsonPath, expected, configuration, out backupPath, files);
        }
    }

    private static ApplicationResult<AzurPilotConfigurationSnapshot> SaveCore(
        string yamlPath, string jsonPath, AzurPilotConfigurationSnapshot expected,
        AzurPilotConfiguration configuration, out string? backupPath, ConfigurationFileOperations files)
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

        if (!current.Value!.HasSameSource(expected))
        {
            return Invalid("Конфигурация изменилась вне приложения. Перезапустите приложение перед сохранением.");
        }

        if (current.Value.SourceFormat == AzurPilotConfigurationFormat.Json)
        {
            ApplicationResult<AzurPilotConfigurationMigrationResult> migrated =
                AzurPilotConfigurationMigration.SaveLegacyJsonAsYaml(jsonPath, yamlPath, current.Value, configuration, files);
            if (migrated.IsFailure)
            {
                return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(migrated.FailureInfo!);
            }

            backupPath = migrated.Value!.BackupFilePath;
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Success(migrated.Value.Snapshot);
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

            // Повторное чтение обнаруживает изменения во время подготовки кандидата.
            ApplicationResult<AzurPilotConfigurationSnapshot> latest = AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);
            if (latest.IsFailure || !latest.Value!.HasSameSource(expected))
            {
                return Invalid("Конфигурация изменилась во время сохранения; исходный файл сохранён.");
            }

            if (current.Value.SourceFormat == AzurPilotConfigurationFormat.Yaml)
            {
                // Запрещаем запись в открытый файл, разрешая атомарную замену имени. Чужая замена
                // имени всё ещё возможна: File.Replace сохраняет именно вытесненные байты в backup.
                using FileStream source = new(yamlPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                backupPath = yamlPath + $".bak-{Guid.NewGuid():N}";
                files.Replace(temporary, yamlPath, backupPath);
                if (ConfigurationFileOperations.VersionOfFile(backupPath) != expected.SourceVersion)
                {
                    string recovery = yamlPath + $".conflict-{Guid.NewGuid():N}";
                    try
                    {
                        // Обратная замена тоже сохраняет вытесненный файл: даже последующая чужая
                        // запись не теряется при восстановлении обнаруженной версии.
                        File.Replace(backupPath, yamlPath, recovery);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
                    {
                        return Invalid($"Конфликт сохранения. Внешние правки сохранены в «{backupPath}». "
                            + $"Не удалось восстановить источник: {exception.Message}. Проверьте YAML перед перезапуском.");
                    }

                    return Invalid($"Конфликт сохранения: внешняя версия восстановлена; вытесненный файл сохранён в «{recovery}». "
                        + "Повторно прочитайте настройки перед сохранением.");
                }
            }
            else
            {
                files.Move(temporary, yamlPath);
            }

            ApplicationResult<AzurPilotConfigurationSnapshot> final = AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);
            return final.IsSuccess && final.Value!.SourceVersion == validated.Value.SourceVersion
                ? final : Invalid("Источники изменились после записи. Проверьте YAML и резервную копию перед повтором.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Invalid("Не удалось сохранить настройки: " + exception.Message
                + (backupPath is null ? string.Empty : $". Путь резервной копии: «{backupPath}»."));
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
