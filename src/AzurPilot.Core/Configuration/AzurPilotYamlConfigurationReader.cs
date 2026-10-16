using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Строгий ридер канонического YAML-документа эффективной схемы v3.
/// </summary>
/// <remarks>
/// <para>
/// Документ разбирается низкоуровневыми событиями <c>YamlDotNet</c> (<see cref="IParser"/>), а не
/// десериализацией в объект: только так контролируются все решения схемы — состав и порядок ключей,
/// дубликаты, anchor/alias, tags, flow-стиль, merge-keys, число документов в потоке и типы значений.
/// Пакет даёт синтаксис YAML; правила схемы остаются здесь.
/// </para>
/// <para>
/// Ридер ничего не подставляет молча: любой документ либо полностью соответствует схеме v3, либо
/// получает ожидаемый отказ <see cref="ApplicationFailure.ConfigurationInvalid"/>, а документ чужой
/// версии схемы — <see cref="ApplicationFailure.ConfigurationSchemaUnsupported"/>. Исключений наружу не
/// выбрасывается, включая синтаксические ошибки YAML.
/// </para>
/// <para>
/// Необязательные ключи (<c>mumu.instance</c>, <c>diagnostics.minimumLevel</c>) не получают здесь
/// собственной копии значения по умолчанию: результат строится поверх единственного владельца
/// <see cref="AzurPilotConfigurationDefaults"/>, и переопределяются только явно заданные ключи.
/// </para>
/// <para>
/// Грамматика значений не дублируется: <c>mumu.instance</c> проверяет <see cref="MuMuInstanceValue"/>,
/// <c>diagnostics.minimumLevel</c> — <see cref="LogLevelNames"/>, а <c>ui.mode</c> — имена самого
/// <see cref="OperatorUiMode"/>, приведённые к нижнему регистру.
/// </para>
/// </remarks>
internal static class AzurPilotYamlConfigurationReader
{
    /// <summary>Имя корневого узла в сообщениях об отказе.</summary>
    private const string RootLocation = "корень";

    /// <summary>Ключ слияния YAML, запрещённый схемой.</summary>
    private const string MergeKey = "<<";

    /// <summary>Пустой перечень обязательных ключей секции.</summary>
    private static readonly string[] NoRequiredKeys = [];

    /// <summary>Строгий UTF-8 без replacement fallback: некорректная кодировка — отказ, а не «похожий» текст.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly string[] RootKeys = ["schemaVersion", "ui", "mumu", "diagnostics", "logging"];

    private static readonly string[] UiKeys = ["mode"];

    private static readonly string[] MuMuKeys = ["instance"];

    private static readonly string[] DiagnosticsKeys = ["minimumLevel"];

    private static readonly string[] LoggingKeys = ["viewer"];

    private static readonly string[] LogViewerKeys = ["enabled"];

    /// <summary>Литералы, которые YAML разрешает как булево значение, а не как строку.</summary>
    private static readonly FrozenSet<string> BooleanLiterals =
        new[] { "true", "false", "yes", "no", "on", "off" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Читает канонический YAML-документ схемы v3.</summary>
    /// <param name="document">Содержимое файла конфигурации без ведущего BOM.</param>
    /// <param name="path">Абсолютный путь файла конфигурации для details и сообщений.</param>
    /// <returns>
    /// Успешную конфигурацию эффективной схемы v3 либо ожидаемый отказ: неподдерживаемая версия схемы,
    /// нарушение строгой схемы, некорректная кодировка или синтаксис YAML.
    /// </returns>
    internal static ApplicationResult<AzurPilotConfiguration> Read(ReadOnlyMemory<byte> document, string path)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(document.Span);
        }
        catch (DecoderFallbackException exception)
        {
            return Invalid($"файл не является корректным UTF-8 ({exception.Message}).", path);
        }

        IReadOnlyList<ParsingEvent> events;
        try
        {
            events = ReadEvents(text);
        }
        catch (YamlException exception)
        {
            return Invalid($"файл не является корректным YAML ({exception.Message}).", path);
        }

