using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AzurPilot.App;

/// <summary>
/// Composition root приложения: единственное место, где собирается application host и выполняется
/// startup.
/// </summary>
/// <remarks>
/// <para>
/// Host строится на <see cref="Host.CreateEmptyApplicationBuilder(HostApplicationBuilderSettings)"/>:
/// зависимости, configuration providers и logging providers добавляются осознанно, а не появляются
/// скрытым стандартным набором. Пользовательская конфигурация — один строгий JSON snapshot, поэтому
/// ни один configuration provider не подключается: загруженный snapshot отдаётся в DI.
/// </para>
/// <para>
/// Ожидаемые отказы (невалидная конфигурация, недоступная или несовместимая native boundary)
/// завершают запуск явным ненулевым кодом выхода и не маскируются как здоровый запуск.
/// </para>
/// </remarks>
public static class AzurPilotHost
{
    /// <summary>Собирает application host с явно заданным набором зависимостей.</summary>
    /// <param name="configuration">
    /// Результат загрузки пользовательской конфигурации: успешный snapshot либо ожидаемый отказ.
    /// </param>
    /// <returns>Построитель host-а, из которого тесты и startup получают реальные runtime services.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> равен <see langword="null"/>.</exception>
    public static HostApplicationBuilder CreateBuilder(ApplicationResult<AzurPilotConfigurationSnapshot> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });

        ConfigureLogging(builder, configuration);
        RegisterRuntimeServices(builder, configuration);
        return builder;
    }

    /// <summary>Выполняет startup приложения: загрузка конфигурации, host, диагностика, итог.</summary>
    /// <param name="configurationPath">
    /// Явный абсолютный путь файла конфигурации; <see langword="null"/> — runtime-путь по умолчанию,
    /// владельцем которого остаётся <see cref="AzurPilotConfigurationPath"/>.
    /// </param>
    /// <param name="cancellationToken">Токен отмены операции запуска.</param>
    /// <returns>Типизированный результат запуска с кодом выхода и человекочитаемым отчётом.</returns>
    public static async Task<AzurPilotStartupResult> RunAsync(
        string? configurationPath = null,
        CancellationToken cancellationToken = default)
    {
        string path = configurationPath ?? AzurPilotConfigurationPath.GetDefaultRuntimePath();
        using AzurPilotOperation operation = AzurPilotOperation.Start();

        ApplicationResult<AzurPilotConfigurationSnapshot> configuration;
        using (operation.StartStep(AzurPilotOperation.ConfigurationActivityName))
        {
            configuration = AzurPilotConfigurationLoader.Load(path);
        }

        HostApplicationBuilder builder = CreateBuilder(configuration);
        using IHost host = builder.Build();
        ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(AzurPilotLog.Category);

        try
        {
            logger.StartupStarted(operation.CorrelationId);

            if (configuration.IsFailure)
            {
                ApplicationFailure configurationFailure = configuration.FailureInfo!;
                logger.ConfigurationRejected(operation.CorrelationId, configurationFailure.Code);
                return AzurPilotStartupResult.Failure(operation.CorrelationId, configurationFailure);
            }

            AzurPilotConfigurationSnapshot snapshot = configuration.Value!;

            // Отображаемые значения готовятся до вызова логирования: сообщение остаётся статическим
            // шаблоном, а его аргументы — уже готовыми строками.
            string configurationSource = snapshot.Source.ToString();
            string minimumLevel = snapshot.Configuration.Diagnostics.MinimumLevel.ToString();

            logger.ConfigurationLoaded(
                operation.CorrelationId,
                configurationSource,
                snapshot.Configuration.SchemaVersion,
                minimumLevel);

            return await RunHostAsync(host, logger, operation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Ожидаемый отказ возвращается значением; сюда попадают только непредвиденные ошибки
            // startup. Их проекция в application failure делает исход запуска единообразным.
            ApplicationFailure failure = NativeBoundaryFailureMapper.Map(exception);
            logger.StartupRejected(operation.CorrelationId, failure.Code, failure.IsRetryable, failure.Message);
            return AzurPilotStartupResult.Failure(operation.CorrelationId, failure);
        }
    }

    /// <summary>Настраивает единственный logging provider приложения.</summary>
    /// <param name="builder">Построитель host-а.</param>
    /// <param name="configuration">Результат загрузки конфигурации.</param>
    private static void ConfigureLogging(
        HostApplicationBuilder builder,
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration)
    {
        // Единственный logging provider — встроенный JSON console formatter, и весь structured runtime
        // log направлен в stderr: stdout остаётся поверхностью человекочитаемого application output и
        // будущего machine-output. Debug/EventSource/EventLog providers не подключаются.
        _ = builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });

        _ = builder.Services.Configure<ConsoleLoggerOptions>(
            options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // Штатные сообщения lifetime generic host («Application started», «Content root path») описывают
        // долгоживущий интерактивный host и к startup-операции приложения не относятся. Предупреждения и
        // ошибки этой категории остаются видимыми: фильтр поднимает порог, а не скрывает сбои.
        _ = builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        // Correlation identifier операции должен попадать в каждую запись, а не только в те события,
        // где он передан явно.
        _ = builder.Services.Configure<LoggerFactoryOptions>(options => options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);

        // Минимальный уровень логирования принадлежит загруженному snapshot. Если конфигурация
        // отклонена, snapshot не существует: тогда действует встроенный уровень, потому что настройку
        // отвергнутого файла применять нельзя.
        _ = builder.Logging.SetMinimumLevel(
            configuration.IsSuccess
                ? configuration.Value!.Configuration.Diagnostics.MinimumLevel
                : LogLevel.Information);
    }

    /// <summary>Регистрирует реальные runtime services этого этапа в DI.</summary>
    /// <param name="builder">Построитель host-а.</param>
    /// <param name="configuration">Результат загрузки конфигурации.</param>
    private static void RegisterRuntimeServices(
        HostApplicationBuilder builder,
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration)
    {
        // Маппер native boundary — production-проекция платформенных ошибок в application failure.
        // Host не создаёт её вручную и не дублирует: он получает готовую проекцию из DI.
        Func<Exception, ApplicationFailure> mapFailure = NativeBoundaryFailureMapper.Map;
        _ = builder.Services.AddSingleton(mapFailure);

        if (configuration.IsSuccess)
        {
            // Конфигурация загружена ровно один раз: в DI попадает готовый snapshot, а не повторная
            // загрузка, и hot reload/file watcher отсутствует.
            _ = builder.Services.AddSingleton(configuration.Value!);
            _ = builder.Services.AddSingleton<AzurPilotDiagnosticService>();
        }
    }

    /// <summary>Запускает host, выполняет диагностическую операцию и завершает host.</summary>
    /// <param name="host">Построенный application host.</param>
    /// <param name="logger">Логгер application host.</param>
    /// <param name="operation">Correlation-операция запуска.</param>
    /// <param name="cancellationToken">Токен отмены операции запуска.</param>
    /// <returns>Типизированный результат запуска.</returns>
    private static async Task<AzurPilotStartupResult> RunHostAsync(
        IHost host,
        ILogger logger,
        AzurPilotOperation operation,
        CancellationToken cancellationToken)
    {
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AzurPilotDiagnosticReport report;
            using (operation.StartStep(AzurPilotOperation.NativeDiagnosticsActivityName))
            {
                report = host.Services.GetRequiredService<AzurPilotDiagnosticService>().Capture();
            }

            if (report.Native.Failure is not null)
            {
                ApplicationFailure nativeFailure = report.Native.Failure;
                logger.StartupRejected(
                    operation.CorrelationId,
                    nativeFailure.Code,
                    nativeFailure.IsRetryable,
                    nativeFailure.Message);
                return AzurPilotStartupResult.Failure(operation.CorrelationId, nativeFailure, report);
            }

            // Отображаемые значения готовятся до вызова логирования: сообщение остаётся статическим
            // шаблоном, а его аргументы — уже готовыми строками.
            string opencvVersion = report.Native.OpencvVersion?.ToString() ?? string.Empty;
            string capabilities = string.Join(", ", report.Native.Capabilities);

            logger.NativeBoundaryVerified(
                operation.CorrelationId,
                report.Native.AbiVersion ?? 0,
                opencvVersion,
                capabilities);
            logger.StartupCompleted(operation.CorrelationId);

            return AzurPilotStartupResult.Success(operation.CorrelationId, report);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
