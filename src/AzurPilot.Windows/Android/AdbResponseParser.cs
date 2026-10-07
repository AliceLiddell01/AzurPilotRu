using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Fail-closed разбор вывода ADB в Android-контракты.
/// </summary>
/// <remarks>
/// <para>
/// Предметом контракта является точная форма ответа, а не «похожесть» на неё: если ответ не распознан
/// целиком, наблюдение объявляется недоказанным, а не достраивается догадкой. «Не удалось спросить» и
/// «отсутствует» — разные результаты и разные значения: неудачный запрос даёт
/// <see cref="AndroidPackagePresence.QueryFailed"/>, <see cref="AndroidTransportState.Unknown"/> или
/// <see langword="null"/> вместо списка процессов, и никогда не выдаётся за доказанное отсутствие.
/// </para>
/// <para>
/// Усечённый захваченный вывод полноценным ответом не считается: признак усечения проверяется первым, и
/// такой вывод даёт недоказанное наблюдение, а не частично разобранное значение.
/// </para>
/// <para>
/// Разбор не запускает процессов и не обращается к устройству: он получает уже завершившийся результат
/// команды и только классифицирует его.
/// </para>
/// </remarks>
public static class AdbResponseParser
{
    private const string DeviceStateName = "device";

    private const string OfflineStateName = "offline";

    private const string AbsentStateName = "absent";

    private const string UnknownStateName = "unknown";

    private const string ResolvedStatusName = "resolved";

    private const string MissingStatusName = "missing";

    private const string AmbiguousStatusName = "ambiguous";

    private const string QueryFailedStatusName = "query_failed";

    private const string ForegroundStatusName = "foreground";

    private const string OtherStatusName = "other";

    private const string TruncatedReasonName = "output_truncated";

    private const string UnrecognizedReasonName = "response_unrecognized";

    private const string PackagePathPrefix = "package:";

    private const string NoActivitiesFoundMessage = "No activities found";

    private const string CurrentFocusMarker = "mCurrentFocus=";

    private const string FocusedAppMarker = "mFocusedApp=";

    private const string AdbVersionBannerPrefix = "Android Debug Bridge version";

    private const string AdbVersionLinePrefix = "Version ";

    private static readonly string[] FocusMarkers = [CurrentFocusMarker, FocusedAppMarker];

    /// <summary>Разбирает ответ команды наблюдения transport точного endpoint-а.</summary>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <param name="endpoint">Точный endpoint, к которому относилась команда.</param>
    /// <returns>Наблюдение transport: доказанное состояние либо <see cref="AndroidTransportState.Unknown"/>.</returns>
    public static AndroidTransportObservation ParseTransport(
        WindowsProcessOutcome outcome,
        AndroidEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (IsOutputTruncated(outcome))
        {
            return new AndroidTransportObservation(
                AndroidTransportState.Unknown,
                Evidence(outcome, "state=" + UnknownStateName, TruncatedReasonName));
        }

        if (outcome.ExitCode == 0)
        {
            string state = FirstToken(outcome.StandardOutput);

            if (string.Equals(state, DeviceStateName, StringComparison.Ordinal))
            {
                return new AndroidTransportObservation(
                    AndroidTransportState.Device,
                    Evidence(outcome, "state=" + DeviceStateName));
            }

            if (string.Equals(state, OfflineStateName, StringComparison.Ordinal))
            {
                return new AndroidTransportObservation(
                    AndroidTransportState.Offline,
                    Evidence(outcome, "state=" + OfflineStateName));
            }

            return new AndroidTransportObservation(
                AndroidTransportState.Unknown,
                Evidence(outcome, "state=" + UnknownStateName, UnrecognizedReasonName));
        }

        if (IsTargetNotFound(outcome, endpoint))
        {
            return new AndroidTransportObservation(
                AndroidTransportState.Absent,
                Evidence(outcome, "state=" + AbsentStateName));
        }

        return new AndroidTransportObservation(
            AndroidTransportState.Unknown,
            Evidence(outcome, "state=" + UnknownStateName, UnrecognizedReasonName));
    }

