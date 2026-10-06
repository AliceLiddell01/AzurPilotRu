using System.Text.Json;
using System.Text.RegularExpressions;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Parser вывода <c>MuMuManager</c> в Windows-local DTO.
/// </summary>
/// <remarks>
/// <para>
/// Parser намеренно узкий и fail-closed: предметом контракта является точная форма ответа, а не
/// «похожесть» на неё. Если ответ не разобран целиком, операция объявляется неуспешной — parser не
/// достраивает отсутствующие поля, не приводит типы и не подставляет значения по умолчанию. Догадка по
/// историческим версиям запрещена: непонятная форма ответа означает, что control surface не
/// подтверждена.
/// </para>
/// <para>
/// Identity экземпляра берётся только из поля <c>index</c> (и ключа перечисления, который обязан с ним
/// совпадать). Display name, позиция в ответе и порядок перечисления идентификатором не являются.
/// </para>
/// <para>
/// Поля, от которых adapter не зависит, игнорируются: они не могут повлиять ни на identity, ни на
/// состояние. Обязательными являются только те поля, из которых эти два решения выводятся.
/// </para>
/// <para>
/// Код выхода процесса — evidence, а не признак успеха: там, где провайдер сообщает код ошибки в теле
/// ответа, он обязан совпадать с кодом выхода, иначе форма считается нераспознанной.
/// </para>
/// </remarks>
public static class MuMuManagerResponseParser
{
    private const string VersionOperation = "version";
    private const string InstanceQueryOperation = "info --vmindex <index>";
    private const string EnumerationOperation = "info --vmindex all";
    private const string ControlOperation = "control --vmindex <index> <operation>";

    /// <summary>
    /// Форма строки версии: две-четыре числовые части, разделённые точками.
    /// </summary>
    /// <remarks>
    /// Проверяется только структура строки: конкретное значение версии не является разрешением на
    /// поддержку, поэтому allowlist версий здесь не заводится.
    /// </remarks>
    private static readonly Regex VersionShapePattern = new(
        @"^\d+(\.\d+){1,3}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Разбирает ответ подкоманды <c>version</c>.</summary>
    /// <param name="standardOutput">Захваченный stdout процесса.</param>
    /// <param name="exitCode">Код выхода процесса.</param>
    /// <returns>Строка версии player либо application-level отказ.</returns>
    public static ApplicationResult<string> ParseVersion(string standardOutput, int exitCode)
    {
        if (TryParseRootObject(standardOutput) is not JsonDocument document)
        {
            return ApplicationResult<string>.Failure(Unrecognized(VersionOperation, exitCode, null));
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (exitCode != 0
                || root.TryGetProperty(MuMuManagerJsonNames.ErrorCode, out _)
                || !TryReadString(root, MuMuManagerJsonNames.Version, out string version)
                || !VersionShapePattern.IsMatch(version))
            {
                return ApplicationResult<string>.Failure(
                    Unrecognized(VersionOperation, exitCode, ReadProviderErrorCode(root)));
            }

            return ApplicationResult<string>.Success(version);
        }
    }

    /// <summary>Разбирает ответ запроса сведений об одном экземпляре.</summary>
    /// <param name="id">Запрошенная identity экземпляра.</param>
    /// <param name="standardOutput">Захваченный stdout процесса.</param>
    /// <param name="exitCode">Код выхода процесса.</param>
    /// <returns>Сведения об экземпляре либо application-level отказ.</returns>
    public static ApplicationResult<MuMuInstanceQueryResult> ParseInstanceQuery(
        MuMuInstanceId id,
        string standardOutput,
        int exitCode)
    {
        if (TryParseRootObject(standardOutput) is not JsonDocument document)
        {
            return ApplicationResult<MuMuInstanceQueryResult>.Failure(
                Unrecognized(InstanceQueryOperation, exitCode, null));
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.TryGetProperty(MuMuManagerJsonNames.ErrorCode, out _))
            {
                MuMuProviderError? providerError = ReadProviderError(root);

                if (providerError is null || providerError.Code == 0 || providerError.Code != exitCode)
                {
                    return ApplicationResult<MuMuInstanceQueryResult>.Failure(
                        Unrecognized(InstanceQueryOperation, exitCode, providerError?.Code));
                }

                return ApplicationResult<MuMuInstanceQueryResult>.Success(new MuMuInstanceQueryResult
                {
                    Id = id,
                    ProviderError = providerError,
                });
            }

            if (exitCode != 0)
            {
                return ApplicationResult<MuMuInstanceQueryResult>.Failure(
                    Unrecognized(InstanceQueryOperation, exitCode, null));
            }

            MuMuInstanceInfo? info = ReadInstance(root, id);

            if (info is null)
            {
                return ApplicationResult<MuMuInstanceQueryResult>.Failure(
                    Unrecognized(InstanceQueryOperation, exitCode, null));
            }

            return ApplicationResult<MuMuInstanceQueryResult>.Success(new MuMuInstanceQueryResult
            {
                Id = id,
                Instance = info,
            });
        }
    }

