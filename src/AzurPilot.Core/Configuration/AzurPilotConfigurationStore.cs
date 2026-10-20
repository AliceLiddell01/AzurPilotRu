using System.Security;
using System.Text;
using AzurPilot.Core.Failures;

namespace AzurPilot.Core.Configuration;

/// <summary>Явное сохранение настроек в YAML с валидацией, backup и атомарной заменой.</summary>
public static class AzurPilotConfigurationStore
{
    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

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

    /// <summary>
    /// Точечно задаёт интервалы секции <c>screenshot</c> канонического YAML: комментарии, форматирование и
    /// остальные ключи файла сохраняются. Запись атомарна, с backup, и только если SHA-256 файла совпадает с
    /// ожидаемым.
    /// </summary>
    /// <param name="yamlPath">Абсолютный путь канонического YAML.</param>
    /// <param name="jsonPath">
    /// Абсолютный путь legacy JSON для runtime-политики двух источников либо null, если источник задан явно одним
    /// файлом (как <c>--config</c>).
    /// </param>
    /// <param name="expectedSourceVersion">SHA-256 файла, на котором основано решение, либо null, если файла не было.</param>
    /// <param name="normalIntervalMilliseconds">Новый <c>screenshot.normalIntervalMs</c>.</param>
    /// <param name="combatIntervalMilliseconds">Новый <c>screenshot.combatIntervalMs</c> либо null — значение не меняется.</param>
    /// <param name="backupPath">Путь резервной копии вытесненного файла.</param>
    /// <returns>Повторно прочитанный snapshot либо отказ; исходный файл при отказе не меняется.</returns>
    public static ApplicationResult<AzurPilotConfigurationSnapshot> SaveScreenshotIntervals(
        string yamlPath, string? jsonPath, string? expectedSourceVersion, int normalIntervalMilliseconds,
        int? combatIntervalMilliseconds, out string? backupPath)
        => SaveScreenshotIntervals(yamlPath, jsonPath, expectedSourceVersion, normalIntervalMilliseconds,
            combatIntervalMilliseconds, out backupPath, ConfigurationFileOperations.System);

