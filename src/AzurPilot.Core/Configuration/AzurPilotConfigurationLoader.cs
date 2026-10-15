using System.Diagnostics.CodeAnalysis;
using System.Security;
using System.Text.Json;
using AzurPilot.Core.Failures;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Загрузчик пользовательской конфигурации: строгое чтение канонического YAML схемы v3, строгая
/// валидация legacy JSON v1/v2 с нормализацией в памяти и встроенные значения по умолчанию при
/// отсутствии файла.
/// </summary>
/// <remarks>
/// <para>
/// Канонический формат — человекочитаемый YAML (<c>config.yaml</c>) эффективной схемы v3. JSON остаётся
/// только legacy-входом: документ v1 или v2 читается строгим контрактом своей версии и нормализуется к
/// v3 <b>в памяти</b> — файл на диске не перезаписывается, не создаётся и не ремонтируется. Явная
/// миграция в канонический YAML выполняется отдельным владельцем
/// <see cref="AzurPilotConfigurationMigration"/> и только по явному действию оператора.
/// </para>
/// <para>
/// Отсутствие файла (или каталога конфигурации) — валидный сценарий: загрузка успешна, а snapshot помечен
/// источником <see cref="AzurPilotConfigurationSource.BuiltInDefaults"/> и несёт встроенную схему v3.
/// Существующий невалидный файл никогда не подменяется defaults: он даёт ожидаемый отказ
/// <see cref="ApplicationFailure.ConfigurationInvalid"/>, а неподдерживаемая версия схемы — отдельный
/// стабильный отказ <see cref="ApplicationFailure.ConfigurationSchemaUnsupported"/>.
/// </para>
/// <para>
/// Существующий, но нечитаемый файл — тоже отказ, а не подстановка defaults: иначе сбой чтения выглядел
/// бы как отсутствие конфигурации.
/// </para>
/// <para>
/// Ожидаемые отказы возвращаются значением через <see cref="ApplicationResult{T}"/> и не выражаются
/// исключениями: исключением сообщается только ошибка программирования вызывающей стороны (пустой или
/// относительный путь). Отказы конфигурации не retryable: файл должен исправить оператор, повтор той же
/// загрузки ничего не изменит.
/// </para>
/// <para>
/// Конфигурация читается один раз: file watcher и reloadOnChange отсутствуют, поэтому полученный
/// snapshot не меняется при последующем изменении файла.
/// </para>
/// <para>
/// Файл читается как UTF-8; ведущий BOM допускается, потому что относится к кодировке файла, а не к
/// документу. Строгость при этом не ослабляется: BOM не делает невалидный документ валидным.
/// </para>
/// </remarks>
public static class AzurPilotConfigurationLoader
{
    private const string CanonicalYamlExtension = ".yaml";

    private const string ShortYamlExtension = ".yml";

    private const string LegacyJsonExtension = ".json";

    private const string SchemaVersionPropertyName = "schemaVersion";
    private const int MaximumDocumentBytes = 64 * 1024;

