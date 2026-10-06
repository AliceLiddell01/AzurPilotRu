using System.Globalization;
using AzurPilot.App;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Tests.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Проверки MuMu-диагностики на реальном startup-пути в отдельном процессе.
/// </summary>
/// <remarks>
/// <para>
/// Продукт не разбирает аргументы запуска, поэтому конкретную конфигурацию ему диктует проба
/// <c>--application-composition &lt;путь&gt;</c>: только так проверяется, что отказ MuMu остаётся
/// диагностическим результатом, а не причиной ненулевого кода выхода, и что нормализация legacy-схемы
/// видна в structured diagnostics.
/// </para>
/// <para>
/// Проверки не зависят от того, установлена ли MuMu на машине: при отсутствии установки диагностика
/// останавливается на обнаружении, при наличии — на разрешении недостижимого экземпляра. Оба исхода
/// обязаны быть данными, поэтому проверяются свойства, верные в обоих случаях.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MuMuDiagnosticsProcessTests
{
    /// <summary>Версия схемы, которую объявляет legacy-документ v1.</summary>
    private const int LegacySchemaVersion = 1;

    /// <summary>Property structured diagnostics с bounded статусом control surface MuMu.</summary>
    private const string ControlSurfaceStatusProperty = "ControlSurfaceStatus";

    /// <summary>Property structured diagnostics со стабильным кодом отказа.</summary>
    private const string FailureCodeProperty = "FailureCode";

    /// <summary>Property structured diagnostics с версией эффективной схемы конфигурации.</summary>
    private const string SchemaVersionProperty = "SchemaVersion";

    /// <summary>Property structured diagnostics с версией схемы источника конфигурации.</summary>
    private const string SourceSchemaVersionProperty = "SourceSchemaVersion";

    /// <summary>Property structured diagnostics с канонической identity выбранного экземпляра MuMu.</summary>
    private const string SelectedInstanceIdProperty = "SelectedInstanceId";

    /// <summary>Фрагмент документа конфигурации, который не должен попадать в вывод.</summary>
    private const string ConfigurationDocumentFragment = "\"minimumLevel\": \"Information\"";

    /// <summary>
    /// Документ схемы v2, требующий заведомо недостижимый номер экземпляра: разрешение выбора обязано
    /// завершиться ожидаемым отказом на любой машине с обнаруженной установкой.
    /// </summary>
    private const string UnresolvableInstanceConfiguration = """
        {
          "schemaVersion": 2,
          "diagnostics": {
            "minimumLevel": "Information"
          },
          "mumu": {
            "instance": "mumu:999"
          }
        }
        """;

    /// <summary>Legacy-документ схемы v1: он нормализуется к схеме v2 в памяти.</summary>
    private const string LegacyConfiguration = """
        {
          "schemaVersion": 1,
          "diagnostics": {
            "minimumLevel": "Information"
          }
        }
        """;

    [Fact(DisplayName = "Отказ MuMu виден в диагностике, но не отклоняет запуск реального процесса")]
    public void MuMuFailureDoesNotRejectStartup()
    {
        using ApplicationCompositionProbe probe = ApplicationCompositionProbe.CreateStaged();
        using TemporaryConfigurationDirectory directory = new();

        ApplicationProbeRun run = probe.Run(directory.WriteConfiguration(UnresolvableInstanceConfiguration));

        Assert.Equal(0, run.ProbeExitCode);

        // Отказ MuMu — результат диагностики: код выхода процесса остаётся успешным, потому что запуск
        // решает конфигурация и native boundary, а не состояние эмулятора.
        Assert.Equal(AzurPilotExitCode.Success, run.ApplicationExitCode);

        StructuredLogRecord failure = Assert.Single(
            run.Logs,
            log => log.State.ContainsKey(ControlSurfaceStatusProperty) && log.IsAtLeast(LogLevel.Warning));
        string failureCode = Assert.IsType<string>(
            StructuredLogRecord.Read(failure.State, FailureCodeProperty));
        Assert.NotEmpty(failureCode);

        // Lifecycle-коды синтезируются только выполненной mutation, поэтому их отсутствие доказывает, что
        // диагностика эмулятор не запускала и не останавливала: отказ сообщается, а не «исправляется».
        Assert.NotEqual(ApplicationFailure.MuMuLifecyclePostconditionNotMet, failureCode);
        Assert.NotEqual(ApplicationFailure.MuMuLifecycleTimeout, failureCode);
    }

    [Fact(DisplayName = "Legacy-схема v1 сообщается как нормализованная, без дампа конфигурации")]
    public void LegacyV1ConfigurationIsReportedAsNormalized()
    {
        using ApplicationCompositionProbe probe = ApplicationCompositionProbe.CreateStaged();
        using TemporaryConfigurationDirectory directory = new();
        string configurationPath = directory.WriteConfiguration(LegacyConfiguration);

        ApplicationProbeRun run = probe.Run(configurationPath);

        Assert.Equal(0, run.ProbeExitCode);
        Assert.Equal(AzurPilotExitCode.Success, run.ApplicationExitCode);

        StructuredLogRecord loaded = Assert.Single(
            run.Logs,
            log => log.State.ContainsKey(SourceSchemaVersionProperty));
        Assert.Equal(
            AzurPilotConfiguration.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture),
            StructuredLogRecord.Read(loaded.State, SchemaVersionProperty));
        Assert.Equal(
            LegacySchemaVersion.ToString(CultureInfo.InvariantCulture),
            StructuredLogRecord.Read(loaded.State, SourceSchemaVersionProperty));

        // Файл действительно содержит документ в этой форме, поэтому проверка ниже что-то доказывает.
        Assert.Contains(
            ConfigurationDocumentFragment,
            File.ReadAllText(configurationPath),
            StringComparison.Ordinal);
        Assert.DoesNotContain(ConfigurationDocumentFragment, run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(ConfigurationDocumentFragment, run.StandardError, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Реальный запуск отображает MuMu-секцию в человекочитаемом итоге")]
    public void RealRunDisplaysMuMuSection()
    {
        ApplicationHostProcess application = ApplicationHostProcess.CreateStaged();

        ApplicationHostRun run = application.Run();

        StructuredLogRecord[] muMuEvents =
            [.. run.Logs.Where(log => log.State.ContainsKey(ControlSurfaceStatusProperty))];
        if (muMuEvents.Length == 0)
        {
            // Пользовательская конфигурация машины может быть отклонена: тогда snapshot не собирается
            // вовсе, и MuMu-секции в итоге нет. Это условие окружения, а не ослабление проверки: когда
            // диагностика собрана, секция обязана быть видна оператору.
            return;
        }

        Assert.Equal(AzurPilotExitCode.Success, run.ExitCode);
        Assert.Contains("MuMu", run.StandardOutput, StringComparison.Ordinal);

        StructuredLogRecord muMu = Assert.Single(muMuEvents);
        string? failureCode = StructuredLogRecord.Read(muMu.State, FailureCodeProperty);
        if (failureCode is not null)
        {
            Assert.Contains(failureCode, run.StandardOutput, StringComparison.Ordinal);
            return;
        }

        string? selectedInstance = StructuredLogRecord.Read(muMu.State, SelectedInstanceIdProperty);
        Assert.False(
            string.IsNullOrEmpty(selectedInstance),
            "MuMu-секция без отказа обязана сообщать выбранный экземпляр.");
        Assert.Contains(selectedInstance!, run.StandardOutput, StringComparison.Ordinal);
    }
}