    /// <summary>Разбирает ответ перечисления всех экземпляров.</summary>
    /// <param name="standardOutput">Захваченный stdout процесса.</param>
    /// <param name="exitCode">Код выхода процесса.</param>
    /// <returns>Перечисление экземпляров либо application-level отказ.</returns>
    public static ApplicationResult<MuMuInstanceEnumeration> ParseInstanceEnumeration(
        string standardOutput,
        int exitCode)
    {
        if (TryParseRootObject(standardOutput) is not JsonDocument document)
        {
            return ApplicationResult<MuMuInstanceEnumeration>.Failure(
                Unrecognized(EnumerationOperation, exitCode, null));
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (exitCode != 0 || root.TryGetProperty(MuMuManagerJsonNames.ErrorCode, out _))
            {
                return ApplicationResult<MuMuInstanceEnumeration>.Failure(
                    Unrecognized(EnumerationOperation, exitCode, ReadProviderErrorCode(root)));
            }

            List<MuMuInstanceInfo> instances = [];
            List<MuMuUnavailableEntry> unavailableEntries = [];

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!TryCreateInstanceId(property.Name, out MuMuInstanceId entryId)
                    || property.Value.ValueKind != JsonValueKind.Object)
                {
                    return ApplicationResult<MuMuInstanceEnumeration>.Failure(
                        Unrecognized(EnumerationOperation, exitCode, null));
                }

                if (property.Value.TryGetProperty(MuMuManagerJsonNames.ErrorCode, out _))
                {
                    MuMuProviderError? entryError = ReadProviderError(property.Value);

                    if (entryError is null || entryError.Code == 0)
                    {
                        return ApplicationResult<MuMuInstanceEnumeration>.Failure(
                            Unrecognized(EnumerationOperation, exitCode, entryError?.Code));
                    }

                    unavailableEntries.Add(new MuMuUnavailableEntry
                    {
                        Id = entryId,
                        ProviderError = entryError,
                    });

                    continue;
                }

                MuMuInstanceInfo? info = ReadInstance(property.Value, entryId);

                if (info is null)
                {
                    return ApplicationResult<MuMuInstanceEnumeration>.Failure(
                        Unrecognized(EnumerationOperation, exitCode, null));
                }

                instances.Add(info);
            }

