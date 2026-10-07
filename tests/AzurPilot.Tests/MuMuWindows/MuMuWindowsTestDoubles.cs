using AzurPilot.Core.Failures;
using AzurPilot.Windows.MuMu;
using AzurPilot.Windows.Processes;
using Microsoft.Extensions.Logging;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Записывающий логгер host-а: доказывает, что host пишет в существующий logging stack.
/// </summary>
internal sealed class RecordingMuMuHostLogger : ILogger<MuMuWindowsHost>
{
    /// <summary>Записанные события в порядке поступления.</summary>
    internal List<RecordedLogEntry> Entries { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        Entries.Add(new RecordedLogEntry(logLevel, eventId.Id, formatter(state, exception)));
    }
}

/// <summary>Записанное событие лога.</summary>
/// <param name="Level">Уровень события.</param>
/// <param name="EventId">Идентификатор события.</param>
/// <param name="Message">Отформатированное сообщение.</param>
internal readonly record struct RecordedLogEntry(LogLevel Level, int EventId, string Message);

/// <summary>
/// Подменяемые внешние границы Windows-адаптеров MuMu и пути, не являющиеся machine-specific
/// константами.
/// </summary>
/// <remarks>
/// Все проверки в этом каталоге работают через эти границы: ни установленная MuMu, ни реальный
/// control utility не требуются. Абсолютные пути собираются в runtime из временного каталога, поэтому
/// в исходниках проверок нет ни буквы диска, ни пути конкретной машины.
/// </remarks>
internal static class MuMuWindowsTestPaths
{
    /// <summary>Собирает абсолютный путь проверки под временным каталогом.</summary>
    /// <param name="segments">Сегменты пути.</param>
    /// <returns>Абсолютный путь.</returns>
    internal static string Create(params string[] segments)
        => Path.Combine([Path.GetTempPath(), "azurpilot-mumu-windows-tests", .. segments]);

    /// <summary>Экранирует путь для подстановки в JSON-документ.</summary>
    /// <param name="path">Абсолютный путь.</param>
    /// <returns>Путь с экранированными обратными слэшами.</returns>
    internal static string EscapeForJson(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}

/// <summary>Записанный запрос запуска процесса вместе с исходом, который вернула подмена.</summary>
/// <param name="Request">Запрос, полученный подменой.</param>
/// <param name="CancellationToken">Токен отмены, переданный подмене.</param>
internal readonly record struct RecordedProcessRequest(
    WindowsProcessRequest Request,
    CancellationToken CancellationToken);

/// <summary>
/// Подмена границы запуска процесса: возвращает заранее заданные исходы и запоминает запросы.
/// </summary>
internal sealed class FakeWindowsProcessRunner : IWindowsProcessRunner
{
    private readonly Queue<ApplicationResult<WindowsProcessOutcome>> _scripted = new();
    private readonly List<RecordedProcessRequest> _requests = [];

    /// <summary>Запросы, полученные подменой, в порядке вызова.</summary>
    internal IReadOnlyList<RecordedProcessRequest> Requests => _requests;

    /// <summary>Добавляет исход завершившегося процесса.</summary>
    /// <param name="exitCode">Код выхода процесса.</param>
    /// <param name="standardOutput">Захваченный stdout.</param>
    /// <param name="standardError">Захваченный stderr.</param>
    /// <param name="standardOutputTruncated">Признак усечения stdout.</param>
    /// <param name="standardErrorTruncated">Признак усечения stderr.</param>
    internal void EnqueueOutcome(
        int exitCode,
        string standardOutput,
        string standardError = "",
        bool standardOutputTruncated = false,
        bool standardErrorTruncated = false)
        => _scripted.Enqueue(ApplicationResult<WindowsProcessOutcome>.Success(new WindowsProcessOutcome
        {
            ExitCode = exitCode,
            StandardOutput = standardOutput,
            StandardError = standardError,
            Duration = TimeSpan.FromMilliseconds(17),
            StandardOutputTruncated = standardOutputTruncated,
            StandardErrorTruncated = standardErrorTruncated,
        }));

