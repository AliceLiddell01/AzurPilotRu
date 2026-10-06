using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security;
using System.Text.Json;
using AzurPilot.Core.Failures;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Загрузчик пользовательской конфигурации: строгая валидация существующего файла, in-memory
/// нормализация legacy-схемы v1 и встроенные значения по умолчанию при отсутствии файла.
/// </summary>
/// <remarks>
/// <para>
/// Отсутствие файла (или каталога конфигурации) — валидный сценарий: загрузка успешна, а snapshot помечен
/// источником <see cref="AzurPilotConfigurationSource.BuiltInDefaults"/> и несёт встроенную схему v2.
/// Существующий невалидный файл никогда не подменяется defaults: он даёт ожидаемый отказ
/// <see cref="ApplicationFailure.ConfigurationInvalid"/>, а неподдерживаемая версия схемы — отдельный
/// стабильный отказ <see cref="ApplicationFailure.ConfigurationSchemaUnsupported"/>.
/// </para>
/// <para>
/// Поддерживаются две source schema, и каждая проверяется строго своим контрактом: эффективная схема v2
/// (<see cref="AzurPilotConfiguration"/>) и legacy-схема v1
/// (<see cref="AzurPilotConfigurationV1"/>). Документ v1 допускается как legacy-вход и нормализуется к v2
/// только в памяти: файл не перезаписывается, не создаётся и не ремонтируется, а migration/repair/update
/// команды у загрузчика не появляются.
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
/// JSON-документу. Строгость при этом не ослабляется: BOM не делает невалидный документ валидным.
/// </para>
/// </remarks>
public static class AzurPilotConfigurationLoader
{
    private const string ConfigurationPathDetailKey = "config_path";

    private const string SchemaVersionDetailKey = "schema_version";

    private const string SchemaVersionPropertyName = "schemaVersion";

    /// <summary>Ведущий BOM файла в UTF-8: допускается при чтении и не является частью документа.</summary>
    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    /// <summary>Загружает конфигурацию из файла по явному абсолютному пути.</summary>
    /// <param name="path">Абсолютный путь к файлу конфигурации.</param>
    /// <returns>
    /// Успешный результат со snapshot: встроенные defaults, если файла нет, либо полностью прочитанный
    /// и провалидированный файл — эффективной схемы v2 или нормализованной legacy-схемы v1. Отказ с кодом
    /// <see cref="ApplicationFailure.ConfigurationInvalid"/>, если файл существует, но не соответствует
    /// своей схеме, либо с кодом <see cref="ApplicationFailure.ConfigurationSchemaUnsupported"/>, если
    /// версия схемы не поддерживается. В details отказа присутствует ключ <c>config_path</c>, а для
    /// неподдерживаемой схемы — ещё и <c>schema_version</c> с версией из файла.
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

        ReadOnlyMemory<byte> document;
        try
        {
            document = StripByteOrderMark(File.ReadAllBytes(path));
        }
        catch (FileNotFoundException)
        {
            // Отсутствие файла — валидный сценарий: приложение стартует на встроенных defaults.
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Success(
                AzurPilotConfigurationSnapshot.FromBuiltInDefaults());
        }
        catch (DirectoryNotFoundException)
        {
            // Нет и самого каталога конфигурации: это тот же сценарий отсутствия файла.
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Success(
                AzurPilotConfigurationSnapshot.FromBuiltInDefaults());
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            // Файл есть, но не читается: defaults не подставляются, иначе сбой чтения выглядел бы
            // как отсутствие конфигурации.
            return InvalidConfiguration(
                $"Файл конфигурации «{path}» не читается: {exception.Message}",
                path);
        }

        ApplicationResult<int> sourceSchemaVersion = ReadSchemaVersion(document, path);
        if (sourceSchemaVersion.IsFailure)
        {
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(sourceSchemaVersion.FailureInfo!);
        }