    /// <summary>Разбирает ответ команды запроса пути пакета.</summary>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <param name="endpoint">Точный endpoint, к которому относилась команда.</param>
    /// <returns>Наблюдённое присутствие пакета.</returns>
    public static AndroidPackagePresence ParsePackagePresence(
        WindowsProcessOutcome outcome,
        AndroidEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (IsOutputTruncated(outcome) || IsTargetNotFound(outcome, endpoint))
        {
            // Недостижимый target не доказывает отсутствие пакета: «не удалось спросить» — не «нет».
            return AndroidPackagePresence.QueryFailed;
        }

        if (outcome.ExitCode == 0)
        {
            foreach (string line in NonEmptyLines(outcome.StandardOutput))
            {
                if (line.StartsWith(PackagePathPrefix, StringComparison.Ordinal))
                {
                    return AndroidPackagePresence.Installed;
                }
            }

            return AndroidPackagePresence.QueryFailed;
        }

        // Доказанное отсутствие пакета — пустой ответ целиком: непустой stderr означает, что запрос не
        // удалось выполнить («error: device offline» и подобные), и такой ответ не выдаётся за «пакета нет».
        return string.IsNullOrWhiteSpace(outcome.StandardOutput)
            && string.IsNullOrWhiteSpace(outcome.StandardError)
            ? AndroidPackagePresence.Absent
            : AndroidPackagePresence.QueryFailed;
    }

    /// <summary>Разбирает ответ команды запроса launcher-компонентов пакета.</summary>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <param name="endpoint">Точный endpoint, к которому относилась команда.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <returns>Результат разрешения launcher-компонента.</returns>
    public static AndroidLauncherResolution ParseLauncherResolution(
        WindowsProcessOutcome outcome,
        AndroidEndpoint endpoint,
        AndroidPackageId package)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (IsOutputTruncated(outcome))
        {
            return QueryFailedResolution(
                Evidence(outcome, "status=" + QueryFailedStatusName, TruncatedReasonName));
        }

        if (outcome.ExitCode != 0 || IsTargetNotFound(outcome, endpoint))
        {
            return QueryFailedResolution(
                Evidence(outcome, "status=" + QueryFailedStatusName, UnrecognizedReasonName));
        }

        List<string> lines = [.. NonEmptyLines(outcome.StandardOutput)];

        if (lines.Count == 0 || lines.TrueForAll(IsNoActivitiesFound))
        {
            return new AndroidLauncherResolution(
                AndroidLauncherResolutionStatus.Missing,
                null,
                0,
                Evidence(outcome, "status=" + MissingStatusName, "matching_components=0"));
        }

        List<AndroidComponent> components = new(lines.Count);

        foreach (string line in lines)
        {
            if (!TryReadComponent(line, package, out AndroidComponent? component))
            {
                return QueryFailedResolution(
                    Evidence(outcome, "status=" + QueryFailedStatusName, UnrecognizedReasonName));
            }

            components.Add(component);
        }

        if (components.Count == 1)
        {
            return new AndroidLauncherResolution(
                AndroidLauncherResolutionStatus.Resolved,
                components[0],
                1,
                Evidence(
                    outcome,
                    "status=" + ResolvedStatusName,
                    "matching_components=1",
                    "component=" + components[0].Flattened));
        }

