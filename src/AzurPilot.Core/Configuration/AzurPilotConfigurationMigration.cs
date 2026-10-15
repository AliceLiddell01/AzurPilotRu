using System.Globalization;
using System.Security;
using System.Text;
using AzurPilot.Core.Failures;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Владелец контролируемой миграции legacy JSON-конфигурации в канонический YAML.
/// </summary>
/// <remarks>
/// <para>
/// Миграция выполняется только по явному действию оператора и никогда не запускается при загрузке
/// конфигурации: загрузчик остаётся читающим, а единственный путь создания <c>config.yaml</c> — эта
/// операция. Порядок шагов фиксирован и проверяем:
/// </para>
/// <list type="number">
/// <item>legacy JSON читается прежним строгим контрактом своей версии — невалидный файл не мигрируется;</item>
/// <item>канонический YAML записывается атомарно: сначала во временный файл в том же каталоге, затем
/// <c>File.Move</c> без замены существующего файла, поэтому чужой YAML не перезаписывается;</item>
/// <item>записанный YAML повторно читается загрузчиком и сверяется с ожидаемым snapshot;</item>
/// <item>только после успешной проверки legacy-файл перемещается в резервную копию
/// (<c>config.json.bak-&lt;UTC timestamp&gt;</c>): перемещение и есть backup, отдельного копирования нет;</item>
/// <item>любая неудача на шагах записи, проверки или перемещения возвращает состояние «существует только
/// legacy JSON»: записанный YAML удаляется, а legacy-файл остаётся нетронутым.</item>
/// </list>
/// <para>
/// Итог успешной миграции — ровно один действующий источник (<c>config.yaml</c>) плюс резервная копия
/// legacy-документа. Поэтому runtime-политика «оба файла существуют» после миграции не срабатывает, и
/// оператор одним действием получает рабочее состояние.
/// </para>
/// <para>
/// Существующий <c>config.yaml</c> не перезаписывается: миграция отказывает, потому что решение о замене
/// уже действующей конфигурации принимает оператор, а не инструмент переноса.
/// </para>
/// <para>
/// Ожидаемые отказы возвращаются значением через <see cref="ApplicationResult{T}"/>: исключением
/// сообщается только ошибка программирования вызывающей стороны (пустой или относительный путь).
/// </para>
/// </remarks>
public static class AzurPilotConfigurationMigration
{
    /// <summary>Суффикс имени резервной копии legacy-файла.</summary>
    private const string BackupSuffix = ".bak-";

    /// <summary>Формат UTC-метки времени в имени резервной копии.</summary>
    private const string BackupTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>Предел подбора свободного имени резервной копии в пределах одной секунды.</summary>
    private const int MaxBackupNameAttempts = 100;

    /// <summary>Переносит legacy JSON-конфигурацию в канонический YAML по явным путям.</summary>
    /// <param name="legacyJsonPath">Абсолютный путь существующего legacy JSON-файла.</param>
    /// <param name="yamlPath">Абсолютный путь канонического YAML-файла, который будет создан.</param>
    /// <returns>
    /// Успешный результат с подтверждённым snapshot канонического YAML и путём резервной копии либо
    /// ожидаемый отказ: legacy-файла нет или он невалиден, канонический YAML уже существует, либо
    /// записанный документ не подтверждён повторным чтением. Отказы несут путь файла в details под
    /// ключом <c>config_path</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Любой из путей пуст, состоит из пробелов или не является абсолютным: это ошибка программирования,
    /// а не ожидаемый отказ миграции.
    /// </exception>
    public static ApplicationResult<AzurPilotConfigurationMigrationResult> MigrateLegacyJsonToYaml(
        string legacyJsonPath,
        string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);
        if (!Path.IsPathFullyQualified(legacyJsonPath))
        {
            throw new ArgumentException(
                $"Путь legacy-конфигурации «{legacyJsonPath}» должен быть абсолютным.",
                nameof(legacyJsonPath));
        }

