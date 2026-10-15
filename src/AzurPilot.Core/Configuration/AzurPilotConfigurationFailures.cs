using System.Globalization;
using AzurPilot.Core.Failures;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Единственный владелец формы конфигурационных отказов: кода, состава details и их ключей.
/// </summary>
/// <remarks>
/// <para>
/// Конфигурацию читают, пишут и мигрируют несколько владельцев (загрузчик, строгий YAML-ридер, запись
/// канонического YAML и миграция), но форма отказа у них одна. Ключи details и код отказа задаются
/// здесь, поэтому второй копии строки <c>config_path</c> или второго набора кодов не появляется.
/// </para>
/// <para>
/// Новый код отказа здесь не заводится: конфликт двух источников — это несоответствие пользовательской
/// конфигурации, то есть существующий <see cref="ApplicationFailure.ConfigurationInvalid"/>, а не новая
/// capability. Отдельный код потребовал бы собственной проекции в код выхода процесса, которой у этого
/// случая нет.
/// </para>
/// </remarks>
internal static class AzurPilotConfigurationFailures
{
    /// <summary>Ключ details с путём файла конфигурации.</summary>
    internal const string ConfigurationPathDetailKey = "config_path";

    /// <summary>Ключ details с путём legacy JSON-файла при конфликте двух источников.</summary>
    internal const string LegacyConfigurationPathDetailKey = "legacy_config_path";

    /// <summary>Ключ details с версией схемы, объявленной неподдерживаемым документом.</summary>
    internal const string SchemaVersionDetailKey = "schema_version";

    /// <summary>Создаёт отказ «существующая конфигурация не соответствует своему контракту».</summary>
    /// <param name="message">Человекочитаемое описание причины.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Отказ <see cref="ApplicationFailure.ConfigurationInvalid"/> с путём файла в details.</returns>
    internal static ApplicationFailure Invalid(string message, string path)
        => new()
        {
            Code = ApplicationFailure.ConfigurationInvalid,
            Message = message,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ConfigurationPathDetailKey] = path,
            },
        };

    /// <summary>Создаёт отказ «версия схемы документа не поддерживается».</summary>
    /// <param name="schemaVersion">Версия схемы, объявленная документом.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <param name="supportedSchemas">Описание поддерживаемых схем для формата этого документа.</param>
    /// <returns>
    /// Отказ <see cref="ApplicationFailure.ConfigurationSchemaUnsupported"/> с путём файла и версией
    /// схемы в details.
    /// </returns>
    internal static ApplicationFailure UnsupportedSchema(int schemaVersion, string path, string supportedSchemas)
        => new()
        {
            Code = ApplicationFailure.ConfigurationSchemaUnsupported,
            Message = $"Версия схемы конфигурации {schemaVersion.ToString(CultureInfo.InvariantCulture)} "
                + $"не поддерживается: {supportedSchemas}.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ConfigurationPathDetailKey] = path,
                [SchemaVersionDetailKey] = schemaVersion.ToString(CultureInfo.InvariantCulture),
            },
        };

    /// <summary>Создаёт отказ «одновременно существуют канонический YAML и legacy JSON».</summary>
    /// <param name="yamlPath">Абсолютный путь канонического YAML-файла.</param>
    /// <param name="jsonPath">Абсолютный путь legacy JSON-файла.</param>
    /// <returns>
    /// Детерминированный отказ <see cref="ApplicationFailure.ConfigurationInvalid"/>: настройки двух
    /// источников не смешиваются и ни один из файлов не выбирается автоматически.
    /// </returns>
    /// <remarks>
    /// Отказ не выбирает «победителя»: он сообщает оператору оба пути и инструкцию по миграции, потому
    /// что решение о переносе настроек принимает человек, а не загрузчик.
    /// </remarks>
    internal static ApplicationFailure ConflictingSources(string yamlPath, string jsonPath)
        => new()
        {
            Code = ApplicationFailure.ConfigurationInvalid,
            Message = $"Найдены оба файла конфигурации: канонический «{yamlPath}» и legacy «{jsonPath}». "
                + "Настройки двух источников не смешиваются и ни один из файлов не выбирается "
                + "автоматически. Перенесите legacy-настройки явной миграцией в канонический YAML "
                + $"(с резервной копией legacy-файла) и удалите «{jsonPath}», после чего повторите запуск.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ConfigurationPathDetailKey] = yamlPath,
                [LegacyConfigurationPathDetailKey] = jsonPath,
            },
        };
}
