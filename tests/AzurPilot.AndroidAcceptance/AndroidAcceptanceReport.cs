using System.Globalization;
using System.Reflection;
using System.Text;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AcceptanceSanitizer = AzurPilot.MuMuAcceptance.AcceptanceSanitizer;

namespace AzurPilot.AndroidAcceptance;

/// <summary>
/// Одна записанная строка отчёта приёмки Android.
/// </summary>
/// <param name="Number">Номер шага контракта приёмки (1–10).</param>
/// <param name="Name">Название шага.</param>
/// <param name="IsProven">Признак того, что шаг доказан.</param>
/// <param name="Detail">Bounded детализация шага без machine-specific значений.</param>
/// <param name="RawDetail">
/// Та же bounded детализация до замены machine-specific значений: по ней проверяется, что отчёт не
/// содержал защищённых значений и до замены, а не только после неё.
/// </param>
internal sealed record AcceptanceStep(int Number, string Name, bool IsProven, string Detail, string RawDetail);

/// <summary>
/// Исход прогона приёмки: доказан или нет, и если нет — почему.
/// </summary>
/// <param name="IsProven">Признак доказанности всей цепочки и восстановления состояния игры.</param>
/// <param name="FailureCode">Локальный код отказа приёмки; отсутствует при успехе.</param>
/// <param name="FailureMessage">Описание отказа приёмки; отсутствует при успехе.</param>
internal sealed record AcceptanceResult(bool IsProven, string? FailureCode, string? FailureMessage)
{
    /// <summary>Создаёт успешный исход приёмки.</summary>
    /// <returns>Исход с доказанной цепочкой.</returns>
    internal static AcceptanceResult Proven() => new(true, null, null);

    /// <summary>Создаёт исход, в котором приёмка не доказана.</summary>
    /// <param name="code">Локальный код отказа приёмки.</param>
    /// <param name="message">Описание отказа.</param>
    /// <returns>Исход с отказом.</returns>
    internal static AcceptanceResult NotProven(string code, string message) => new(false, code, message);
}

/// <summary>
/// Identity сборки: имя, версия и informational version с revision.
/// </summary>
/// <param name="AssemblyName">Простое имя сборки.</param>
/// <param name="Version">Версия сборки.</param>
/// <param name="InformationalVersion">Informational version сборки — identity текущего build.</param>
internal sealed record AcceptanceBuildIdentity(string AssemblyName, string Version, string InformationalVersion)
{
    /// <summary>Читает identity сборки, которой принадлежит тип-якорь.</summary>
    /// <param name="anchor">Тип, по которому определяется сборка.</param>
    /// <returns>Identity сборки.</returns>
    internal static AcceptanceBuildIdentity For(Type anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        Assembly assembly = anchor.Assembly;
        AssemblyName name = assembly.GetName();

        return new AcceptanceBuildIdentity(
            name.Name ?? string.Empty,
            name.Version?.ToString() ?? string.Empty,
            assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? string.Empty);
    }

    /// <summary>Описывает identity одной строкой.</summary>
    /// <returns>Строка вида <c>AzurPilot.Windows 1.0.0 (1.0.0+revision)</c>.</returns>
    internal string Describe()
        => InformationalVersion.Length == 0
            ? AssemblyName + " " + Version
            : AssemblyName + " " + Version + " (" + InformationalVersion + ")";
}

/// <summary>
/// Готовый к печати текст отчёта и результат его проверки на machine-specific значения.
/// </summary>
/// <param name="Text">Санитизированный текст отчёта для <c>stdout</c>.</param>
/// <param name="IsSanitized">Признак того, что в отчёт не попало ни одного защищённого значения.</param>
internal sealed record AcceptanceReportText(string Text, bool IsSanitized);

/// <summary>
/// Bounded отчёт приёмки Android: шаги контракта, доказанные postconditions и итог.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт печатается в <c>stdout</c> как человекочитаемый итог и не смешивается со structured log,
/// который уходит в <c>stderr</c>. Каждая деталь ограничена по длине владельцем bounded текста
/// <see cref="BoundedDiagnosticText"/> и проходит через санитайзер.
/// </para>
/// <para>
/// Отчёт связан с exact build revision: печатаются identity инструмента и обеих проверяемых сборок
/// (Core и Windows) с их informational version.
/// </para>
/// <para>
/// Текст собирается дважды: печатаемый — с заменёнными machine-specific значениями, и проверочный — с
/// исходными. Проверка идёт по исходному тексту, поэтому защита не сводится к самой замене: если в отчёт
/// попало защищённое значение, приёмка не доказана, даже когда все шаги цепочки прошли.
/// </para>
/// <para>
/// Санитайзер не заводится вторым: правила приватности отчёта — единственный владелец, общий с
/// инструментом MuMu-приёмки. Здесь он используется как есть.
/// </para>
/// </remarks>
internal sealed class AndroidAcceptanceReport
{
    private const string ProvenWord = "доказано";