        if (!Path.IsPathFullyQualified(yamlPath))
        {
            throw new ArgumentException(
                $"Путь канонической конфигурации «{yamlPath}» должен быть абсолютным.",
                nameof(yamlPath));
        }

        // Наличие источника проверяется первым: после успешной миграции legacy-файла уже нет, и повторное
        // действие обязано получить понятное «переносить нечего», а не сообщение о существующем YAML.
        if (!File.Exists(legacyJsonPath))
        {
            return Failure(
                $"Legacy JSON «{legacyJsonPath}» не найден: переносить нечего.",
                legacyJsonPath);
        }

        if (File.Exists(yamlPath))
        {
            return Failure(
                $"Канонический YAML «{yamlPath}» уже существует: миграция не перезаписывает действующую "
                + "конфигурацию. Удалите или переименуйте файл, если перенос действительно нужен.",
                yamlPath);
        }

        ApplicationResult<AzurPilotConfigurationSnapshot> legacy =
            AzurPilotConfigurationLoader.Load(legacyJsonPath);
        if (legacy.IsFailure)
        {
            // Невалидный legacy-документ не мигрируется: причина отказа сообщается как есть.
            return ApplicationResult<AzurPilotConfigurationMigrationResult>.Failure(legacy.FailureInfo!);
        }

        AzurPilotConfigurationSnapshot source = legacy.Value!;
        if (source.SourceFormat != AzurPilotConfigurationFormat.Json)
        {
            return Failure(
                $"Источник миграции «{legacyJsonPath}» не является legacy JSON-документом: "
                + "переносятся только схемы, прочитанные как legacy-вход.",
                legacyJsonPath);
        }

        string document = AzurPilotConfigurationYamlWriter.Write(source.Configuration);
        if (!TryWriteAtomically(yamlPath, document, out string? writeError))
        {
            return Failure(
                $"Не удалось записать канонический YAML «{yamlPath}»: {writeError}",
                yamlPath);
        }

        ApplicationResult<AzurPilotConfigurationSnapshot> verified =
            AzurPilotConfigurationLoader.Load(yamlPath);
        bool confirmed = verified.IsSuccess
            && verified.Value!.SourceFormat == AzurPilotConfigurationFormat.Yaml
            && verified.Value!.Configuration == source.Configuration;

        if (!confirmed)
        {
            // Записанный документ не подтверждён: он удаляется, чтобы runtime-состояние вернулось к
            // «существует только legacy JSON». Legacy-файл миграция не изменяла.
            TryDelete(yamlPath);
            return Failure(
                $"Записанный канонический YAML «{yamlPath}» не подтверждён повторным чтением: "
                + "загрузчик не воспроизвёл ожидаемую конфигурацию. Файл удалён, legacy-конфигурация не "
                + "изменена.",
                yamlPath);
        }

        string backupPath;
        try
        {
            backupPath = MoveToBackup(legacyJsonPath);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // Перенос legacy-файла не удался: подтверждённый YAML удаляется, иначе действующими
            // источниками остались бы оба файла и runtime-политика отказывала бы на запуске.
            TryDelete(yamlPath);
            return Failure(
                $"Канонический YAML записан и подтверждён, но legacy-файл «{legacyJsonPath}» не удалось "
                + $"перенести в резервную копию: {exception.Message}. Записанный YAML удалён, "
                + "legacy-конфигурация не изменена.",
                legacyJsonPath);
        }