    /// <summary>Ведущий BOM файла в UTF-8: допускается при чтении и не является частью документа.</summary>
    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    /// <summary>Загружает конфигурацию из файла по явному абсолютному пути.</summary>
    /// <param name="path">Абсолютный путь к файлу конфигурации.</param>
    /// <returns>
    /// Успешный результат со snapshot: встроенные defaults, если файла нет, либо полностью прочитанный и
    /// провалидированный документ. Формат определяется расширением: <c>.yaml</c>/<c>.yml</c> — строгая
    /// схема v3, <c>.json</c> — legacy-контракт v1 или v2 с нормализацией к v3 в памяти. Отказ с кодом
    /// <see cref="ApplicationFailure.ConfigurationInvalid"/>, если файл существует, но не соответствует
    /// своему контракту или имеет неподдерживаемое расширение, либо с кодом
    /// <see cref="ApplicationFailure.ConfigurationSchemaUnsupported"/>, если версия схемы не
    /// поддерживается. В details отказа присутствует ключ <c>config_path</c>, а для неподдерживаемой
    /// схемы — ещё и <c>schema_version</c> с версией из документа.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Путь пуст, состоит из пробелов или не является абсолютным: это ошибка программирования, а не
    /// ожидаемый отказ загрузки.
    /// </exception>
    public static ApplicationResult<AzurPilotConfigurationSnapshot> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Путь конфигурации «{path}» должен быть абсолютным.", nameof(path));
        }

        if (Directory.Exists(path))
        {
            return InvalidConfiguration($"Путь конфигурации «{path}» указывает на каталог, а не на файл.", path);
        }

        if (!TryResolveFormat(path, out AzurPilotConfigurationFormat format))
        {
            return InvalidConfiguration(
                $"Расширение файла конфигурации «{path}» не поддерживается: канонический формат — "
                + $"«{CanonicalYamlExtension}», legacy-вход — «{LegacyJsonExtension}».",
                path);
        }

        DocumentReadOutcome outcome = TryReadDocument(
            path,
            out ReadOnlyMemory<byte> document,
            out ApplicationFailure? readFailure);

        if (outcome == DocumentReadOutcome.Failed)
        {
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(readFailure!);
        }

        if (outcome == DocumentReadOutcome.Missing)
        {
            // Отсутствие файла — валидный сценарий: приложение стартует на встроенных defaults.
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Success(
                AzurPilotConfigurationSnapshot.FromBuiltInDefaults());
        }

        return format == AzurPilotConfigurationFormat.Yaml
            ? ReadYamlDocument(document, path)
            : ReadLegacyJsonDocument(document, path);
    }

    /// <summary>Загружает конфигурацию по обоим runtime-путям по умолчанию.</summary>
    /// <returns>
    /// Успешный snapshot канонического YAML, legacy JSON (нормализованного к v3 в памяти) либо встроенных
    /// defaults, если нет ни одного файла. Отказ с кодом
    /// <see cref="ApplicationFailure.ConfigurationInvalid"/>, если существуют оба файла: настройки двух
    /// источников не смешиваются и ни один из файлов не выбирается автоматически. В details такого отказа
    /// присутствуют ключи <c>config_path</c> (канонический YAML) и <c>legacy_config_path</c> (legacy JSON).
    /// </returns>
    /// <remarks>
    /// Политика двух источников детерминирована: канонический YAML — единственный поддерживаемый формат
    /// записи, legacy JSON допускается только пока YAML отсутствует. Автоматической миграции при загрузке
    /// не выполняется: перенос настроек — явное действие оператора.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Каталог <c>LocalApplicationData</c> не определён в окружении процесса: runtime-путь вычислить
    /// нельзя.
    /// </exception>
    public static ApplicationResult<AzurPilotConfigurationSnapshot> LoadRuntime()
        => LoadFromSources(
            AzurPilotConfigurationPath.GetDefaultRuntimePath(),
            AzurPilotConfigurationPath.GetLegacyJsonRuntimePath());

    /// <summary>Загружает конфигурацию по явно заданным путям канонического YAML и legacy JSON.</summary>
    /// <param name="canonicalPath">Абсолютный путь канонического YAML-файла.</param>
    /// <param name="legacyJsonPath">Абсолютный путь legacy JSON-файла.</param>
    /// <returns>Тот же результат, что и у <see cref="LoadRuntime"/>, но по переданным путям.</returns>
    /// <remarks>
    /// Это тот же runtime-политика двух источников, что и у <see cref="LoadRuntime"/>, с явными путями:
    /// владелец путей по умолчанию остаётся один, а проверка политики не зависит от пользовательского
    /// <c>%LOCALAPPDATA%</c> конкретной машины. Пути — код-параметр composition, а не пользовательская
    /// опция приложения.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Любой из путей пуст, состоит из пробелов или не является абсолютным: это ошибка программирования,
    /// а не ожидаемый отказ загрузки.
    /// </exception>
    public static ApplicationResult<AzurPilotConfigurationSnapshot> LoadFromSources(
        string canonicalPath,
        string legacyJsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyJsonPath);
        if (!Path.IsPathFullyQualified(canonicalPath))
        {
            throw new ArgumentException(
                $"Путь канонической конфигурации «{canonicalPath}» должен быть абсолютным.",
                nameof(canonicalPath));
        }

        if (!Path.IsPathFullyQualified(legacyJsonPath))
        {
            throw new ArgumentException(
                $"Путь legacy-конфигурации «{legacyJsonPath}» должен быть абсолютным.",
                nameof(legacyJsonPath));
        }

        bool canonicalExists;
        bool legacyExists;
        try
        {
            canonicalExists = SourceExists(canonicalPath);
            legacyExists = SourceExists(legacyJsonPath);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return InvalidConfiguration("Не удалось проверить источники конфигурации: " + exception.Message, canonicalPath);
        }

        if (canonicalExists && legacyExists)
        {
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(
                AzurPilotConfigurationFailures.ConflictingSources(canonicalPath, legacyJsonPath));
        }

        if (canonicalExists)
        {
            return Load(canonicalPath);
        }

        return legacyExists
            ? Load(legacyJsonPath)
            : ApplicationResult<AzurPilotConfigurationSnapshot>.Success(
                AzurPilotConfigurationSnapshot.FromBuiltInDefaults());
    }

    private static bool SourceExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Определяет формат документа по расширению пути.</summary>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <param name="format">Формат, соответствующий расширению.</param>
    /// <returns><see langword="true"/>, если расширение поддерживается.</returns>
    private static bool TryResolveFormat(string path, out AzurPilotConfigurationFormat format)
    {
        string extension = Path.GetExtension(path);

        if (extension.Equals(CanonicalYamlExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(ShortYamlExtension, StringComparison.OrdinalIgnoreCase))
        {
            format = AzurPilotConfigurationFormat.Yaml;
            return true;
        }

        if (extension.Equals(LegacyJsonExtension, StringComparison.OrdinalIgnoreCase))
        {
            format = AzurPilotConfigurationFormat.Json;
            return true;
        }

        format = AzurPilotConfigurationFormat.None;
        return false;
    }

    /// <summary>Читает содержимое файла конфигурации без ведущего BOM.</summary>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <param name="document">Содержимое файла без ведущего BOM.</param>
    /// <param name="failure">Отказ при сбое чтения существующего файла.</param>
    /// <returns>Исход чтения: файла нет, файл прочитан или чтение не удалось.</returns>
    private static DocumentReadOutcome TryReadDocument(
        string path,
        out ReadOnlyMemory<byte> document,
        out ApplicationFailure? failure)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumDocumentBytes)
            {
                document = default;
                failure = AzurPilotConfigurationFailures.Invalid("Файл конфигурации превышает допустимые 64 КиБ.", path);
                return DocumentReadOutcome.Failed;
            }

            byte[] content = new byte[(int)stream.Length];
            stream.ReadExactly(content);
            document = StripByteOrderMark(content);
            failure = null;
            return DocumentReadOutcome.Read;
        }
        catch (FileNotFoundException)
        {
            document = default;
            failure = null;
            return DocumentReadOutcome.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            // Нет и самого каталога конфигурации: это тот же сценарий отсутствия файла.
            document = default;
            failure = null;
            return DocumentReadOutcome.Missing;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            // Файл есть, но не читается: defaults не подставляются, иначе сбой чтения выглядел бы
            // как отсутствие конфигурации.
            document = default;
            failure = AzurPilotConfigurationFailures.Invalid(
                $"Файл конфигурации «{path}» не читается: {exception.Message}",
                path);
            return DocumentReadOutcome.Failed;
        }
    }

    /// <summary>Строго читает канонический YAML-документ эффективной схемы v3.</summary>
    /// <param name="document">Содержимое файла конфигурации без BOM.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Snapshot с source schema v3 либо ожидаемый отказ.</returns>
    private static ApplicationResult<AzurPilotConfigurationSnapshot> ReadYamlDocument(
        ReadOnlyMemory<byte> document,
        string path)
    {
        ApplicationResult<AzurPilotConfiguration> configuration = AzurPilotYamlConfigurationReader.Read(
            document,
            path);
        if (configuration.IsFailure)
        {
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(configuration.FailureInfo!);
        }

        return ApplicationResult<AzurPilotConfigurationSnapshot>.Success(
            AzurPilotConfigurationSnapshot.FromFile(
                path,
                AzurPilotConfigurationFormat.Yaml,
                configuration.Value!,
                AzurPilotConfiguration.CurrentSchemaVersion));
    }

    /// <summary>Строго читает legacy JSON-документ и нормализует его к эффективной схеме в памяти.</summary>
    /// <param name="document">Содержимое файла конфигурации без BOM.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>
    /// Snapshot эффективной схемы с source schema v1 или v2 либо ожидаемый отказ. Файл на диске при этом
    /// не изменяется и не создаётся: нормализация существует только в памяти.
    /// </returns>
    private static ApplicationResult<AzurPilotConfigurationSnapshot> ReadLegacyJsonDocument(
        ReadOnlyMemory<byte> document,
        string path)
    {
        ApplicationResult<int> sourceSchemaVersion = ReadJsonSchemaVersion(document, path);
        if (sourceSchemaVersion.IsFailure)
        {
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(sourceSchemaVersion.FailureInfo!);
        }

        return sourceSchemaVersion.Value switch
        {
            AzurPilotConfigurationV1.LegacySchemaVersion => ReadLegacyV1(document, path),
            AzurPilotConfigurationV2.LegacySchemaVersion => ReadLegacyV2(document, path),
            _ => ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(
                AzurPilotConfigurationFailures.UnsupportedSchema(
                    sourceSchemaVersion.Value,
                    path,
                    "legacy JSON описывает только схемы "
                    + $"{AzurPilotConfigurationV1.LegacySchemaVersion} и "
                    + $"{AzurPilotConfigurationV2.LegacySchemaVersion}, а каноническая схема "
                    + $"{AzurPilotConfiguration.CurrentSchemaVersion} существует только в YAML")),
        };
    }

    /// <summary>Строго читает legacy-документ схемы v1 и нормализует его к эффективной схеме.</summary>
    /// <param name="document">Содержимое файла конфигурации без BOM.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Snapshot эффективной схемы либо отказ <c>configuration_invalid</c>.</returns>
    private static ApplicationResult<AzurPilotConfigurationSnapshot> ReadLegacyV1(
        ReadOnlyMemory<byte> document,
        string path)
    {
        try
        {
            AzurPilotConfigurationV1? legacyConfiguration = JsonSerializer.Deserialize(
                document.Span,
                AzurPilotConfigurationJson.Context.AzurPilotConfigurationV1);

            if (legacyConfiguration is null)
            {
                return InvalidConfiguration($"Файл конфигурации «{path}» не содержит объекта конфигурации.", path);
            }

            return ApplicationResult<AzurPilotConfigurationSnapshot>.Success(
                AzurPilotConfigurationSnapshot.FromFile(
                    path,
                    AzurPilotConfigurationFormat.Json,
                    legacyConfiguration.ToCurrentSchema(),
                    AzurPilotConfigurationV1.LegacySchemaVersion));
        }
        catch (JsonException exception)
        {
            return InvalidConfiguration(
                $"Файл конфигурации «{path}» не соответствует legacy-схеме "
                + $"v{AzurPilotConfigurationV1.LegacySchemaVersion}: {exception.Message}",
                path);
        }
    }

    /// <summary>Строго читает legacy-документ схемы v2 и нормализует его к эффективной схеме.</summary>
    /// <param name="document">Содержимое файла конфигурации без BOM.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Snapshot эффективной схемы либо отказ <c>configuration_invalid</c>.</returns>
    private static ApplicationResult<AzurPilotConfigurationSnapshot> ReadLegacyV2(
        ReadOnlyMemory<byte> document,
        string path)
    {
        try
        {
            AzurPilotConfigurationV2? legacyConfiguration = JsonSerializer.Deserialize(
                document.Span,
                AzurPilotConfigurationJson.Context.AzurPilotConfigurationV2);

            if (legacyConfiguration is null)
            {
                return InvalidConfiguration($"Файл конфигурации «{path}» не содержит объекта конфигурации.", path);
            }

            return ApplicationResult<AzurPilotConfigurationSnapshot>.Success(
                AzurPilotConfigurationSnapshot.FromFile(
                    path,
                    AzurPilotConfigurationFormat.Json,
                    legacyConfiguration.ToCurrentSchema(),
                    AzurPilotConfigurationV2.LegacySchemaVersion));
        }
        catch (JsonException exception)
        {
            return InvalidConfiguration(
                $"Файл конфигурации «{path}» не соответствует legacy-схеме "
                + $"v{AzurPilotConfigurationV2.LegacySchemaVersion}: {exception.Message}",
                path);
        }
    }

    /// <summary>Определяет версию схемы legacy JSON-документа до выбора строгого контракта.</summary>
    /// <param name="document">Содержимое файла конфигурации без BOM.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Версия схемы из документа либо отказ <c>configuration_invalid</c>.</returns>
    /// <remarks>
    /// <para>
    /// Версия схемы читается до строгой десериализации: файл более новой схемы неизбежно содержит новые
    /// секции, и оператор должен получить «версия схемы не поддерживается», а не сообщение о неизвестном
    /// свойстве. На этом этапе документ разбирается как JSON DOM, то есть без строгих правил схемы;
    /// строгую проверку выполняет десериализация выбранного контракта.
    /// </para>
    /// <para>
    /// Повторяющееся свойство в корне документа отклоняется уже здесь: версия выбирает контракт, поэтому
    /// решение обязано приниматься по однозначному документу. Иначе результат зависел бы от того, какое
    /// из повторяющихся значений увидел предварительный разбор, и документ с двумя <c>schemaVersion</c>
    /// мог бы получить отказ «неподдерживаемая версия» вместо «несоответствие схеме». Повторяющиеся
    /// свойства внутри секций по-прежнему отклоняет строгая десериализация — так сохраняется приоритет
    /// «неподдерживаемая версия важнее строгой проверки секций будущей схемы».
    /// </para>
    /// </remarks>
    private static ApplicationResult<int> ReadJsonSchemaVersion(ReadOnlyMemory<byte> document, string path)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(document);
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ApplicationResult<int>.Failure(AzurPilotConfigurationFailures.Invalid(
                    $"Файл конфигурации «{path}» не содержит JSON-объекта в корне.",
                    path));
            }

            if (TryFindDuplicatePropertyName(root, out string? duplicateName))
            {
                return ApplicationResult<int>.Failure(AzurPilotConfigurationFailures.Invalid(
                    $"Файл конфигурации «{path}» содержит повторяющееся свойство «{duplicateName}».",
                    path));
            }

            if (!root.TryGetProperty(SchemaVersionPropertyName, out JsonElement element)
                || element.ValueKind != JsonValueKind.Number
                || !element.TryGetInt32(out int schemaVersion))
            {
                return ApplicationResult<int>.Failure(AzurPilotConfigurationFailures.Invalid(
                    $"Файл конфигурации «{path}» не содержит обязательное целочисленное свойство "
                    + $"{SchemaVersionPropertyName}.",
                    path));
            }

            return ApplicationResult<int>.Success(schemaVersion);
        }
        catch (JsonException exception)
        {
            return ApplicationResult<int>.Failure(AzurPilotConfigurationFailures.Invalid(
                $"Файл конфигурации «{path}» не является корректным JSON: {exception.Message}",
                path));
        }
    }

    /// <summary>Ищет повторяющееся имя свойства в корневом объекте документа.</summary>
    /// <param name="root">Корневой объект документа конфигурации.</param>
    /// <param name="duplicateName">Имя первого повторяющегося свойства.</param>
    /// <returns><see langword="true"/>, если в корне документа есть повторяющееся имя свойства.</returns>
    /// <remarks>
    /// Имена сравниваются с ordinal-семантикой, как и строгая десериализация: разные регистры — это
    /// разные имена свойств.
    /// </remarks>
    private static bool TryFindDuplicatePropertyName(
        JsonElement root,
        [NotNullWhen(true)] out string? duplicateName)
    {
        HashSet<string> propertyNames = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
            {
                duplicateName = property.Name;
                return true;
            }
        }

        duplicateName = null;
        return false;
    }

    private static ApplicationResult<AzurPilotConfigurationSnapshot> InvalidConfiguration(string message, string path)
        => ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(
            AzurPilotConfigurationFailures.Invalid(message, path));

    private static bool IsReadFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException;

    /// <summary>Отбрасывает ведущий UTF-8 BOM: это кодировка файла, а не часть документа.</summary>
    /// <param name="document">Содержимое файла конфигурации.</param>
    /// <returns>Документ без ведущего BOM.</returns>
    private static ReadOnlyMemory<byte> StripByteOrderMark(byte[] document)
        => document.Length >= Utf8ByteOrderMark.Length
            && document.AsSpan(0, Utf8ByteOrderMark.Length).SequenceEqual(Utf8ByteOrderMark)
                ? document.AsMemory(Utf8ByteOrderMark.Length)
                : document;

    /// <summary>Исход чтения файла конфигурации.</summary>
    private enum DocumentReadOutcome
    {
        /// <summary>Файла (или каталога) нет: приложение стартует на встроенных defaults.</summary>
        Missing,

        /// <summary>Содержимое файла прочитано.</summary>
        Read,

        /// <summary>Файл существует, но не читается: загрузка fail-closed.</summary>
        Failed,
    }
}