    private const string NotProvenWord = "НЕ доказано";

    private readonly List<AcceptanceStep> _steps = [];

    /// <summary>Санитайзер, через который проходит весь текст отчёта.</summary>
    internal AcceptanceSanitizer Sanitizer { get; } = new();

    /// <summary>Записывает шаг приёмки.</summary>
    /// <param name="number">Номер шага контракта (1–10).</param>
    /// <param name="name">Название шага.</param>
    /// <param name="isProven">Признак того, что шаг доказан.</param>
    /// <param name="detail">Детализация шага.</param>
    internal void Record(int number, string name, bool isProven, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(detail);

        string bounded = BoundedDiagnosticText.Bounded(detail);
        _steps.Add(new AcceptanceStep(number, name, isProven, Sanitizer.Sanitize(bounded), bounded));
    }

    /// <summary>Собирает текст отчёта и проверяет его на machine-specific значения.</summary>
    /// <remarks>
    /// Проверка выполняется по тексту до замены: санитайзер — вторая линия защиты, поэтому отчёт не
    /// должен содержать защищённые значения и без неё. Замену видит только печатаемый текст.
    /// </remarks>
    /// <param name="options">Проверенные аргументы прогона.</param>
    /// <param name="result">Исход прогона.</param>
    /// <returns>Готовый к печати текст и результат проверки санитайзера.</returns>
    internal AcceptanceReportText Render(AcceptanceOptions options, AcceptanceResult result)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(result);

        string body = Compose(options, static step => step.Detail);
        string rawBody = Compose(options, static step => step.RawDetail);
        string sanitized = Sanitizer.Sanitize(body);
        bool isSanitized = !Sanitizer.ContainsProtectedValue(rawBody);

        StringBuilder verdict = new(sanitized);
        if (!isSanitized)
        {
            _ = verdict.AppendLine(
                "  Проверка санитайзера: НЕ доказано — в отчёт попало machine-specific значение.");
        }
        else
        {
            _ = verdict.AppendLine(
                "  Проверка санитайзера: доказано — machine-specific значения в отчёт не попали.");
        }

        if (result.IsProven && isSanitized)
        {
            _ = verdict.AppendLine("Итог: приёмка пройдена — цепочка и начальное состояние игры доказаны.");
        }
        else
        {
            string message = isSanitized
                ? result.FailureMessage ?? string.Empty
                : "acceptance_report_not_sanitized: в отчёт попало machine-specific значение.";
            _ = verdict.AppendLine("Итог: приёмка не доказана — " + message);
        }

        return new AcceptanceReportText(verdict.ToString(), isSanitized);
    }

    /// <summary>Собирает текст отчёта из записанных шагов и постоянных строк.</summary>
    /// <param name="options">Проверенные аргументы прогона.</param>
    /// <param name="detailSelector">Как читать детализацию шага: заменённую или исходную.</param>
    /// <returns>Полный текст отчёта без итога.</returns>
    private string Compose(AcceptanceOptions options, Func<AcceptanceStep, string> detailSelector)
    {
        StringBuilder builder = new();
        _ = builder.AppendLine(
            "AzurPilot Android acceptance — реальная приёмка ADB readiness и lifecycle Azur Lane Global/EN");
        _ = builder.AppendLine("  Инструмент: " + Describe(typeof(AndroidAcceptanceReport)));
        _ = builder.AppendLine("  Core: " + Describe(typeof(AzurLaneGameLifecycleService)));
        _ = builder.AppendLine("  Windows: " + Describe(typeof(AndroidWindowsHost)));
        _ = builder.AppendLine(
            "  Запрошенный exact instance: " + options.InstanceValue
            + " (explicit; автоматический выбор не используется)");
        _ = builder.AppendLine("  Режим: " + AcceptanceOptions.DescribeMode());
        _ = builder.AppendLine(
            "  Product identity: " + AzurLaneProduct.DisplayName + " (" + AzurLaneProduct.Package + ")");

        foreach (AcceptanceStep step in _steps)
        {
            _ = builder.AppendLine(
                "  Шаг " + step.Number.ToString(CultureInfo.InvariantCulture) + ". " + step.Name + ": "
                + (step.IsProven ? ProvenWord : NotProvenWord) + " — " + detailSelector(step));
        }

        return builder.ToString();
    }

    /// <summary>Описывает identity сборки типа-якоря.</summary>
    /// <param name="anchor">Тип, по которому определяется сборка.</param>
    /// <returns>Строка с identity сборки.</returns>
    private static string Describe(Type anchor) => AcceptanceBuildIdentity.For(anchor).Describe();
}