            return ApplicationResult<MuMuInstanceEnumeration>.Success(new MuMuInstanceEnumeration
            {
                Instances = instances,
                UnavailableEntries = unavailableEntries,
            });
        }
    }

    /// <summary>Разбирает ответ операции изменения состояния экземпляра.</summary>
    /// <param name="id">Identity экземпляра, к которому относилась операция.</param>
    /// <param name="command">Выполненная операция.</param>
    /// <param name="standardOutput">Захваченный stdout процесса.</param>
    /// <param name="exitCode">Код выхода процесса.</param>
    /// <returns>Результат операции либо application-level отказ.</returns>
    public static ApplicationResult<MuMuControlOutcome> ParseControlResult(
        MuMuInstanceId id,
        MuMuControlCommand command,
        string standardOutput,
        int exitCode)
    {
        if (TryParseRootObject(standardOutput) is not JsonDocument document)
        {
            return ApplicationResult<MuMuControlOutcome>.Failure(
                Unrecognized(ControlOperation, exitCode, null));
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            MuMuProviderError? providerError = ReadProviderError(root);

            if (providerError is null || providerError.Code != exitCode)
            {
                return ApplicationResult<MuMuControlOutcome>.Failure(
                    Unrecognized(ControlOperation, exitCode, providerError?.Code));
            }

            return ApplicationResult<MuMuControlOutcome>.Success(new MuMuControlOutcome
            {
                Id = id,
                Command = command,
                ExitCode = exitCode,
                BoundedOutput = MuMuBoundedText.Bounded(standardOutput),
                ProviderError = providerError.Code == 0 ? null : providerError,
            });
        }
    }

    /// <summary>
    /// Проверяет грамматику номера экземпляра в ответе провайдера.
    /// </summary>
    /// <remarks>
    /// Грамматика не дублируется: она читается у владельца синтаксиса значения <c>mumu.instance</c>.
    /// Провайдерский номер в ответе и провайдерская форма значения в конфигурации — одна и та же
    /// грамматика, поэтому второй её владелец не заводится.
    /// </remarks>
    private static bool TryCreateInstanceId(string? value, out MuMuInstanceId id)
    {
        id = default;

        if (value is null
            || !MuMuInstanceValue.IsValid(MuMuInstanceValue.ProviderPrefix + value))
        {
            return false;
        }

        id = MuMuInstanceId.FromIndex(value);
        return true;
    }

    private static ApplicationFailure Unrecognized(string operation, int exitCode, int? providerErrorCode)
        => MuMuPlatformFailureMapper.ForUnrecognizedResponse(operation, exitCode, providerErrorCode);

    private static JsonDocument? TryParseRootObject(string standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(standardOutput, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException)
        {
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    private static MuMuInstanceInfo? ReadInstance(JsonElement element, MuMuInstanceId expectedId)
    {
        if (!TryReadString(element, MuMuManagerJsonNames.Index, out string indexText)
            || !TryCreateInstanceId(indexText, out MuMuInstanceId id)
            || !string.Equals(id.Index, expectedId.Index, StringComparison.Ordinal)
            || !TryReadBoolean(element, MuMuManagerJsonNames.IsProcessStarted, out bool isProcessStarted)
            || !TryReadBoolean(element, MuMuManagerJsonNames.IsAndroidStarted, out bool isAndroidStarted)
            || !TryReadOptionalString(element, MuMuManagerJsonNames.Name, out string? displayName)
            || !TryReadOptionalString(element, MuMuManagerJsonNames.AndroidVersion, out string? androidVersion)
            || !TryReadOptionalString(element, MuMuManagerJsonNames.PlayerState, out string? playerState)
            || !TryReadOptionalInt32(element, MuMuManagerJsonNames.ProcessId, out int? processId)
            || !TryReadOptionalInt32(element, MuMuManagerJsonNames.AdbPort, out int? adbPort)
            || !TryReadOptionalInt64(element, MuMuManagerJsonNames.CreatedTimestamp, out long? createdTimestamp)
            || !TryReadOptionalErrorCode(element, MuMuManagerJsonNames.InstanceErrorCode, out bool hasInstanceError)
            || !TryReadOptionalErrorCode(element, MuMuManagerJsonNames.LaunchErrorCode, out bool hasLaunchError))
        {
            return null;
        }

        bool hasProviderError = hasInstanceError || hasLaunchError;

        return new MuMuInstanceInfo
        {
            Id = id,
            DisplayName = displayName,
            AndroidVersion = androidVersion,
            State = MuMuPlayerStateMap.Map(isProcessStarted, isAndroidStarted, playerState, hasProviderError),
            RawPlayerState = playerState,
            IsProcessStarted = isProcessStarted,
            IsAndroidStarted = isAndroidStarted,
            ProcessId = processId,
            AdbPort = adbPort,
            CreatedTimestamp = createdTimestamp,
        };
    }

    private static MuMuProviderError? ReadProviderError(JsonElement element)
    {
        if (!element.TryGetProperty(MuMuManagerJsonNames.ErrorCode, out JsonElement codeElement)
            || codeElement.ValueKind != JsonValueKind.Number
            || !codeElement.TryGetInt32(out int code)
            || !TryReadString(element, MuMuManagerJsonNames.ErrorMessage, out string message))
        {
            return null;
        }

        return new MuMuProviderError { Code = code, Message = message };
    }

    private static int? ReadProviderErrorCode(JsonElement element)
        => element.TryGetProperty(MuMuManagerJsonNames.ErrorCode, out JsonElement codeElement)
            && codeElement.ValueKind == JsonValueKind.Number
            && codeElement.TryGetInt32(out int code)
                ? code
                : null;

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadOptionalString(JsonElement element, string propertyName, out string? value)
    {
        value = null;

        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryReadBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;

        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadOptionalInt32(JsonElement element, string propertyName, out int? value)
    {
        value = null;

        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadOptionalInt64(JsonElement element, string propertyName, out long? value)
    {
        value = null;

        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out long parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadOptionalErrorCode(JsonElement element, string propertyName, out bool hasNonZeroCode)
    {
        hasNonZeroCode = false;

        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int code))
        {
            return false;
        }

        hasNonZeroCode = code != 0;
        return true;
    }
}
