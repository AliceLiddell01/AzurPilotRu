using AzurPilot.App;
using AzurPilot.Core.Failures;
using AzurPilot.Tests.Configuration;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Проверки реального процесса <c>AzurPilot.App.exe</c> — так, как его запускает пользователь: без
/// аргументов и с runtime-путём конфигурации по умолчанию.
/// </summary>
/// <remarks>
/// <para>
/// Продукт не разбирает аргументы запуска, поэтому проверки формулируются свойствами, которые верны и
/// при отсутствующем, и при существующем (в том числе невалидном) пользовательском
/// <c>%LOCALAPPDATA%\AzurPilot\config.json</c>: код выхода обязан быть согласован с отчётом, отказ
/// никогда не маскируется как здоровый запуск, а structured runtime log не попадает в stdout.
/// </para>
/// <para>
/// Application-level доказательства конкретных исходов (native_unavailable, native_incompatible,
/// configuration_invalid, configuration_schema_unsupported и здоровый snapshot) получает проба:
/// <see cref="ApplicationCompositionProbeTests"/> выполняет composition с явно переданным путём
/// конфигурации, потому что пользовательской опции для этого у продукта нет.
/// </para>
/// <para>
/// Файл пользователя не подменяется, не создаётся и не удаляется: тесты его не читают и не пишут.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AzurPilotApplicationProcessTests
{
    /// <summary>Длина correlation identifier операции.</summary>
    private const int CorrelationIdLength = 32;

    private const string InvalidConfiguration =
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Verbose"}}""";

    [Fact(DisplayName = "Реальный запуск: stdout без structured JSON, stderr только project-owned события")]
    public void StructuredLogsStayOffStandardOutput()
    {
        ApplicationHostProcess application = ApplicationHostProcess.CreateStaged();

        ApplicationHostRun run = application.Run();

        // stdout — поверхность человекочитаемого итога: ни одной записи structured runtime log.
        Assert.NotEmpty(run.StandardOutput.Trim());
        Assert.Empty(StructuredLogRecord.ReadFrom(run.StandardOutput));

        // stderr — только project-owned события приложения: чужих категорий логирования здесь нет.
        Assert.All(run.Logs, log => Assert.Equal("AzurPilot.App", log.Category));
    }

    [Fact(DisplayName = "Код выхода согласован с отчётом: healthy — ноль, отказ — ненулевой и не маскируется")]
    public void ExitCodeAgreesWithReportedOutcome()
    {
        ApplicationHostProcess application = ApplicationHostProcess.CreateStaged();

        ApplicationHostRun run = application.Run();

        string? failureCode = ReadFailureCodeOrNull(run);
        if (failureCode is null)
        {
            // Ни одно событие не заявляет отказ: запуск обязан быть успешным.
            Assert.Equal(AzurPilotExitCode.Success, run.ExitCode);
            return;
        }

        // Отказ заявлен: он обязан быть виден и в человекочитаемом итоге, и в коде выхода, причём код
        // выхода берётся из того же соответствия, что и в продукте.
        Assert.NotEqual(AzurPilotExitCode.Success, run.ExitCode);
        Assert.Equal(ExpectedExitCode(failureCode), run.ExitCode);
        Assert.Contains(failureCode, run.StandardOutput, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Один correlation identifier связывает события запуска и человекочитаемый итог")]
    public void CorrelationIdentifierLinksOneLaunch()
    {
        ApplicationHostProcess application = ApplicationHostProcess.CreateStaged();

        ApplicationHostRun run = application.Run();

        // Человекочитаемый итог всегда описывает операцию запуска.
        IReadOnlyList<string> reportedIdentifiers = ReadIdentifiers(run.StandardOutput);
        Assert.NotEmpty(reportedIdentifiers);

        // События, которые прошли политику логирования пользовательской конфигурации, принадлежат одной
        // операции и несут тот же identifier, что и итог: identifier приходит из Activity операции, а не
        // печатается внутри одного сообщения.
        if (run.Logs.Count == 0)
        {
            return;
        }

        string identifier = Assert.Single(
            run.Logs.Select(log => log.CorrelationId).OfType<string>().Distinct(StringComparer.Ordinal));
        Assert.Equal(CorrelationIdLength, identifier.Length);
        Assert.All(run.Logs, log => Assert.Equal(identifier, log.CorrelationId));
        Assert.All(run.Logs, log => Assert.Equal(identifier, log.TraceId));
        Assert.Contains(identifier, reportedIdentifiers);
    }

    [Fact(DisplayName = "Продукт не разбирает аргументы запуска: переданный путь конфигурации игнорируется")]
    public void ApplicationIgnoresCommandLineArguments()
    {
        ApplicationHostProcess application = ApplicationHostProcess.CreateStaged();
        using TemporaryConfigurationDirectory directory = new();
        string configurationPath = directory.WriteConfiguration(InvalidConfiguration);

        ApplicationHostRun plain = application.Run();
        ApplicationHostRun withArguments = application.Run("--config", configurationPath);

        // Аргументы не влияют ни на код выхода, ни на заявленный отказ: продуктового CLI нет.
        Assert.Equal(plain.ExitCode, withArguments.ExitCode);
        Assert.Equal(ReadFailureCodeOrNull(plain), ReadFailureCodeOrNull(withArguments));

        // Файл по переданному пути не читается: его путь не появляется в человекочитаемом итоге.
        Assert.DoesNotContain(configurationPath, withArguments.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>Возвращает код выхода, соответствующий заявленному application-level отказу.</summary>
    /// <param name="failureCode">Код отказа, прочитанный из structured diagnostics.</param>
    /// <returns>Код выхода процесса из контракта <see cref="AzurPilotExitCode"/>.</returns>
    private static int ExpectedExitCode(string failureCode)
        => AzurPilotExitCode.FromFailure(
            new ApplicationFailure
            {
                Code = failureCode,
                Message = "Отказ, прочитанный из structured diagnostics реального процесса.",
            });

    /// <summary>Читает application-level код отказа, если запуск его заявил.</summary>
    /// <param name="run">Результат запуска приложения.</param>
    /// <returns>Код отказа либо <see langword="null"/>, если отказа не было.</returns>
    private static string? ReadFailureCodeOrNull(ApplicationHostRun run)
        => run.Logs.Select(log => log.FailureCode).FirstOrDefault(code => code is not null);

    /// <summary>Находит correlation identifiers операции в человекочитаемом итоге.</summary>
    /// <param name="text">Содержимое stdout процесса.</param>
    /// <returns>Токены длиной correlation identifier, состоящие только из hex-цифр.</returns>
    private static List<string> ReadIdentifiers(string text)
    {
        List<string> identifiers = [];
        foreach (string token in text.Split(
            [' ', '\t', '\r', '\n', ':', ';', ',', '«', '»', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == CorrelationIdLength && token.All(Uri.IsHexDigit))
            {
                identifiers.Add(token);
            }
        }

        return identifiers;
    }
}