        return ReadBySourceSchema(document, sourceSchemaVersion.Value, path);
    }

    /// <summary>Выбирает контракт строгой десериализации по версии схемы документа.</summary>
    /// <param name="document">Содержимое файла конфигурации без BOM.</param>
    /// <param name="sourceSchemaVersion">Версия схемы, объявленная документом.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>
    /// Успешный snapshot эффективной схемы либо отказ: неподдерживаемая версия или документ, не
    /// соответствующий строгому контракту своей версии.
    /// </returns>
    private static ApplicationResult<AzurPilotConfigurationSnapshot> ReadBySourceSchema(
        ReadOnlyMemory<byte> document,
        int sourceSchemaVersion,
        string path)
        => sourceSchemaVersion switch
        {
            AzurPilotConfiguration.CurrentSchemaVersion => ReadCurrentSchema(document, path),
            AzurPilotConfigurationV1.LegacySchemaVersion => ReadLegacySchema(document, path),
            _ => ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(
                CreateUnsupportedSchemaFailure(sourceSchemaVersion, path)),
        };

    /// <summary>Строго читает документ эффективной схемы v2.</summary>
    /// <param name="document">Содержимое файла конфигурации без BOM.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Snapshot с source schema v2 либо отказ <c>configuration_invalid</c>.</returns>
    private static ApplicationResult<AzurPilotConfigurationSnapshot> ReadCurrentSchema(
        ReadOnlyMemory<byte> document,
        string path)
    {
        try
        {
            AzurPilotConfiguration? configuration = JsonSerializer.Deserialize(
                document.Span,
                AzurPilotConfigurationJson.Context.AzurPilotConfiguration);

            if (configuration is null)
            {
                return InvalidConfiguration($"Файл конфигурации «{path}» не содержит объекта конфигурации.", path);
            }

            return ApplicationResult<AzurPilotConfigurationSnapshot>.Success(
                AzurPilotConfigurationSnapshot.FromFile(
                    path,
                    configuration,
                    AzurPilotConfiguration.CurrentSchemaVersion));
        }
        catch (JsonException exception)
        {
            return InvalidConfiguration(
                $"Файл конфигурации «{path}» не соответствует схеме "
                + $"v{AzurPilotConfiguration.CurrentSchemaVersion}: {exception.Message}",
                path);
        }
    }

    /// <summary>Строго читает legacy-документ схемы v1 и нормализует его к эффективной схеме в памяти.</summary>
    /// <param name="document">Содержимое файла конфигурации без BOM.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>
    /// Snapshot эффективной схемы с source schema v1 либо отказ <c>configuration_invalid</c>. Файл на
    /// диске при этом не изменяется и не создаётся: нормализация существует только в памяти.
    /// </returns>
    private static ApplicationResult<AzurPilotConfigurationSnapshot> ReadLegacySchema(
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

    /// <summary>Определяет версию схемы документа до выбора контракта строгой десериализации.</summary>
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
    /// свойства внутри секций по-прежнему отклоняет строгая десериализация — так сохраняется
    /// приоритет «неподдерживаемая версия важнее строгой проверки секций будущей схемы».
    /// </para>
    /// </remarks>
    private static ApplicationResult<int> ReadSchemaVersion(ReadOnlyMemory<byte> document, string path)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(document);
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ApplicationResult<int>.Failure(CreateInvalidFailure(
                    $"Файл конфигурации «{path}» не содержит JSON-объекта в корне.",
                    path));
            }

            if (TryFindDuplicatePropertyName(root, out string? duplicateName))
            {
                return ApplicationResult<int>.Failure(CreateInvalidFailure(
                    $"Файл конфигурации «{path}» содержит повторяющееся свойство «{duplicateName}».",
                    path));
            }

            if (!root.TryGetProperty(SchemaVersionPropertyName, out JsonElement element)
                || element.ValueKind != JsonValueKind.Number
                || !element.TryGetInt32(out int schemaVersion))
            {
                return ApplicationResult<int>.Failure(CreateInvalidFailure(
                    $"Файл конфигурации «{path}» не содержит обязательное целочисленное свойство "
                    + $"{SchemaVersionPropertyName}.",
                    path));
            }

            return ApplicationResult<int>.Success(schemaVersion);
        }
        catch (JsonException exception)
        {
            return ApplicationResult<int>.Failure(CreateInvalidFailure(
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
        => ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(CreateInvalidFailure(message, path));

    private static ApplicationFailure CreateInvalidFailure(string message, string path)
        => new()
        {
            Code = ApplicationFailure.ConfigurationInvalid,
            Message = message,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ConfigurationPathDetailKey] = path,
            },
        };

    private static ApplicationFailure CreateUnsupportedSchemaFailure(int schemaVersion, string path)
        => new()
        {
            Code = ApplicationFailure.ConfigurationSchemaUnsupported,
            Message = $"Версия схемы конфигурации {schemaVersion} не поддерживается: эта сборка понимает "
                + $"версии {AzurPilotConfigurationV1.LegacySchemaVersion} и "
                + $"{AzurPilotConfiguration.CurrentSchemaVersion}.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ConfigurationPathDetailKey] = path,
                [SchemaVersionDetailKey] = schemaVersion.ToString(CultureInfo.InvariantCulture),
            },
        };

    private static bool IsReadFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException;

    /// <summary>Отбрасывает ведущий UTF-8 BOM: это кодировка файла, а не часть JSON-документа.</summary>
    /// <param name="document">Содержимое файла конфигурации.</param>
    /// <returns>Документ без ведущего BOM.</returns>
    private static ReadOnlyMemory<byte> StripByteOrderMark(byte[] document)
        => document.Length >= Utf8ByteOrderMark.Length
            && document.AsSpan(0, Utf8ByteOrderMark.Length).SequenceEqual(Utf8ByteOrderMark)
                ? document.AsMemory(Utf8ByteOrderMark.Length)
                : document;
}