    /// <summary>Добавляет исход-отказ границы запуска.</summary>
    /// <param name="failure">Отказ, который должна вернуть подмена.</param>
    internal void EnqueueFailure(ApplicationFailure failure)
        => _scripted.Enqueue(ApplicationResult<WindowsProcessOutcome>.Failure(failure));

    /// <inheritdoc />
    public Task<ApplicationResult<WindowsProcessOutcome>> RunAsync(
        WindowsProcessRequest request,
        CancellationToken cancellationToken)
    {
        _requests.Add(new RecordedProcessRequest(request, cancellationToken));

        if (_scripted.Count == 0)
        {
            throw new InvalidOperationException("Подмена границы процесса не получила запрограммированного исхода.");
        }

        return Task.FromResult(_scripted.Dequeue());
    }
}

/// <summary>Подмена границы файловой системы: каталоги и файлы задаются содержимым.</summary>
internal sealed class FakeMuMuFileSystemProbe : IMuMuFileSystemProbe
{
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Регистрирует существующий каталог.</summary>
    /// <param name="absolutePath">Абсолютный путь каталога.</param>
    internal void AddDirectory(string absolutePath) => _directories.Add(absolutePath);

    /// <summary>Регистрирует существующий файл с содержимым.</summary>
    /// <param name="absolutePath">Абсолютный путь файла.</param>
    /// <param name="content">Содержимое файла.</param>
    internal void AddFile(string absolutePath, string content) => _files[absolutePath] = content;

    /// <inheritdoc />
    public bool FileExists(string absolutePath) => _files.ContainsKey(absolutePath);

    /// <inheritdoc />
    public bool DirectoryExists(string absolutePath) => _directories.Contains(absolutePath);

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string absolutePath)
        => [.. _directories.Where(directory => string.Equals(
            Path.GetDirectoryName(directory),
            absolutePath,
            StringComparison.OrdinalIgnoreCase))];

    /// <inheritdoc />
    public string ReadAllText(string absolutePath)
        => _files.TryGetValue(absolutePath, out string? content)
            ? content
            : throw new FileNotFoundException("Файл не зарегистрирован в подмене файловой системы.", absolutePath);
}

/// <summary>Подмена источника uninstall-записей реестра.</summary>
internal sealed class FakeMuMuInstallationRegistrySource : IMuMuInstallationRegistrySource
{
    private readonly List<MuMuRegistryCandidate> _candidates = [];

    /// <summary>Исключение, которым должна завершиться попытка чтения реестра.</summary>
    internal Exception? Failure { get; set; }

    /// <summary>Добавляет запись uninstall-раздела.</summary>
    /// <param name="candidate">Запись установки.</param>
    internal void Add(MuMuRegistryCandidate candidate) => _candidates.Add(candidate);

    /// <inheritdoc />
    public IReadOnlyList<MuMuRegistryCandidate> ReadCandidates()
        => Failure is null ? _candidates : throw Failure;
}

/// <summary>Подмена источника документов install metadata.</summary>
internal sealed class FakeMuMuInstallMetadataSource : IMuMuInstallMetadataSource
{
    private readonly List<MuMuInstallMetadataDocument> _documents = [];

    /// <summary>Исключение, которым должна завершиться попытка чтения документов.</summary>
    internal Exception? Failure { get; set; }

    /// <summary>Добавляет документ install metadata.</summary>
    /// <param name="filePath">Абсолютный путь документа.</param>
    /// <param name="content">Содержимое документа.</param>
    internal void Add(string filePath, string content)
        => _documents.Add(new MuMuInstallMetadataDocument { FilePath = filePath, Content = content });

    /// <inheritdoc />
    public IReadOnlyList<MuMuInstallMetadataDocument> ReadDocuments()
        => Failure is null ? _documents : throw Failure;
}

/// <summary>Проверочные данные, снятые с реальной установки MuMuPlayer.</summary>
internal static class MuMuObservedPayloads
{
    /// <summary>Ответ <c>version</c> реальной установки.</summary>
    internal const string VersionResponse = """
        {
          "version": "6.8.0.0"
        }
        """;