        return new AndroidLauncherResolution(
            AndroidLauncherResolutionStatus.Ambiguous,
            null,
            components.Count,
            Evidence(
                outcome,
                "status=" + AmbiguousStatusName,
                "matching_components=" + Count(components.Count)));
    }

    /// <summary>Разбирает ответ команды перечисления процессов.</summary>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <returns>Наблюдение процессов пакета; при недоказанном наблюдении список равен <see langword="null"/>.</returns>
    public static AndroidProcessObservation ParseProcessObservation(
        WindowsProcessOutcome outcome,
        AndroidPackageId package)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (IsOutputTruncated(outcome))
        {
            return UnprovenProcesses(
                Evidence(outcome, "processes=" + UnknownStateName, TruncatedReasonName));
        }

        if (outcome.ExitCode != 0)
        {
            return UnprovenProcesses(
                Evidence(outcome, "processes=" + UnknownStateName, UnrecognizedReasonName));
        }

        List<string> lines = [.. NonEmptyLines(outcome.StandardOutput)];

        if (lines.Count == 0)
        {
            // Перечисление процессов всегда печатает заголовок таблицы: пустой ответ не распознан.
            return UnprovenProcesses(
                Evidence(outcome, "processes=" + UnknownStateName, UnrecognizedReasonName));
        }

        List<int> processIds = [];
        string packagePrefix = package.ToString() + ":";

        for (int index = 0; index < lines.Count; index++)
        {
            if (!TrySplitProcessLine(lines[index], out string pidText, out string name))
            {
                return UnprovenProcesses(
                    Evidence(outcome, "processes=" + UnknownStateName, UnrecognizedReasonName));
            }

            if (index == 0 && !int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                // Первая строка — заголовок таблицы: у него в колонке PID стоит имя колонки.
                continue;
            }

            if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId))
            {
                return UnprovenProcesses(
                    Evidence(outcome, "processes=" + UnknownStateName, UnrecognizedReasonName));
            }

            if (string.Equals(name, package.ToString(), StringComparison.Ordinal)
                || name.StartsWith(packagePrefix, StringComparison.Ordinal))
            {
                processIds.Add(processId);
            }
        }

        return new AndroidProcessObservation(
            processIds.Count,
            processIds,
            Evidence(outcome, "processes=" + Count(processIds.Count)));
    }

    /// <summary>Разбирает ответ дампа сведений о дисплеях и определяет наблюдённый foreground.</summary>
    /// <remarks>
    /// Сравнение выполняется по exact package, а не по компоненту: запуск launcher-компонента может
    /// привести к другой activity того же пакета, поэтому сравнение компонентов не доказало бы
    /// postcondition никогда. Компонент сообщается как evidence, а не как identity.
    /// </remarks>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <param name="expectedPackage">Идентификатор пакета, foreground которого подтверждается.</param>
    /// <returns>Наблюдение foreground: доказанный статус либо <see cref="AndroidForegroundStatus.Unknown"/>.</returns>
    public static AndroidForegroundObservation ParseForeground(
        WindowsProcessOutcome outcome,
        string expectedPackage)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackage);

        if (IsOutputTruncated(outcome))
        {
            return UnknownForeground(Evidence(outcome, TruncatedReasonName));
        }

        if (outcome.ExitCode != 0)
        {
            return UnknownForeground(Evidence(outcome, UnrecognizedReasonName));
        }

        if (!TryReadFocusedComponent(outcome.StandardOutput, out AndroidComponent? component))
        {
            return UnknownForeground(Evidence(outcome, UnrecognizedReasonName));
        }

        bool isExpectedPackage = string.Equals(
            component.Package.ToString(),
            expectedPackage,
            StringComparison.Ordinal);

        return new AndroidForegroundObservation(
            isExpectedPackage ? AndroidForegroundStatus.Foreground : AndroidForegroundStatus.Other,
            component,
            Evidence(
                outcome,
                "status=" + (isExpectedPackage ? ForegroundStatusName : OtherStatusName),
                "focus=" + component.Flattened));
    }

    /// <summary>Читает целочисленное значение свойства Android из ответа <c>getprop</c>.</summary>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <returns>Значение свойства либо <see langword="null"/>, если оно не наблюдалось.</returns>
    public static int? ParsePropertyInteger(WindowsProcessOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (IsOutputTruncated(outcome) || outcome.ExitCode != 0)
        {
            return null;
        }

        string value = outcome.StandardOutput.Trim();

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    /// <summary>Читает текстовое значение свойства Android из ответа <c>getprop</c>.</summary>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <returns>Значение свойства либо <see langword="null"/>, если оно не наблюдалось.</returns>
    public static string? ParsePropertyText(WindowsProcessOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (IsOutputTruncated(outcome) || outcome.ExitCode != 0)
        {
            return null;
        }

        string value = BoundedDiagnosticText.Bounded(outcome.StandardOutput);

        return value.Length == 0 ? null : value;
    }

    /// <summary>Читает bounded evidence версии ADB из ответа команды <c>version</c>.</summary>
    /// <remarks>
    /// В evidence попадают только строки с версией: строка <c>Installed as</c> содержит абсолютный путь
    /// конкретной машины и в диагностику не переносится.
    /// </remarks>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <returns>Bounded evidence версии либо <see langword="null"/>, если форма ответа не распознана.</returns>
    public static string? ParseAdbVersionEvidence(WindowsProcessOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (IsOutputTruncated(outcome) || outcome.ExitCode != 0)
        {
            return null;
        }

        string? banner = null;
        string? version = null;

        foreach (string line in NonEmptyLines(outcome.StandardOutput))
        {
            if (banner is null && line.StartsWith(AdbVersionBannerPrefix, StringComparison.Ordinal))
            {
                banner = line;
                continue;
            }

            if (version is null && line.StartsWith(AdbVersionLinePrefix, StringComparison.Ordinal))
            {
                version = line;
            }
        }

        if (banner is null)
        {
            return null;
        }

        return BoundedDiagnosticText.Bounded(
            version is null ? banner : banner + "; " + version);
    }

    /// <summary>Приводит завершившуюся команду ADB к контракту результата однократного вызова.</summary>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <returns>Результат с кодом выхода и bounded выводом.</returns>
    public static AndroidCommandOutcome ToCommandOutcome(WindowsProcessOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new AndroidCommandOutcome(
            outcome.ExitCode,
            BoundedDiagnosticText.Bounded(outcome.StandardOutput + " " + outcome.StandardError));
    }

    /// <summary>Определяет, что ADB сообщил об отсутствии запрошенного target-а.</summary>
    /// <remarks>
    /// Признаком служит документированная форма отказа ADB: ненулевой код выхода и сообщение, которое
    /// одновременно называет отсутствующим устройство и содержит запрошенный endpoint. Любая другая
    /// форма отказа не истолковывается: недостижимость target-а не выдаётся за доказанное отсутствие.
    /// </remarks>
    /// <param name="outcome">Результат завершившейся команды ADB.</param>
    /// <param name="endpoint">Точный endpoint, к которому относилась команда.</param>
    /// <returns><see langword="true"/>, если ADB сообщил, что устройства по этому адресу нет.</returns>
    public static bool IsTargetNotFound(WindowsProcessOutcome outcome, AndroidEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.ExitCode == 0)
        {
            return false;
        }

        string diagnostics = outcome.StandardError + " " + outcome.StandardOutput;

        return diagnostics.Contains("not found", StringComparison.OrdinalIgnoreCase)
            && diagnostics.Contains(endpoint.ToString(), StringComparison.Ordinal);
    }

    private static AndroidLauncherResolution QueryFailedResolution(string evidence)
        => new(AndroidLauncherResolutionStatus.QueryFailed, null, 0, evidence);

    private static AndroidProcessObservation UnprovenProcesses(string evidence)
        => new(0, null, evidence);

    private static AndroidForegroundObservation UnknownForeground(string evidence)
        => new(AndroidForegroundStatus.Unknown, null, evidence);

    private static bool IsOutputTruncated(WindowsProcessOutcome outcome)
        => outcome.StandardOutputTruncated || outcome.StandardErrorTruncated;

    private static bool IsNoActivitiesFound(string line)
        => string.Equals(line, NoActivitiesFoundMessage, StringComparison.OrdinalIgnoreCase);

    private static string Evidence(WindowsProcessOutcome outcome, params string?[] parts)
    {
        List<string> present = new(parts.Length + 1)
        {
            "exit=" + Count(outcome.ExitCode),
        };

        foreach (string? part in parts)
        {
            if (!string.IsNullOrEmpty(part))
            {
                present.Add(part);
            }
        }

        return BoundedDiagnosticText.Bounded(string.Join(';', present));
    }

    private static string FirstToken(string value)
    {
        foreach (string line in NonEmptyLines(value))
        {
            int separator = line.IndexOf(' ', StringComparison.Ordinal);

            return separator < 0 ? line : line[..separator];
        }

        return string.Empty;
    }

    private static List<string> NonEmptyLines(string value)
    {
        List<string> lines = [];

        foreach (string raw in value.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static bool TrySplitProcessLine(string line, out string pidText, out string name)
    {
        int separator = line.IndexOf(' ', StringComparison.Ordinal);

        if (separator <= 0)
        {
            pidText = string.Empty;
            name = string.Empty;
            return false;
        }

        pidText = line[..separator];
        name = line[(separator + 1)..].Trim();

        return name.Length > 0;
    }

    private static bool TryReadComponent(
        string line,
        AndroidPackageId expectedPackage,
        [NotNullWhen(true)] out AndroidComponent? component)
    {
        if (!TrySplitComponent(line, out string packageValue, out string activityValue)
            || !string.Equals(packageValue, expectedPackage.ToString(), StringComparison.Ordinal))
        {
            component = null;
            return false;
        }

        component = new AndroidComponent(
            new AndroidPackageId(packageValue),
            activityValue,
            packageValue + "/" + activityValue);

        return true;
    }

    private static bool TrySplitComponent(string value, out string packageValue, out string activityValue)
    {
        int separator = value.IndexOf('/', StringComparison.Ordinal);

        if (separator <= 0 || separator == value.Length - 1)
        {
            packageValue = string.Empty;
            activityValue = string.Empty;
            return false;
        }

        packageValue = value[..separator];
        activityValue = value[(separator + 1)..];

        return !ContainsSeparator(packageValue) && !ContainsSeparator(activityValue);
    }

    private static bool ContainsSeparator(string value)
    {
        foreach (char symbol in value)
        {
            if (char.IsWhiteSpace(symbol) || symbol is '=' or '{' or '}' or '/')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadFocusedComponent(
        string output,
        [NotNullWhen(true)] out AndroidComponent? component)
    {
        // Переводы строк приводятся к одной форме владельцем этого правила: иначе граница строки зависела бы
        // от того, чем именно разделены строки ответа.
        string normalized = output.ReplaceLineEndings("\n");

        foreach (string marker in FocusMarkers)
        {
            int markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);

            if (markerIndex < 0)
            {
                continue;
            }

            // Значение маркера ограничено его собственной строкой: иначе значение соседнего поля было бы
            // приписано этому маркеру, и недоказанный передний план выглядел бы доказанным.
            int start = markerIndex + marker.Length;
            int lineEnd = normalized.IndexOf('\n', start);
            string line = lineEnd < 0 ? normalized[start..] : normalized[start..lineEnd];
            int end = line.IndexOf('}', StringComparison.Ordinal);
            string segment = end < 0 ? line : line[..end];

            foreach (string token in segment.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TrySplitComponent(token, out string packageValue, out string activityValue))
                {
                    continue;
                }

                component = new AndroidComponent(
                    new AndroidPackageId(packageValue),
                    activityValue,
                    packageValue + "/" + activityValue);

                return true;
            }
        }

        component = null;
        return false;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