        return Analyze(events, path);
    }

    /// <summary>Собирает поток событий YAML-документа.</summary>
    /// <param name="text">Содержимое файла как текст UTF-8.</param>
    /// <returns>События потока в порядке следования.</returns>
    /// <exception cref="YamlException">Документ синтаксически некорректен.</exception>
    private static List<ParsingEvent> ReadEvents(string text)
    {
        Parser parser = new(new StringReader(text));
        List<ParsingEvent> events = [];
        while (parser.MoveNext())
        {
            events.Add(parser.Current!);
        }

        return events;
    }

    /// <summary>Проверяет структуру потока и строит конфигурацию по разобранному документу.</summary>
    /// <param name="events">События потока YAML.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Успешную конфигурацию схемы v3 либо ожидаемый отказ.</returns>
    private static ApplicationResult<AzurPilotConfiguration> Analyze(
        IReadOnlyList<ParsingEvent> events,
        string path)
    {
        EventCursor cursor = new(events);

        if (!cursor.HasNext || cursor.Peek() is not StreamStart)
        {
            return Invalid("файл не содержит YAML-документа.", path);
        }

        _ = cursor.Read();

        // Поток без документа — это пустой файл или файл из одних комментариев: конфигурации в нём нет,
        // и подставлять defaults вместо явного документа нельзя.
        if (!cursor.HasNext || cursor.Peek() is not DocumentStart documentStart)
        {
            return Invalid(
                "файл не содержит YAML-документа (пустой файл или файл из одних комментариев).",
                path);
        }

        // Явный маркер «---» отвергается вместе с multi-document: канонический документ — ровно один
        // неявно начатый документ, поэтому разделитель виден сразу.
        if (!documentStart.IsImplicit)
        {
            return Invalid(
                "поток содержит явный маркер начала документа «---» или несколько документов; "
                + "поддерживается ровно один документ без явных маркеров.",
                path);
        }

        _ = cursor.Read();

        ApplicationResult<YamlNode> root = MaterializeValue(cursor, path, RootLocation);
        if (root.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(root.FailureInfo!);
        }

        if (!cursor.HasNext || cursor.Peek() is not DocumentEnd)
        {
            return Invalid("поток содержит несколько YAML-документов; поддерживается ровно один.", path);
        }

        _ = cursor.Read();

        if (cursor.HasNext && cursor.Peek() is not StreamEnd)
        {
            return Invalid("поток содержит несколько YAML-документов; поддерживается ровно один.", path);
        }

        return root.Value is YamlMapping rootMapping
            ? Validate(rootMapping, path)
            : Invalid("корень документа должен быть mapping-ом YAML с ключами схемы v3.", path);
    }

    /// <summary>Читает узел значения по текущему событию потока.</summary>
    /// <param name="cursor">Курсор событий потока.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <param name="location">Путь ключа в документе для сообщений об отказе.</param>
    /// <returns>Разобранный узел либо ожидаемый отказ.</returns>
    private static ApplicationResult<YamlNode> MaterializeValue(EventCursor cursor, string path, string location)
    {
        if (!cursor.HasNext)
        {
            return Invalid<YamlNode>($"у значения «{location}» отсутствует узел YAML.", path);
        }

        return cursor.Peek() switch
        {
            MappingStart mappingStart => MaterializeMapping(cursor, mappingStart, path, location),
            SequenceStart => Invalid<YamlNode>(
                $"значение «{location}» задано последовательностью: схема v3 не содержит списков.",
                path),
            Scalar scalar => MaterializeScalar(cursor, scalar, path, location),
            AnchorAlias => Invalid<YamlNode>(
                $"значение «{location}» задано alias-ссылкой «*»: anchor и alias в схеме v3 запрещены.",
                path),
            _ => Invalid<YamlNode>($"у значения «{location}» неожиданный узел YAML.", path),
        };
    }

    /// <summary>Читает скалярное значение и отвергает запрещённые anchor и tag.</summary>
    /// <param name="cursor">Курсор событий потока.</param>
    /// <param name="scalar">Событие скаляра.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <param name="location">Путь ключа в документе для сообщений об отказе.</param>
    /// <returns>Разобранный скаляр либо ожидаемый отказ.</returns>
    private static ApplicationResult<YamlNode> MaterializeScalar(
        EventCursor cursor,
        Scalar scalar,
        string path,
        string location)
    {
        if (scalar.Anchor != AnchorName.Empty)
        {
            return Invalid<YamlNode>(
                $"значение «{location}» помечено anchor-ом «&»: anchor и alias в схеме v3 запрещены.",
                path);
        }

        if (scalar.Tag != TagName.Empty || (!scalar.IsPlainImplicit && !scalar.IsQuotedImplicit))
        {
            return Invalid<YamlNode>(
                $"значение «{location}» задано с явным tag-ом «!!»: tags в схеме v3 запрещены.",
                path);
        }

        _ = cursor.Read();
        return ApplicationResult<YamlNode>.Success(new YamlScalar(scalar.Value, scalar.Style));
    }

    /// <summary>Читает mapping и отвергает запрещённые формы его записи.</summary>
    /// <param name="cursor">Курсор событий потока.</param>
    /// <param name="start">Событие начала mapping-а.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <param name="location">Путь ключа в документе для сообщений об отказе.</param>
    /// <returns>Разобранный mapping либо ожидаемый отказ.</returns>
    private static ApplicationResult<YamlNode> MaterializeMapping(
        EventCursor cursor,
        MappingStart start,
        string path,
        string location)
    {
        if (start.Anchor != AnchorName.Empty)
        {
            return Invalid<YamlNode>(
                $"секция «{location}» помечена anchor-ом «&»: anchor и alias в схеме v3 запрещены.",
                path);
        }

        if (start.Tag != TagName.Empty)
        {
            return Invalid<YamlNode>(
                $"секция «{location}» задана с явным tag-ом «!!»: tags в схеме v3 запрещены.",
                path);
        }

        if (start.Style == MappingStyle.Flow)
        {
            return Invalid<YamlNode>(
                $"секция «{location}» записана в flow-стиле «{{...}}»: поддерживается только блочный стиль.",
                path);
        }

        _ = cursor.Read();

        List<KeyValuePair<string, YamlNode>> entries = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        while (true)
        {
            if (!cursor.HasNext)
            {
                return Invalid<YamlNode>($"секция «{location}» не завершена.", path);
            }

            if (cursor.Peek() is MappingEnd)
            {
                _ = cursor.Read();
                break;
            }

            if (cursor.Peek() is AnchorAlias)
            {
                return Invalid<YamlNode>(
                    $"ключ в секции «{location}» задан alias-ссылкой «*»: anchor и alias в схеме v3 запрещены.",
                    path);
            }

            if (cursor.Peek() is not Scalar key)
            {
                return Invalid<YamlNode>(
                    $"ключом в секции «{location}» может быть только скаляр, а не mapping или список.",
                    path);
            }

            if (key.Anchor != AnchorName.Empty || key.Tag != TagName.Empty)
            {
                return Invalid<YamlNode>(
                    $"ключ «{key.Value}» в секции «{location}» задан с anchor-ом или tag-ом: "
                    + "anchor, alias и tags в схеме v3 запрещены.",
                    path);
            }

            if (string.Equals(key.Value, MergeKey, StringComparison.Ordinal))
            {
                return Invalid<YamlNode>(
                    $"секция «{location}» использует merge-key «{MergeKey}»: слияние ключей в схеме v3 запрещено.",
                    path);
            }

            if (!names.Add(key.Value))
            {
                return Invalid<YamlNode>(
                    $"секция «{location}» содержит повторяющийся ключ «{key.Value}».",
                    path);
            }

            _ = cursor.Read();

            ApplicationResult<YamlNode> value = MaterializeValue(
                cursor,
                path,
                ChildLocation(location, key.Value));
            if (value.IsFailure)
            {
                return value;
            }

            entries.Add(new KeyValuePair<string, YamlNode>(key.Value, value.Value!));
        }

        return ApplicationResult<YamlNode>.Success(new YamlMapping(entries));
    }

    /// <summary>Проверяет состав ключей и значения документа схемы v3.</summary>
    /// <param name="root">Корневой mapping документа.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Успешную конфигурацию схемы v3 либо ожидаемый отказ.</returns>
    private static ApplicationResult<AzurPilotConfiguration> Validate(YamlMapping root, string path)
    {
        // Версия схемы читается до строгой проверки секций: документ чужой версии неизбежно содержит
        // другие ключи, и оператор должен увидеть неподдерживаемую версию, а не неизвестный ключ.
        if (!root.TryGet("schemaVersion", out YamlNode? schemaVersionNode))
        {
            return Invalid("в корне документа отсутствует обязательный ключ «schemaVersion».", path);
        }

        ApplicationResult<int> schemaVersion = ReadSchemaVersion(schemaVersionNode, path);
        if (schemaVersion.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(schemaVersion.FailureInfo!);
        }

        if (schemaVersion.Value != AzurPilotConfiguration.CurrentSchemaVersion)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(
                AzurPilotConfigurationFailures.UnsupportedSchema(
                    schemaVersion.Value,
                    path,
                    $"YAML-документ описывает только схему {AzurPilotConfiguration.CurrentSchemaVersion}"));
        }

        ApplicationResult<bool> rootShape = ValidateKeys(root, RootKeys, RootKeys, RootLocation, path);
        if (rootShape.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(rootShape.FailureInfo!);
        }

        ApplicationResult<YamlMapping> ui = ReadSection(root, "ui", "ui", UiKeys, UiKeys, path);
        if (ui.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(ui.FailureInfo!);
        }

        ApplicationResult<YamlMapping> muMu = ReadSection(root, "mumu", "mumu", MuMuKeys, NoRequiredKeys, path);
        if (muMu.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(muMu.FailureInfo!);
        }

        ApplicationResult<YamlMapping> diagnostics = ReadSection(
            root,
            "diagnostics",
            "diagnostics",
            DiagnosticsKeys,
            NoRequiredKeys,
            path);
        if (diagnostics.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(diagnostics.FailureInfo!);
        }

        ApplicationResult<YamlMapping> logging = ReadSection(
            root,
            "logging",
            "logging",
            LoggingKeys,
            LoggingKeys,
            path);
        if (logging.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(logging.FailureInfo!);
        }

        ApplicationResult<YamlMapping> viewer = ReadSection(
            logging.Value!,
            "viewer",
            "logging.viewer",
            LogViewerKeys,
            LogViewerKeys,
            path);
        if (viewer.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(viewer.FailureInfo!);
        }

        ApplicationResult<OperatorUiMode> mode = ReadUiMode(ui.Value!, path);
        if (mode.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(mode.FailureInfo!);
        }

        ApplicationResult<bool> viewerEnabled = ReadBool(viewer.Value!, "enabled", "logging.viewer.enabled", path);
        if (viewerEnabled.IsFailure)
        {
            return ApplicationResult<AzurPilotConfiguration>.Failure(viewerEnabled.FailureInfo!);
        }

        // Необязательные ключи не читаются вовсе: их значения приходят от владельца встроенных defaults,
        // поэтому второй копии значения по умолчанию здесь не появляется.
        AzurPilotConfiguration defaults = AzurPilotConfigurationDefaults.Create();

        string instance = defaults.MuMu.Instance;
        if (muMu.Value!.Contains("instance"))
        {
            ApplicationResult<string> readInstance = ReadMuMuInstance(muMu.Value!, path);
            if (readInstance.IsFailure)
            {
                return ApplicationResult<AzurPilotConfiguration>.Failure(readInstance.FailureInfo!);
            }

            instance = readInstance.Value!;
        }

        LogLevel minimumLevel = defaults.Diagnostics.MinimumLevel;
        if (diagnostics.Value!.Contains("minimumLevel"))
        {
            ApplicationResult<LogLevel> readMinimumLevel = ReadMinimumLevel(diagnostics.Value!, path);
            if (readMinimumLevel.IsFailure)
            {
                return ApplicationResult<AzurPilotConfiguration>.Failure(readMinimumLevel.FailureInfo!);
            }

            minimumLevel = readMinimumLevel.Value;
        }

        return ApplicationResult<AzurPilotConfiguration>.Success(defaults with
        {
            Ui = defaults.Ui with { Mode = mode.Value! },
            MuMu = defaults.MuMu with { Instance = instance },
            Diagnostics = defaults.Diagnostics with { MinimumLevel = minimumLevel },
            Logging = defaults.Logging with
            {
                Viewer = defaults.Logging.Viewer with { Enabled = viewerEnabled.Value },
            },
        });
    }

    /// <summary>Читает версию схемы, объявленную документом.</summary>
    /// <param name="node">Узел значения ключа <c>schemaVersion</c>.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Версию схемы либо ожидаемый отказ.</returns>
    private static ApplicationResult<int> ReadSchemaVersion(YamlNode node, string path)
    {
        if (node is not YamlScalar { Style: ScalarStyle.Plain } scalar)
        {
            return Invalid<int>(
                "значение «schemaVersion» должно быть целым числом YAML, записанным без кавычек.",
                path);
        }

        if (!int.TryParse(scalar.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int version))
        {
            return Invalid<int>(
                $"значение «schemaVersion» «{scalar.Value}» не является целым числом.",
                path);
        }

        return ApplicationResult<int>.Success(version);
    }

    /// <summary>Читает секцию документа и проверяет её состав.</summary>
    /// <param name="parent">Mapping, в котором лежит секция.</param>
    /// <param name="key">Имя ключа секции.</param>
    /// <param name="location">Путь секции в документе для сообщений об отказе.</param>
    /// <param name="allowedKeys">Полный перечень допустимых ключей секции.</param>
    /// <param name="requiredKeys">Ключи, которые обязаны присутствовать.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Mapping секции либо ожидаемый отказ.</returns>
    /// <remarks>
    /// Пустое значение секции (<c>mumu:</c> без вложенных ключей) — это YAML null, а не mapping. Такая
    /// запись означает «ни один ключ секции не задан»: она читается как пустой mapping, после чего
    /// отсутствие обязательных ключей сообщается отдельным отказом. Секция со скалярным значением —
    /// по-прежнему неверный тип.
    /// </remarks>
    private static ApplicationResult<YamlMapping> ReadSection(
        YamlMapping parent,
        string key,
        string location,
        string[] allowedKeys,
        string[] requiredKeys,
        string path)
    {
        if (!parent.TryGet(key, out YamlNode? node))
        {
            return Invalid<YamlMapping>($"секция «{location}» отсутствует.", path);
        }

        YamlMapping? mapping = node switch
        {
            YamlMapping section => section,
            YamlScalar { IsNull: true } => YamlMapping.Empty,
            _ => null,
        };

        if (mapping is null)
        {
            return Invalid<YamlMapping>(
                $"секция «{location}» должна быть mapping-ом YAML, а не скаляром или списком.",
                path);
        }

        ApplicationResult<bool> shape = ValidateKeys(mapping, allowedKeys, requiredKeys, location, path);
        return shape.IsFailure
            ? ApplicationResult<YamlMapping>.Failure(shape.FailureInfo!)
            : ApplicationResult<YamlMapping>.Success(mapping);
    }

    /// <summary>Проверяет состав ключей секции и наличие обязательных.</summary>
    /// <param name="mapping">Mapping секции.</param>
    /// <param name="allowedKeys">Полный перечень допустимых ключей секции.</param>
    /// <param name="requiredKeys">Ключи, которые обязаны присутствовать.</param>
    /// <param name="location">Путь секции в документе для сообщений об отказе.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Успех либо ожидаемый отказ.</returns>
    private static ApplicationResult<bool> ValidateKeys(
        YamlMapping mapping,
        string[] allowedKeys,
        string[] requiredKeys,
        string location,
        string path)
    {
        foreach (KeyValuePair<string, YamlNode> entry in mapping.Entries)
        {
            if (Array.IndexOf(allowedKeys, entry.Key) < 0)
            {
                return Invalid<bool>(
                    $"секция «{location}» содержит неизвестный ключ «{entry.Key}»; "
                    + $"допустимы: {string.Join(", ", allowedKeys)}.",
                    path);
            }
        }

        foreach (string required in requiredKeys)
        {
            if (!mapping.Contains(required))
            {
                return Invalid<bool>(
                    $"секция «{location}» не содержит обязательный ключ «{required}».",
                    path);
            }
        }

        return ApplicationResult<bool>.Success(true);
    }

    /// <summary>Читает значение <c>ui.mode</c>.</summary>
    /// <param name="ui">Mapping секции <c>ui</c>.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Режим интерфейса либо ожидаемый отказ.</returns>
    private static ApplicationResult<OperatorUiMode> ReadUiMode(YamlMapping ui, string path)
    {
        ApplicationResult<string> text = ReadString(ui, "mode", "ui.mode", path);
        if (text.IsFailure)
        {
            return ApplicationResult<OperatorUiMode>.Failure(text.FailureInfo!);
        }

        if (!OperatorUiModeNames.TryParse(text.Value!, out OperatorUiMode mode))
        {
            return Invalid<OperatorUiMode>(
                $"значение «ui.mode» «{text.Value}» недопустимо; допустимы в нижнем регистре: "
                + $"{string.Join(", ", OperatorUiModeNames.All)}.",
                path);
        }

        return ApplicationResult<OperatorUiMode>.Success(mode);
    }

    /// <summary>Читает значение <c>mumu.instance</c>.</summary>
    /// <param name="muMu">Mapping секции <c>mumu</c> с обязательным ключом <c>instance</c>.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Значение выбора экземпляра либо ожидаемый отказ.</returns>
    /// <remarks>
    /// Грамматику значения проверяет <see cref="MuMuInstanceValue"/>, поэтому вторая копия перечня
    /// допустимых форм не появляется. Отсутствие ключа обрабатывает вызывающая сторона: значение по
    /// умолчанию приходит от владельца встроенных defaults, а не подставляется здесь.
    /// </remarks>
    private static ApplicationResult<string> ReadMuMuInstance(YamlMapping muMu, string path)
    {
        ApplicationResult<string> text = ReadString(muMu, "instance", "mumu.instance", path);
        if (text.IsFailure)
        {
            return text;
        }

        if (!MuMuInstanceValue.IsValid(text.Value))
        {
            return Invalid<string>(
                $"значение «mumu.instance» «{text.Value}» недопустимо; ожидается "
                + $"«{MuMuInstanceValue.AutoValue}» либо «{MuMuInstanceValue.ProviderPrefix}<номер>» "
                + "без ведущих нулей и без знака.",
                path);
        }

        return text;
    }

    /// <summary>Читает значение <c>diagnostics.minimumLevel</c>.</summary>
    /// <param name="diagnostics">Mapping секции <c>diagnostics</c> с обязательным ключом <c>minimumLevel</c>.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Уровень логирования либо ожидаемый отказ.</returns>
    private static ApplicationResult<LogLevel> ReadMinimumLevel(YamlMapping diagnostics, string path)
    {
        ApplicationResult<string> text = ReadString(diagnostics, "minimumLevel", "diagnostics.minimumLevel", path);
        if (text.IsFailure)
        {
            return ApplicationResult<LogLevel>.Failure(text.FailureInfo!);
        }

        if (!LogLevelNames.TryParse(text.Value!, out LogLevel level))
        {
            return Invalid<LogLevel>(
                $"значение «diagnostics.minimumLevel» «{text.Value}» неизвестно; допустимы имена: "
                + $"{string.Join(", ", LogLevelNames.All)}.",
                path);
        }

        return ApplicationResult<LogLevel>.Success(level);
    }

    /// <summary>Читает строковое значение ключа.</summary>
    /// <param name="mapping">Mapping, в котором лежит ключ.</param>
    /// <param name="key">Имя ключа.</param>
    /// <param name="location">Путь ключа в документе для сообщений об отказе.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Строковое значение либо ожидаемый отказ.</returns>
    private static ApplicationResult<string> ReadString(
        YamlMapping mapping,
        string key,
        string location,
        string path)
    {
        if (!mapping.TryGet(key, out YamlNode? node))
        {
            return Invalid<string>($"ключ «{location}» отсутствует.", path);
        }

        if (node is not YamlScalar scalar)
        {
            return Invalid<string>(
                $"значение «{location}» должно быть строкой YAML, а не секцией или списком.",
                path);
        }

        if (scalar.Style is not (ScalarStyle.Plain or ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted))
        {
            return Invalid<string>(
                $"значение «{location}» должно быть обычной строкой, а не блочным скаляром.",
                path);
        }

        if (scalar.IsNull || (scalar.Style == ScalarStyle.Plain && IsNonStringPlainScalar(scalar.Value)))
        {
            return Invalid<string>(
                $"значение «{location}» должно быть строкой YAML, а не числом, булевым значением или null.",
                path);
        }

        return ApplicationResult<string>.Success(scalar.Value);
    }

    /// <summary>Читает булево значение ключа.</summary>
    /// <param name="mapping">Mapping, в котором лежит ключ.</param>
    /// <param name="key">Имя ключа.</param>
    /// <param name="location">Путь ключа в документе для сообщений об отказе.</param>
    /// <param name="path">Абсолютный путь файла конфигурации.</param>
    /// <returns>Булево значение либо ожидаемый отказ.</returns>
    /// <remarks>
    /// Принимаются ровно литералы <c>true</c> и <c>false</c>: кавычки делают значение строкой, а не
    /// булевым, поэтому строковая форма отвергается как неверный тип.
    /// </remarks>
    private static ApplicationResult<bool> ReadBool(
        YamlMapping mapping,
        string key,
        string location,
        string path)
    {
        if (!mapping.TryGet(key, out YamlNode? node))
        {
            return Invalid<bool>($"ключ «{location}» отсутствует.", path);
        }

        if (node is not YamlScalar { Style: ScalarStyle.Plain } scalar)
        {
            return Invalid<bool>(
                $"значение «{location}» должно быть булевым YAML-значением true или false без кавычек.",
                path);
        }

        return scalar.Value switch
        {
            "true" => ApplicationResult<bool>.Success(true),
            "false" => ApplicationResult<bool>.Success(false),
            _ => Invalid<bool>(
                $"значение «{location}» должно быть ровно true или false в нижнем регистре, "
                + $"а получено «{scalar.Value}».",
                path),
        };
    }

    /// <summary>Определяет, разрешается ли plain-скаляр YAML в нестроковое значение.</summary>
    /// <param name="value">Текст plain-скаляра.</param>
    /// <returns>
    /// <see langword="true"/>, если YAML читает такое значение как null, булево или число.
    /// </returns>
    /// <remarks>
    /// Проверка нужна для ключей, объявленных строковыми: <c>ui.mode: 1</c> — это число, а не строка,
    /// поэтому документ должен получить отказ о типе, а не сообщение о неизвестном значении строки.
    /// </remarks>
    private static bool IsNonStringPlainScalar(string value)
    {
        if (value.Length == 0 || BooleanLiterals.Contains(value))
        {
            return true;
        }

        if (string.Equals(value, "~", StringComparison.Ordinal)
            || value.Equals("null", StringComparison.OrdinalIgnoreCase)
            || value.Equals(".nan", StringComparison.OrdinalIgnoreCase)
            || value.Equals(".inf", StringComparison.OrdinalIgnoreCase)
            || value.Equals("-.inf", StringComparison.OrdinalIgnoreCase)
            || value.Equals("+.inf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        ReadOnlySpan<char> digits = value.AsSpan();
        if (!digits.IsEmpty && digits[0] is '+' or '-')
        {
            digits = digits[1..];
        }

        if (digits.IsEmpty)
        {
            return false;
        }

        bool hasDigit = false;
        foreach (char character in digits)
        {
            if (char.IsAsciiDigit(character))
            {
                hasDigit = true;
                continue;
            }

            if (character is '.' or 'e' or 'E' or '_')
            {
                continue;
            }

            return false;
        }

        return hasDigit;
    }

    /// <summary>Строит путь дочернего ключа для сообщений об отказе.</summary>
    /// <param name="location">Путь родительского узла.</param>
    /// <param name="key">Имя дочернего ключа.</param>
    /// <returns>Путь вида <c>logging.viewer</c>.</returns>
    private static string ChildLocation(string location, string key)
        => string.Equals(location, RootLocation, StringComparison.Ordinal) ? key : location + "." + key;

    private static ApplicationResult<AzurPilotConfiguration> Invalid(string message, string path)
        => ApplicationResult<AzurPilotConfiguration>.Failure(
            AzurPilotConfigurationFailures.Invalid(
                $"Файл конфигурации «{path}» не соответствует схеме "
                + $"v{AzurPilotConfiguration.CurrentSchemaVersion}: {message}",
                path));

    private static ApplicationResult<T> Invalid<T>(string message, string path)
        => ApplicationResult<T>.Failure(
            AzurPilotConfigurationFailures.Invalid(
                $"Файл конфигурации «{path}» не соответствует схеме "
                + $"v{AzurPilotConfiguration.CurrentSchemaVersion}: {message}",
                path));

    /// <summary>Курсор по событиям потока YAML.</summary>
    private sealed class EventCursor
    {
        private readonly IReadOnlyList<ParsingEvent> _events;
        private int _index;

        internal EventCursor(IReadOnlyList<ParsingEvent> events) => _events = events;

        internal bool HasNext => _index < _events.Count;

        internal ParsingEvent? Peek() => _index < _events.Count ? _events[_index] : null;

        internal ParsingEvent Read() => _events[_index++];
    }

    /// <summary>Разобранный узел YAML: скаляр или mapping.</summary>
    private abstract record YamlNode;

    /// <summary>Скалярное значение с сохранением стиля записи.</summary>
    /// <param name="Value">Текст значения.</param>
    /// <param name="Style">Стиль записи скаляра.</param>
    private sealed record YamlScalar(string Value, ScalarStyle Style) : YamlNode
    {
        /// <summary>Признак того, что значение записано как YAML null, то есть ключ без значения.</summary>
        internal bool IsNull => Style == ScalarStyle.Plain && Value.Length == 0;
    }

    /// <summary>Mapping с сохранением порядка ключей документа.</summary>
    /// <param name="Entries">Пары «ключ → узел» в порядке документа.</param>
    private sealed record YamlMapping(IReadOnlyList<KeyValuePair<string, YamlNode>> Entries) : YamlNode
    {
        /// <summary>Пустой mapping: секция записана без вложенных ключей.</summary>
        internal static YamlMapping Empty { get; } = new([]);

        /// <summary>Проверяет наличие ключа.</summary>
        /// <param name="key">Имя ключа.</param>
        /// <returns><see langword="true"/>, если ключ присутствует в mapping-е.</returns>
        internal bool Contains(string key) => TryGet(key, out _);

        /// <summary>Возвращает узел по имени ключа.</summary>
        /// <param name="key">Имя ключа.</param>
        /// <param name="node">Узел значения.</param>
        /// <returns><see langword="true"/>, если ключ присутствует в mapping-е.</returns>
        internal bool TryGet(string key, [NotNullWhen(true)] out YamlNode? node)
        {
            foreach (KeyValuePair<string, YamlNode> entry in Entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    node = entry.Value;
                    return true;
                }
            }

            node = null;
            return false;
        }
    }
}