        return ApplicationResult<AzurPilotConfigurationMigrationResult>.Success(
            new AzurPilotConfigurationMigrationResult
            {
                Snapshot = verified.Value!,
                BackupFilePath = backupPath,
            });
    }

    /// <summary>Переносит legacy JSON-конфигурацию по runtime-путям по умолчанию.</summary>
    /// <returns>
    /// Успешный результат с подтверждённым snapshot канонического YAML и путём резервной копии либо
    /// ожидаемый отказ. Пути берутся у владельца <see cref="AzurPilotConfigurationPath"/>, поэтому
    /// второй их копии не заводится.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Каталог <c>LocalApplicationData</c> не определён в окружении процесса: runtime-пути вычислить
    /// нельзя.
    /// </exception>
    public static ApplicationResult<AzurPilotConfigurationMigrationResult> MigrateRuntimeLegacyJsonToYaml()
        => MigrateLegacyJsonToYaml(
            AzurPilotConfigurationPath.GetLegacyJsonRuntimePath(),
            AzurPilotConfigurationPath.GetDefaultRuntimePath());

    /// <summary>Переносит legacy-файл в резервную копию рядом с ним.</summary>
    /// <param name="legacyJsonPath">Абсолютный путь legacy JSON-файла.</param>
    /// <returns>Абсолютный путь резервной копии, в которую перенесён legacy-файл.</returns>
    /// <remarks>
    /// <para>
    /// Перемещение и есть резервная копия: отдельного копирования нет, поэтому после успешной миграции
    /// действующим источником остаётся ровно один канонический YAML. Вызывающая сторона выполняет
    /// перемещение только после подтверждения записанного YAML.
    /// </para>
    /// <para>
    /// Имя копии строится из UTC-метки времени и, при совпадении в пределах одной секунды, порядкового
    /// суффикса: существующая копия никогда не перезаписывается.
    /// </para>
    /// </remarks>
    private static string MoveToBackup(string legacyJsonPath)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString(BackupTimestampFormat, CultureInfo.InvariantCulture);
        string candidate = legacyJsonPath + BackupSuffix + timestamp;

        for (int attempt = 1; File.Exists(candidate); attempt++)
        {
            if (attempt > MaxBackupNameAttempts)
            {
                throw new IOException(
                    $"Не удалось подобрать свободное имя резервной копии рядом с «{legacyJsonPath}».");
            }

            candidate = legacyJsonPath
                + BackupSuffix
                + timestamp
                + "-"
                + attempt.ToString(CultureInfo.InvariantCulture);
        }

        File.Move(legacyJsonPath, candidate);
        return candidate;
    }

    /// <summary>Атомарно записывает канонический YAML-документ.</summary>
    /// <param name="yamlPath">Абсолютный путь целевого файла.</param>
    /// <param name="document">Текст канонического документа.</param>
    /// <param name="error">Сообщение о причине неудачи записи.</param>
    /// <returns><see langword="true"/>, если документ записан.</returns>
    /// <remarks>
    /// Документ сначала пишется во временный файл в том же каталоге, а затем переносится на место
    /// целевого имени без замены чужого файла. Читатель не видит частично записанный документ, а каталог создаётся при
    /// необходимости. Временный файл удаляется при любой неудаче.
    /// </remarks>
    private static bool TryWriteAtomically(string yamlPath, string document, out string? error)
    {
        error = null;

        string? directory = Path.GetDirectoryName(yamlPath);
        if (string.IsNullOrEmpty(directory))
        {
            error = "каталог целевого файла не определён";
            return false;
        }

        string tempPath = Path.Combine(
            directory,
            Path.GetFileName(yamlPath)
                + "."
                + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
                + ".tmp");

        try
        {
            _ = Directory.CreateDirectory(directory);
            File.WriteAllText(tempPath, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, yamlPath, overwrite: false);
            return true;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            error = exception.Message;
            TryDelete(tempPath);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // Неудачное удаление не подменяет исходный отказ: файл остаётся диагностируемым фактом.
        }
    }

    private static ApplicationResult<AzurPilotConfigurationMigrationResult> Failure(string message, string path)
        => ApplicationResult<AzurPilotConfigurationMigrationResult>.Failure(
            AzurPilotConfigurationFailures.Invalid(message, path));

    private static bool IsFileFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException;
}