    internal static ApplicationResult<AzurPilotConfigurationSnapshot> SaveScreenshotIntervals(
        string yamlPath, string? jsonPath, string? expectedSourceVersion, int normalIntervalMilliseconds,
        int? combatIntervalMilliseconds, out string? backupPath, ConfigurationFileOperations files)
    {
        lock (ConfigurationFileOperations.WriteGate)
        {
            backupPath = null;
            ApplicationResult<AzurPilotConfigurationSnapshot> current = Read(yamlPath, jsonPath);
            if (current.IsFailure)
            {
                return current;
            }

            AzurPilotConfigurationSnapshot snapshot = current.Value!;
            if (snapshot.SourceVersion != expectedSourceVersion)
            {
                return Invalid("Конфигурация изменилась после проверки; исходный файл сохранён.", yamlPath);
            }

            if (snapshot.SourceFormat == AzurPilotConfigurationFormat.Json)
            {
                return Invalid("Источник — legacy JSON; сначала выполните явную миграцию в YAML.", yamlPath);
            }

            AzurPilotConfiguration desired = snapshot.Configuration with
            {
                Screenshot = snapshot.Configuration.Screenshot with
                {
                    NormalIntervalMilliseconds = normalIntervalMilliseconds,
                    CombatIntervalMilliseconds = combatIntervalMilliseconds ?? snapshot.Configuration.Screenshot.CombatIntervalMilliseconds,
                },
            };
            if (snapshot.SourceFormat == AzurPilotConfigurationFormat.None)
            {
                // Файла нет — сохранять нечего, пишется канонический документ, но только по YAML-пути.
                return (Path.GetExtension(yamlPath).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(yamlPath).Equals(".yml", StringComparison.OrdinalIgnoreCase))
                    ? Replace(yamlPath, jsonPath, snapshot, snapshot,
                        new UTF8Encoding(false).GetBytes(AzurPilotConfigurationYamlWriter.Write(desired)), desired, out backupPath, files)
                    : Invalid("Путь настроек не является YAML; запись невозможна.", yamlPath);
            }

            byte[] original;
            try
            {
                original = File.ReadAllBytes(yamlPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                return Invalid("Не удалось прочитать настройки: " + exception.Message, yamlPath);
            }

            if (ConfigurationFileOperations.Version(original) != expectedSourceVersion)
            {
                return Invalid("Конфигурация изменилась после проверки; исходный файл сохранён.", yamlPath);
            }

            int bom = original.AsSpan().StartsWith(Utf8ByteOrderMark) ? Utf8ByteOrderMark.Length : 0;
            string? edited = AzurPilotConfigurationYamlEdit.SetScreenshotIntervals(
                Encoding.UTF8.GetString(original, bom, original.Length - bom), normalIntervalMilliseconds,
                combatIntervalMilliseconds, out string? editFailure);
            if (edited is null)
            {
                return Invalid("Точечная правка YAML невозможна: " + editFailure, yamlPath);
            }

            byte[] candidate = [.. original.AsSpan(0, bom), .. new UTF8Encoding(false).GetBytes(edited)];
            return Replace(yamlPath, jsonPath, snapshot, snapshot, candidate, desired, out backupPath, files);
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
            return Invalid("Конфигурация изменилась вне приложения. Перезапустите приложение перед сохранением.", yamlPath);
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

        return Replace(yamlPath, jsonPath, current.Value, expected,
            new UTF8Encoding(false).GetBytes(AzurPilotConfigurationYamlWriter.Write(configuration)), configuration,
            out backupPath, files);
    }

    /// <summary>Атомарно заменяет YAML проверенным кандидатом с backup и обнаружением конкурентной записи.</summary>
    private static ApplicationResult<AzurPilotConfigurationSnapshot> Replace(
        string yamlPath, string? jsonPath, AzurPilotConfigurationSnapshot current, AzurPilotConfigurationSnapshot expected,
        byte[] candidate, AzurPilotConfiguration configuration, out string? backupPath, ConfigurationFileOperations files)
    {
        backupPath = null;
        string? directory = Path.GetDirectoryName(yamlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string temporary = Path.Combine(directory, $".config-{Guid.NewGuid():N}.yaml");
        try
        {
            _ = Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporary, candidate);
            ApplicationResult<AzurPilotConfigurationSnapshot> validated = AzurPilotConfigurationLoader.Load(temporary);
            if (validated.IsFailure || validated.Value!.Configuration != configuration)
            {
                return Invalid("Настройки не соответствуют канонической схеме; исходный файл сохранён.");
            }

            // Повторное чтение обнаруживает изменения во время подготовки кандидата.
            ApplicationResult<AzurPilotConfigurationSnapshot> latest = Read(yamlPath, jsonPath);
            if (latest.IsFailure || !latest.Value!.HasSameSource(expected))
            {
                return Invalid("Конфигурация изменилась во время сохранения; исходный файл сохранён.");
            }

            if (current.SourceFormat == AzurPilotConfigurationFormat.Yaml)
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

            ApplicationResult<AzurPilotConfigurationSnapshot> final = Read(yamlPath, jsonPath);
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

    // Явный файл читается так же, как его читает host с --config; иначе действует политика двух источников.
    private static ApplicationResult<AzurPilotConfigurationSnapshot> Read(string yamlPath, string? jsonPath)
        => jsonPath is null ? AzurPilotConfigurationLoader.Load(yamlPath) : AzurPilotConfigurationLoader.LoadFromSources(yamlPath, jsonPath);

    private static ApplicationResult<AzurPilotConfigurationSnapshot> Invalid(string message, string yamlPath)
        => ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(AzurPilotConfigurationFailures.Invalid(message, yamlPath));
}
