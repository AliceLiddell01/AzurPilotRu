using System.Globalization;
using System.Security;
using System.Text.Json;
using AzurPilot.Core.Failures;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Загрузчик пользовательской конфигурации: строгая валидация существующего файла и встроенные
/// значения по умолчанию при его отсутствии.
/// </summary>
/// <remarks>
/// <para>
/// Отсутствие файла (или каталога конфигурации) — валидный сценарий: загрузка успешна, а snapshot помечен
/// источником <see cref="AzurPilotConfigurationSource.BuiltInDefaults"/>. Существующий невалидный файл
/// никогда не подменяется defaults: он даёт ожидаемый отказ
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
    /// и провалидированный файл. Отказ с кодом <see cref="ApplicationFailure.ConfigurationInvalid"/>,
    /// если файл существует, но не соответствует схеме, либо с кодом
    /// <see cref="ApplicationFailure.ConfigurationSchemaUnsupported"/>, если версия схемы не
    /// поддерживается. В details отказа присутствует ключ <c>config_path</c>, а для неподдерживаемой
    /// схемы — ещё и <c>schema_version</c> с версией из файла.
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

        ApplicationResult<int> schemaVersion = ReadSchemaVersion(document, path);
        if (schemaVersion.IsFailure)
        {
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(schemaVersion.FailureInfo!);
        }

        if (schemaVersion.Value != AzurPilotConfiguration.CurrentSchemaVersion)
        {
            return ApplicationResult<AzurPilotConfigurationSnapshot>.Failure(
                CreateUnsupportedSchemaFailure(schemaVersion.Value, path));
        }

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
                AzurPilotConfigurationSnapshot.FromFile(path, configuration));
        }
        catch (JsonException exception)
        {
            return InvalidConfiguration(
                $"Файл конфигурации «{path}» не соответствует схеме "
                + $"v{AzurPilotConfiguration.CurrentSchemaVersion}: {exception.Message}",
                path);
        }
    }

    private static ApplicationResult<int> ReadSchemaVersion(ReadOnlyMemory<byte> document, string path)
    {
        // Версия схемы читается до строгой десериализации: файл более новой схемы неизбежно содержит
        // новые секции, и оператор должен получить «версия схемы не поддерживается», а не сообщение о
        // неизвестном свойстве. На этом этапе документ разбирается как JSON DOM, то есть без строгих
        // правил схемы; строгую проверку выполняет десериализация ниже.
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(document);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ApplicationResult<int>.Failure(CreateInvalidFailure(
                    $"Файл конфигурации «{path}» не содержит JSON-объекта в корне.",
                    path));
            }

            if (!parsed.RootElement.TryGetProperty(SchemaVersionPropertyName, out JsonElement element)
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
                + $"версию {AzurPilotConfiguration.CurrentSchemaVersion}.",
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
