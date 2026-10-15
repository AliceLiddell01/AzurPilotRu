using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.App.LogViewer;
using AzurPilot.App.OperatorServices;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;
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
    /// <summary>Ключ регистрации границы запуска процесса для Android-возможности.</summary>
    /// <remarks>
    /// Ключ отделяет регистрацию Android от регистрации MuMu: граница запуска процесса принимает ровно
    /// одну проекцию отказов, поэтому одна и та же реализация подключается дважды — по одной на владельца
    /// смысла отказов.
    /// </remarks>
    private const string AndroidProcessRunnerKey = "android-adb";

    /// <summary>Собирает application host с явно заданным набором зависимостей.</summary>
    /// <param name="configuration">
    /// Результат загрузки пользовательской конфигурации: успешный snapshot либо ожидаемый отказ.
    /// </param>
    /// <returns>Построитель host-а, из которого тесты и startup получают реальные runtime services.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> равен <see langword="null"/>.</exception>
    public static HostApplicationBuilder CreateBuilder(ApplicationResult<AzurPilotConfigurationSnapshot> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return CreateBuilder(configuration, operatorLogBuffer: null, externalLoggerProvider: null);
    }

    private static HostApplicationBuilder CreateBuilder(
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration,
        AzurPilotOperatorLogBuffer? operatorLogBuffer,
        ILoggerProvider? externalLoggerProvider)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });

        ConfigureLogging(builder, configuration, operatorLogBuffer, externalLoggerProvider);
        RegisterRuntimeServices(builder, configuration);
        if (externalLoggerProvider is AzurPilotLogViewerSession viewerSession)
        {
            _ = builder.Services.AddSingleton(viewerSession);
        }

        return builder;
    }

    /// <summary>Запускает долгоживущий application host на время интерактивной операторской сессии.</summary>
    /// <param name="runSession">
    /// UI callback, вызываемый после startup-диагностики при живом host-е; получает эффективный режим
    /// интерфейса, уже разрешённый из явного аргумента запуска и пользовательской конфигурации.
    /// </param>
    /// <param name="configurationPath">Путь конфигурации или runtime-путь по умолчанию.</param>
    /// <param name="modeOverride">
    /// Режим интерфейса из явного аргумента запуска. Явный аргумент имеет приоритет над сохранённым
    /// значением конфигурации; <see langword="null"/> означает, что режим берётся из конфигурации.
    /// </param>
    /// <param name="cancellationToken">Токен отмены запуска и сессии.</param>
    /// <returns>Startup-итог и признак того, что UI session была запущена.</returns>
    public static async Task<AzurPilotInteractiveRunResult> RunInteractiveAsync(
        Func<AzurPilotOperatorService, AzurPilotDiagnosticReport, AzurPilotOperatorLogBuffer,
            OperatorUiMode, ApplicationFailure?, CancellationToken, Task> runSession,
        string? configurationPath = null,
        OperatorUiMode? modeOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runSession);

        AzurPilotOperation operation = AzurPilotOperation.Start("azurpilot.interactive.startup");
        AzurPilotOperatorLogBuffer logBuffer = new();
        AzurPilotLogViewerSession? logViewer = null;
        ApplicationFailure? logViewerFailure = null;
        IHost? host = null;
        bool hostStarted = false;
        bool sessionStarted = false;
        bool operationDisposed = false;
        AzurPilotStartupResult? startupResult = null;
        ILogger? logger = null;

        try
        {
            ApplicationResult<AzurPilotConfigurationSnapshot> configuration;
            try
            {
                using (operation.StartStep(AzurPilotOperation.ConfigurationActivityName))
                {
                    configuration = configurationPath is null
                        ? AzurPilotConfigurationLoader.LoadRuntime()
                        : AzurPilotConfigurationLoader.Load(configurationPath);
                }

                // Отдельная лог-консоль открывается до сборки host-а: её провайдер — часть logging stack,
                // а не второй владелец логирования. Отказ открытия окна не мешает запуску.
                if (configuration.IsSuccess && configuration.Value!.Configuration.Logging.Viewer.Enabled)
                {
                    logViewer = new AzurPilotLogViewerSession(configuration.Value.Configuration.Diagnostics.MinimumLevel);
                    ApplicationResult<bool> viewer = await logViewer.OpenAsync(cancellationToken).ConfigureAwait(false);
                    if (viewer.IsFailure)
                    {
                        logViewerFailure = viewer.FailureInfo;
                    }
                }

                host = CreateBuilder(configuration, logBuffer, logViewer).Build();
            }
            catch (Exception exception)
            {
                ApplicationFailure failure = NativeBoundaryFailureMapper.Map(exception);
                return new AzurPilotInteractiveRunResult(
                    AzurPilotStartupResult.Failure(operation.CorrelationId, failure),
                    InteractiveSessionStarted: false);
            }

            logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(AzurPilotLog.Category);
            logger.StartupStarted(operation.CorrelationId);

            if (logViewerFailure is ApplicationFailure viewerFailure)
            {
                // Отказ просмотра журнала — записываемый факт, а не причина отказа запуска.
                logger.LogViewerUnavailable(
                    operation.CorrelationId,
                    viewerFailure.Code,
                    viewerFailure.Message);
            }

            if (configuration.IsFailure)
            {
                ApplicationFailure configurationFailure = configuration.FailureInfo!;
                logger.ConfigurationRejected(operation.CorrelationId, configurationFailure.Code);
                return new AzurPilotInteractiveRunResult(
                    AzurPilotStartupResult.Failure(operation.CorrelationId, configurationFailure),
                    InteractiveSessionStarted: false);
            }

            AzurPilotConfigurationSnapshot snapshot = configuration.Value!;
            string configurationSource = snapshot.Source.ToString();
            string minimumLevel = snapshot.Configuration.Diagnostics.MinimumLevel.ToString();
            logger.ConfigurationLoaded(
                operation.CorrelationId,
                configurationSource,
                snapshot.EffectiveSchemaVersion,
                snapshot.SourceSchemaVersion,
                minimumLevel);

            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            hostStarted = true;

            // Снимок и результат startup готовы до UI, но сам host останется запущен до её закрытия.
            startupResult = await InspectStartedHostAsync(host, logger, operation, cancellationToken)
                .ConfigureAwait(false);
            if (!startupResult.IsSuccess || startupResult.Diagnostics is null)
            {
                return new AzurPilotInteractiveRunResult(startupResult, InteractiveSessionStarted: false);
            }

            // Startup activity не должна ошибочно коррелировать все последующие фоновые сообщения сессии.
            operation.Dispose();
            operationDisposed = true;
            sessionStarted = true;
            // Явный аргумент запуска приоритетнее сохранённого значения конфигурации.
            OperatorUiMode effectiveMode = modeOverride ?? snapshot.Configuration.Ui.Mode;
            await runSession(
                host.Services.GetRequiredService<AzurPilotOperatorService>(),
                startupResult.Diagnostics,
                logBuffer,
                effectiveMode,
                logViewerFailure,
                cancellationToken).ConfigureAwait(false);

            hostStarted = false;
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return new AzurPilotInteractiveRunResult(startupResult, InteractiveSessionStarted: true);
        }
        catch (Exception exception)
        {
            ApplicationFailure failure = NativeBoundaryFailureMapper.Map(exception);
            logger?.StartupRejected(
                operation.CorrelationId,
                failure.Code,
                failure.IsRetryable,
                failure.Message);
            AzurPilotStartupResult failed = AzurPilotStartupResult.Failure(
                operation.CorrelationId,
                failure,
                startupResult?.Diagnostics);
            return new AzurPilotInteractiveRunResult(failed, sessionStarted);
        }
        finally
        {
            if (host is not null)
            {
                if (hostStarted)
                {
                    try
                    {
                        hostStarted = false;
                        await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        // StopAsync выполняется ровно один раз; если он не удался, host всё равно будет
                        // освобождён. Ошибка shutdown уже не может быть безопасно возвращена из finally.
                        ApplicationFailure failure = NativeBoundaryFailureMapper.Map(exception);
                        logger?.OperatorHostStopFailed(
                            operation.CorrelationId,
                            failure.Code,
                            failure.Message);
                    }
                }

                host.Dispose();
            }
            else
            {
                logBuffer.Dispose();
            }

            // Отдельная лог-консоль завершается вместе с сессией: канал закрывается, viewer выходит сам,
            // поэтому осиротевших процессов не остаётся.
            if (logViewer is not null)
            {
                await logViewer.DisposeAsync().ConfigureAwait(false);
            }

            if (!operationDisposed)
            {
                operation.Dispose();
            }
        }
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

            using (operation.StartStep(AzurPilotOperation.ConfigurationActivityName))
            {
                configuration = configurationPath is null
                    ? AzurPilotConfigurationLoader.LoadRuntime()
                    : AzurPilotConfigurationLoader.Load(configurationPath);
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
    /// <param name="operatorLogBuffer">In-memory provider интерактивного режима или <see langword="null"/>.</param>
    /// <param name="externalLoggerProvider">
    /// Read-only провайдер отдельной лог-консоли либо <see langword="null"/>, если она не открыта.
    /// </param>
    private static void ConfigureLogging(
        HostApplicationBuilder builder,
        ApplicationResult<AzurPilotConfigurationSnapshot> configuration,
        AzurPilotOperatorLogBuffer? operatorLogBuffer,
        ILoggerProvider? externalLoggerProvider)
    {
        if (operatorLogBuffer is null)
        {
            // В режиме одноразовой диагностики structured runtime log остаётся JSON в stderr.
            _ = builder.Logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });

            _ = builder.Services.Configure<ConsoleLoggerOptions>(
                options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        }
        else
        {
            // Интерактивный экран получает тот же structured logging через bounded in-memory buffer.
            _ = builder.Logging.AddProvider(operatorLogBuffer);
        }

        if (externalLoggerProvider is not null)
        {
            // Отдельная лог-консоль — второй consumer того же потока, а не второй владелец логирования.
            _ = builder.Logging.AddProvider(externalLoggerProvider);
        }

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
            RegisterAndroidServices(builder);
            _ = builder.Services.AddSingleton<AzurPilotOperatorService>();
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
        // Граница запуска процесса общая для Windows-возможностей приложения, а смысл её отказов
        // принадлежит владельцу возможности: MuMu передаёт границе свою проекцию. Второй runner и второй
        // набор MuMu-кодов поэтому не заводятся, а проекция остаётся обязательной зависимостью границы.
        _ = builder.Services.AddSingleton<IProcessFailureProjection, MuMuProcessFailureProjection>();
        _ = builder.Services.AddSingleton<IWindowsProcessRunner, WindowsProcessRunner>();

        // Реализация выбирается явной фабрикой: MuMuWindowsHost имеет несколько конструкторов, и какой
        // из них использован, должно быть видно в composition root, а не выводиться DI-эвристикой.
        _ = builder.Services.AddSingleton<IMuMuHost>(
            static services => new MuMuWindowsHost(
                services.GetRequiredService<IMuMuInstallationRegistrySource>(),
                services.GetRequiredService<IMuMuInstallMetadataSource>(),
                services.GetRequiredService<IMuMuFileSystemProbe>(),
                services.GetRequiredService<IWindowsProcessRunner>(),
                services.GetRequiredService<ILogger<MuMuWindowsHost>>()));

        _ = builder.Services.AddSingleton<MuMuInstanceMutationGate>();
        _ = builder.Services.AddSingleton(TimeProvider.System);
        _ = builder.Services.AddSingleton(MuMuLifecycleTimings.Default);
        _ = builder.Services.AddSingleton<MuMuLifecycleService>();
    }

    /// <summary>Регистрирует Android-сервисы и lifecycle игры Azur Lane этого этапа в DI.</summary>
    /// <remarks>
    /// <para>
    /// Production host-side поверхность Android приходит из платформенной boundary
    /// <c>AzurPilot.Windows</c>: App связывает её с orchestration, не перенося в себя ни запуск процессов,
    /// ни разбор ответов ADB.
    /// </para>
    /// <para>
    /// Граница запуска процесса — та же реализация <see cref="WindowsProcessRunner"/>, что и у MuMu, но со
    /// своей проекцией отказов: конструктор границы принимает ровно одну проекцию, а смысл отказов
    /// принадлежит владельцу возможности. Поэтому для Android граница регистрируется под своим ключом:
    /// одна общая реализация, два владельца её отказов, ни второй реализации runner-а, ни второго набора
    /// Android-кодов.
    /// </para>
    /// <para>
    /// Часы — тот же <see cref="TimeProvider.System"/>, что уже зарегистрирован вместе с MuMu-сервисами:
    /// второй источник времени нарушил бы контракт deadline, поэтому здесь он повторно не регистрируется.
    /// </para>
    /// <para>
    /// Все Android-сервисы — singleton-ы одного application host. Координация mutation
    /// <see cref="AndroidGameMutationGate"/> обязана быть единственной на процесс, иначе process-local
    /// гарантия «одновременной mutation одного target-а нет» перестала бы действовать. Числа времени
    /// приходят от владельца <see cref="AndroidLifecycleTimings"/>, а не из литералов в сервисах.
    /// </para>
    /// <para>
    /// Регистрация ничего не выполняет: ни одна Android-операция здесь не запускается, и startup не
    /// подключает ADB и не запускает игру.
    /// </para>
    /// </remarks>
    /// <param name="builder">Построитель host-а.</param>
    private static void RegisterAndroidServices(HostApplicationBuilder builder)
    {
        // Проекция отказов Android и её граница запуска процесса: отдельная регистрация под ключом, чтобы
        // MuMu-проекция не подменяла смысл отказов ADB.
        _ = builder.Services.AddSingleton<AdbProcessFailureProjection>();
        _ = builder.Services.AddKeyedSingleton<IWindowsProcessRunner>(
            AndroidProcessRunnerKey,
            static (services, _) => new WindowsProcessRunner(
                services.GetRequiredService<AdbProcessFailureProjection>()));

        // Реализация выбирается явной фабрикой: AndroidWindowsHost имеет несколько конструкторов, и какой
        // из них использован, должно быть видно в composition root, а не выводиться DI-эвристикой.
        _ = builder.Services.AddSingleton<AndroidWindowsHost>(
            static services => new AndroidWindowsHost(
                services.GetRequiredKeyedService<IWindowsProcessRunner>(AndroidProcessRunnerKey)));
        // Lifecycle и screenshot capture используют один и тот же host, ADB discovery cache и process
        // boundary. Отдельная реализация под новым интерфейсом могла бы незаметно завести второй ADB cache.
        _ = builder.Services.AddSingleton<IAndroidHost>(
            static services => services.GetRequiredService<AndroidWindowsHost>());
        _ = builder.Services.AddSingleton<IAndroidScreenshotHost>(
            static services => services.GetRequiredService<AndroidWindowsHost>());

        _ = builder.Services.AddSingleton(AndroidLifecycleTimings.Default);
        _ = builder.Services.AddSingleton<AndroidGameMutationGate>();
        _ = builder.Services.AddSingleton<AndroidReadinessService>();
        _ = builder.Services.AddSingleton<AzurLaneGameStateService>();
        _ = builder.Services.AddSingleton<AzurLaneGameLifecycleService>();
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
            return await InspectStartedHostAsync(host, logger, operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Task<AzurPilotStartupResult> InspectStartedHostAsync(
        IHost host,
        ILogger logger,
        AzurPilotOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AzurPilotDiagnosticReport report;
        using (operation.StartStep(AzurPilotOperation.NativeDiagnosticsActivityName))
        {
            report = host.Services.GetRequiredService<AzurPilotDiagnosticService>().Capture();
        }

        // MuMu, Android и игра собираются существующими read-only diagnostic path-ами. Этот шаг
        // ничего не запускает и не подключает; при отсутствии устройств host остаётся доступным TUI.
        LogMuMuDiagnostics(logger, operation.CorrelationId, report.MuMu);
        LogAndroidDiagnostics(logger, operation.CorrelationId, report.Android);
        LogAzurLaneDiagnostics(logger, operation.CorrelationId, report.AzurLane);

        if (report.Native.Failure is ApplicationFailure nativeFailure)
        {
            logger.StartupRejected(
                operation.CorrelationId,
                nativeFailure.Code,
                nativeFailure.IsRetryable,
                nativeFailure.Message);
            return Task.FromResult(AzurPilotStartupResult.Failure(operation.CorrelationId, nativeFailure, report));
        }

        string opencvVersion = report.Native.OpencvVersion?.ToString() ?? string.Empty;
        string capabilities = string.Join(", ", report.Native.Capabilities);
        logger.NativeBoundaryVerified(
            operation.CorrelationId,
            report.Native.AbiVersion ?? 0,
            opencvVersion,
            capabilities);
        logger.StartupCompleted(operation.CorrelationId);
        return Task.FromResult(AzurPilotStartupResult.Success(operation.CorrelationId, report));
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

    /// <summary>Записывает bounded результат Android-диагностики в существующий structured log.</summary>
    /// <remarks>
    /// Событие остаётся в существующем logging stack и несёт correlation identifier операции, поэтому
    /// Android-диагностика одного запуска связана с остальными его событиями. Полный список устройств,
    /// вывод команд ADB и пути установки в событие не попадают.
    /// </remarks>
    /// <param name="logger">Логгер application host.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="android">Android-секция собранного диагностического snapshot.</param>
    private static void LogAndroidDiagnostics(
        ILogger logger,
        string correlationId,
        AzurPilotAndroidDiagnostics android)
    {
        // Отображаемые значения готовятся до вызова логирования: сообщение остаётся статическим
        // шаблоном, а его аргументы — уже готовыми строками.
        if (android.Failure is ApplicationFailure failure)
        {
            logger.AndroidDiagnosticsFailed(correlationId, android.Stage, failure.Code);
            return;
        }

        // Отображаемые значения готовятся до вызова логирования: сообщение остаётся статическим
        // шаблоном, а его аргументы — уже готовыми строками.
        string endpoint = android.Endpoint ?? string.Empty;
        string transportState = android.TransportState?.ToString() ?? string.Empty;

        logger.AndroidDiagnosticsCaptured(
            correlationId,
            android.IsAdbAvailable,
            endpoint,
            transportState);
    }

    /// <summary>Записывает bounded результат наблюдения состояния игры Azur Lane в structured log.</summary>
    /// <remarks>
    /// Событие остаётся в существующем logging stack и несёт correlation identifier операции, поэтому
    /// наблюдение одного запуска связано с остальными его событиями. Полный список процессов, вывод команд
    /// ADB и пути установки в событие не попадают.
    /// </remarks>
    /// <param name="logger">Логгер application host.</param>
    /// <param name="correlationId">Correlation identifier операции.</param>
    /// <param name="azurLane">Секция состояния игры собранного диагностического snapshot.</param>
    private static void LogAzurLaneDiagnostics(
        ILogger logger,
        string correlationId,
        AzurPilotAzurLaneDiagnostics azurLane)
    {
        // Отображаемые значения готовятся до вызова логирования: сообщение остаётся статическим
        // шаблоном, а его аргументы — уже готовыми строками.
        if (azurLane.Failure is ApplicationFailure failure)
        {
            logger.AzurLaneDiagnosticsFailed(
                correlationId,
                azurLane.Product,
                azurLane.Package,
                failure.Code);
            return;
        }

        string state = azurLane.State?.ToString() ?? string.Empty;

        logger.AzurLaneDiagnosticsCaptured(
            correlationId,
            azurLane.Product,
            azurLane.Package,
            state,
            azurLane.IsInstalled,
            azurLane.IsProcessRunning,
            azurLane.IsForeground);
    }
}