    /// <summary>Ответ <c>info --vmindex &lt;n&gt;</c> для запущенного экземпляра.</summary>
    internal const string RunningInstanceResponse = """
        {
          "adb_host_ip": "127.0.0.1",
          "adb_port": 16416,
          "android_version": "15.0",
          "created_timestamp": 1785242087433799,
          "disk_size_bytes": 59294197214,
          "error_code": 0,
          "hyperv_enabled": true,
          "index": "1",
          "info_source": "rpc",
          "is_android_started": true,
          "is_main": false,
          "is_process_started": true,
          "launch_err_code": 0,
          "launch_err_msg": "",
          "launch_time": 1602875,
          "main_wnd": "009E0D78",
          "name": "Azur lane",
          "pid": 31176,
          "player_state": "start_finished",
          "render_wnd": "000D0F98",
          "vt_enabled": true
        }
        """;

    /// <summary>Ответ <c>info --vmindex &lt;n&gt;</c> для остановленного экземпляра: поля <c>player_state</c> нет.</summary>
    internal const string StoppedInstanceResponse = """
        {
          "android_version": "15.0",
          "created_timestamp": 1785242087433799,
          "disk_size_bytes": 59294196788,
          "error_code": 0,
          "hyperv_enabled": true,
          "index": "1",
          "info_source": "local",
          "is_android_started": false,
          "is_main": false,
          "is_process_started": false,
          "name": "Azur lane"
        }
        """;

    /// <summary>Ответ провайдера на несуществующий номер экземпляра.</summary>
    internal const string IndexNotFoundResponse = """
        {
          "errcode": -200,
          "errmsg": "player index not found",
          "info_source": "rpc"
        }
        """;

    /// <summary>Код выхода процесса для ответа о несуществующем номере экземпляра.</summary>
    internal const int IndexNotFoundExitCode = -200;

    /// <summary>Ответ перечисления, в котором остановленный экземпляр остаётся в списке.</summary>
    internal const string SingleStoppedEnumerationResponse = """
        {
          "1": {
            "android_version": "15.0",
            "created_timestamp": 1785242087433799,
            "disk_size_bytes": 59294196788,
            "error_code": 0,
            "hyperv_enabled": true,
            "index": "1",
            "info_source": "local",
            "is_android_started": false,
            "is_main": false,
            "is_process_started": false,
            "name": "Azur lane"
          }
        }
        """;

    /// <summary>Ответ перечисления с двумя экземплярами разного состояния.</summary>
    internal const string TwoInstancesEnumerationResponse = """
        {
          "1": {
            "android_version": "15.0",
            "created_timestamp": 1785242087433799,
            "error_code": 0,
            "index": "1",
            "is_android_started": true,
            "is_process_started": true,
            "name": "Azur lane",
            "pid": 31176,
            "player_state": "start_finished"
          },
          "2": {
            "android_version": "12.0",
            "created_timestamp": 1785242087433800,
            "error_code": 0,
            "index": "2",
            "is_android_started": false,
            "is_process_started": false,
            "name": "Второй экземпляр"
          }
        }
        """;

    /// <summary>Ответ перечисления нескольких номеров, часть которых не существует.</summary>
    internal const string PartialEnumerationResponse = """
        {
          "1": {
            "android_version": "15.0",
            "index": "1",
            "is_android_started": true,
            "is_process_started": true,
            "name": "Azur lane",
            "player_state": "start_finished"
          },
          "2": {
            "errcode": -200,
            "errmsg": "player index not found",
            "info_source": "rpc"
          }
        }
        """;

    /// <summary>Ответ операции изменения состояния, принятой провайдером.</summary>
    internal const string AcceptedControlResponse = """
        {
          "errcode": 0,
          "errmsg": ""
        }
        """;
}

/// <summary>
/// Собирает установку MuMu на подменяемых границах реестра, install metadata и файловой системы.
/// </summary>
/// <remarks>
/// Один владелец формы установки для проверок: и обнаружение, и host используют одну и ту же раскладку
/// каталогов и один и тот же документ install metadata, поэтому проверки не расходятся в ожиданиях.
/// Абсолютные пути собираются в runtime, machine-specific констант в исходниках нет.
/// </remarks>
internal static class MuMuTestInstallation
{
    /// <summary>Версия установки, подтверждённая на реальной установке.</summary>
    internal const string Version = "6.8.0.0";

