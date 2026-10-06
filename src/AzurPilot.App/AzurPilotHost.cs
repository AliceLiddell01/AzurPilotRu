using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using AzurPilot.Windows.MuMu;
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
/// <para>
/// Непредвиденная ошибка startup проецируется в application-отказ тем же production-маппером, поэтому
/// исход запуска единообразен и до появления логгера, и после него.
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
        // Операция начинается первой: её correlation identifier нужен и человекочитаемому итогу, и каждой
        // записи диагностики. Собственный отказ операции поэтому не проецируется в результат запуска —
        // идентичности операции ещё нет, а итог без неё неполон.
        using AzurPilotOperation operation = AzurPilotOperation.Start();

        ApplicationResult<AzurPilotConfigurationSnapshot> configuration;
        IHost host;
        try
        {
            string path = configurationPath ?? AzurPilotConfigurationPath.GetDefaultRuntimePath();

            using (operation.StartStep(AzurPilotOperation.ConfigurationActivityName))
            {
                configuration = AzurPilotConfigurationLoader.Load(path);
            }

            host = CreateBuilder(configuration).Build();
        }
        catch (Exception exception)
        {
            // Непредвиденная ошибка startup до появления логгера: исход запуска всё равно единообразен,
            // но записать отказ в structured log ещё нечем.
            return AzurPilotStartupResult.Failure(
                operation.CorrelationId,
                NativeBoundaryFailureMapper.Map(exception));
        }

        using (host)
        {
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
                    snapshot.EffectiveSchemaVersion,
                    snapshot.SourceSchemaVersion,
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
        // отвергнутого файла применять нельзя. Значение встроенного уровня читается у его владельца
        // AzurPilotConfigurationDefaults: второго литерала уровня в App нет.
        _ = builder.Logging.SetMinimumLevel(
            configuration.IsSuccess
                ? configuration.Value!.Configuration.Diagnostics.MinimumLevel
                : AzurPilotConfigurationDefaults.Create().Diagnostics.MinimumLevel);
    }

    /// <summary>Регистрирует реальные runtime services этого этапа в DI.</summary>
    /// <param name="builder">Построитель host-а.</param>
    /// <param name="configuration">Результат загрузки конфигурации.</param>
    private static void RegisterRuntimeServices(
        HostApplicationBuilder builder,
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration)
    {
        // Маппер native boundary — production-проекция платформенных ошибок в application failure.
        // Host не создаёт её вручную и не дублирует: он получает готовые проекции из DI — и для ошибки
        // границы, и для несовместимости, подтверждённой значением проверки контракта.
        Func<Exception, ApplicationFailure> mapFailure = NativeBoundaryFailureMapper.Map;
        _ = builder.Services.AddSingleton(mapFailure);
        _ = builder.Services.AddSingleton<Func<string, ApplicationFailure>>(
            NativeBoundaryFailureMapper.MapIncompatibility);

        if (configuration.IsSuccess)
        {
            // Конфигурация загружена ровно один раз: в DI попадает готовый snapshot, а не повторная
            // загрузка, и hot reload/file watcher отсутствует.
            _ = builder.Services.AddSingleton(configuration.Value!);
            _ = builder.Services.AddSingleton<AzurPilotDiagnosticService>();

            RegisterMuMuServices(builder);
        }
    }

    /// <summary>Регистрирует MuMu-сервисы этого этапа в DI.</summary>
    /// <remarks>
    /// <para>
    /// Production host-side поверхность MuMu приходит из платформенной boundary
    /// <c>AzurPilot.Windows</c>: App только связывает узкие внешние границы адаптеров, не перенося в себя
    /// ни registry, ни файловую систему, ни запуск процессов.
    /// </para>
    /// <para>
    /// Все MuMu-зависимости — singleton-ы одного application host. Координация mutation
    /// <see cref="MuMuInstanceMutationGate"/> обязана быть единственной на процесс, иначе гарантия
    /// «одновременных mutation одного экземпляра нет» перестала бы действовать. Числа времени приходят
    /// от владельца <see cref="MuMuLifecycleTimings"/>, часы — от <see cref="TimeProvider.System"/>:
    /// optional-зависимостей с молчаливыми значениями у orchestration нет.
    /// </para>
    /// <para>
    /// MuMu-операции резолвятся из DI: ни один MuMu-сервис не создаётся вручную и второй экземпляр
    /// координации не заводится.
    /// </para>
    /// </remarks>
    /// <param name="builder">Построитель host-а.</param>
    private static void RegisterMuMuServices(HostApplicationBuilder builder)
    {
        _ = builder.Services.AddSingleton<IMuMuFileSystemProbe, WindowsMuMuFileSystemProbe>();
        _ = builder.Services.AddSingleton<IMuMuInstallationRegistrySource, WindowsMuMuInstallationRegistrySource>();
        _ = builder.Services.AddSingleton<IMuMuInstallMetadataSource>(
            static services => new WindowsMuMuInstallMetadataSource(
                services.GetRequiredService<IMuMuFileSystemProbe>()));
        _ = builder.Services.AddSingleton<IMuMuProcessRunner, MuMuProcessRunner>();

        // Реализация выбирается явной фабрикой: MuMuWindowsHost имеет несколько конструкторов, и какой
        // из них использован, должно быть видно в composition root, а не выводиться DI-эвристикой.
        _ = builder.Services.AddSingleton<IMuMuHost>(
            static services => new MuMuWindowsHost(
                services.GetRequiredService<IMuMuInstallationRegistrySource>(),
                services.GetRequiredService<IMuMuInstallMetadataSource>(),
                services.GetRequiredService<IMuMuFileSystemProbe>(),
                services.GetRequiredService<IMuMuProcessRunner>(),
                services.GetRequiredService<ILogger<MuMuWindowsHost>>()));

        _ = builder.Services.AddSingleton<MuMuInstanceMutationGate>();
        _ = builder.Services.AddSingleton(TimeProvider.System);
        _ = builder.Services.AddSingleton(MuMuLifecycleTimings.Default);
        _ = builder.Services.AddSingleton<MuMuLifecycleService>();
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

            // MuMu-секция — диагностический результат, а не причина отказа: отсутствие установки,
            // неподдерживаемая control surface и остановленный экземпляр остаются данными, и startup их
            // не «исправляет». Запуск эмулятора диагностикой не выполняется никогда.
            LogMuMuDiagnostics(logger, operation.CorrelationId, report.MuMu);

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

    /// <summary>Записывает bounded результат MuMu-диагностики в существующий structured log.</summary>
    /// <remarks>
    /// Событие остаётся в существующем logging stack и несёт correlation identifier операции, поэтому
    /// MuMu-диагностика одного запуска связана с остальными его событиями. Полный вывод control utility,
    /// список процессов и пути установки в событие не попадают.
    /// </remarks>
    /// <param name="logger">Логгер application host.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="muMu">MuMu-секция собранного диагностического snapshot.</param>
    private static void LogMuMuDiagnostics(
        ILogger logger,
        string correlationId,
        AzurPilotMuMuDiagnostics muMu)
    {
        // Отображаемые значения готовятся до вызова логирования: сообщение остаётся статическим
        // шаблоном, а его аргументы — уже готовыми строками.
        if (muMu.Failure is ApplicationFailure failure)
        {
            logger.MuMuDiagnosticsFailed(correlationId, failure.Code, muMu.ControlSurfaceStatus);
            return;
        }

        logger.MuMuDiagnosticsCaptured(
            correlationId,
            muMu.IsInstallationDiscovered,
            muMu.Version ?? string.Empty,
            muMu.ControlSurfaceStatus,
            muMu.ConfiguredInstance,
            muMu.SelectedInstanceId ?? string.Empty);
    }
}