    /// <summary>Имя продукта из install metadata реальной установки.</summary>
    internal const string ProductName = "MuMuPlayerGlobal";

    /// <summary>Идентификатор продукта из install metadata реальной установки.</summary>
    internal const string ProductId = "MuMu6.0";

    /// <summary>Добавляет установку во все три подменяемые границы.</summary>
    /// <param name="registry">Подмена источника uninstall-записей.</param>
    /// <param name="metadata">Подмена источника install metadata.</param>
    /// <param name="fileSystem">Подмена границы файловой системы.</param>
    /// <param name="leaf">Имя подкаталога, отличающее установки друг от друга.</param>
    /// <param name="registryVersion">Версия в uninstall-записи или <see langword="null"/>.</param>
    /// <param name="withMetadata">Добавлять ли документ install metadata.</param>
    /// <param name="withControlExecutable">Добавлять ли точку входа control surface.</param>
    /// <returns>Абсолютный путь корня установки.</returns>
    internal static string Add(
        FakeMuMuInstallationRegistrySource registry,
        FakeMuMuInstallMetadataSource metadata,
        FakeMuMuFileSystemProbe fileSystem,
        string leaf,
        string? registryVersion = Version,
        bool withMetadata = true,
        bool withControlExecutable = true)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(fileSystem);

        string installRoot = MuMuWindowsTestPaths.Create(leaf);

        fileSystem.AddDirectory(installRoot);

        if (withControlExecutable)
        {
            fileSystem.AddFile(ControlExecutablePath(installRoot), string.Empty);
        }

        // Запись реестра добавляется всегда: она даёт корень установки, а версия в ней может и
        // отсутствовать — тогда версия берётся из install metadata.
        registry.Add(RegistryCandidate(installRoot, registryVersion));

        if (withMetadata)
        {
            metadata.Add(
                MuMuWindowsTestPaths.Create($"{leaf}-product", "install_config.json"),
                InstallMetadata(installRoot));
        }

        return installRoot;
    }

    /// <summary>Собирает путь точки входа control surface установки.</summary>
    /// <param name="installRoot">Абсолютный путь корня установки.</param>
    /// <returns>Абсолютный путь control utility.</returns>
    internal static string ControlExecutablePath(string installRoot)
        => Path.Combine(installRoot, "nx_main", "MuMuManager.exe");

    /// <summary>Собирает uninstall-запись семейства MuMu.</summary>
    /// <param name="installRoot">Абсолютный путь корня установки.</param>
    /// <param name="version">Версия установки или <see langword="null"/>.</param>
    /// <returns>Запись uninstall-раздела.</returns>
    internal static MuMuRegistryCandidate RegistryCandidate(string installRoot, string? version)
        => new()
        {
            RegistryKeyPath = $"LocalMachine\\Registry64\\Uninstall\\MuMuPlayerGlobal-{installRoot}",
            DisplayName = "MuMuPlayer",
            DisplayVersion = version,
            InstallLocation = installRoot,
            Publisher = "Netease",
        };

    /// <summary>Собирает документ install metadata реальной формы.</summary>
    /// <param name="installRoot">Абсолютный путь корня установки.</param>
    /// <returns>Содержимое документа <c>install_config.json</c>.</returns>
    internal static string InstallMetadata(string installRoot)
        => $$"""
            {
                "config_version": "2",
                "product": {
                    "name": "{{ProductName}}",
                    "install_dir": "{{MuMuWindowsTestPaths.EscapeForJson(installRoot)}}",
                    "version": "{{Version}}",
                    "product": "{{ProductId}}",
                    "usage": "overseas"
                },
                "engines": {
                    "nemux": { "player": { "android_version": "12.0" } },
                    "mumu15": { "player": { "android_version": "15.0" } }
                }
            }
            """;
}
