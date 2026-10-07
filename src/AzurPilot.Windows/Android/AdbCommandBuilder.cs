using AzurPilot.Core.Android;

namespace AzurPilot.Windows.Android;

/// <summary>
/// Единственный владелец точной формы аргументов ADB, которой адресуется конкретный target.
/// </summary>
/// <remarks>
/// <para>
/// Форма зафиксирована по реальной установке: подключение transport выполняется подкомандой
/// <c>connect &lt;endpoint&gt;</c>, а все команды над конкретным устройством несут точный target
/// <c>-s &lt;endpoint&gt;</c> первыми аргументами. Ни одна команда не адресует «устройство по
/// умолчанию» и не работает со списком устройств.
/// </para>
/// <para>
/// Строка командной строки не собирается: возвращается список аргументов, который граница запуска
/// процесса передаёт процессу по одному элементу. Оболочки (<c>cmd</c>, PowerShell), перенаправлений и
/// составных команд на стороне машины не существует, поэтому значение аргумента не может быть
/// переинтерпретировано.
/// </para>
/// <para>
/// Одна команда — один вызов: builder не склеивает несколько действий в одну команду и не добавляет к
/// запрошенной операции ни повторного подключения, ни завершения сервера ADB.
/// </para>
/// </remarks>
public static class AdbCommandBuilder
{
    /// <summary>Подкоманда подключения к точному endpoint-у.</summary>
    public const string ConnectSubcommand = "connect";

    /// <summary>Аргумент точного target-а для всех команд над конкретным устройством.</summary>
    public const string TargetArgument = "-s";

    /// <summary>Подкоманда наблюдения состояния transport.</summary>
    public const string GetStateSubcommand = "get-state";

    /// <summary>Подкоманда выполнения команды внутри Android-устройства.</summary>
    public const string ShellSubcommand = "shell";

    /// <summary>Подкоманда вывода версии клиента ADB.</summary>
    public const string VersionSubcommand = "version";

    /// <summary>Подкоманда чтения свойства Android.</summary>
    public const string GetPropertySubcommand = "getprop";

    /// <summary>Свойство Android, означающее завершение загрузки.</summary>
    public const string BootCompletedProperty = "sys.boot_completed";

    /// <summary>Свойство Android с версией релиза.</summary>
    public const string AndroidReleaseProperty = "ro.build.version.release";

    /// <summary>Свойство Android с уровнем SDK.</summary>
    public const string SdkLevelProperty = "ro.build.version.sdk";

    /// <summary>Подкоманда package manager.</summary>
    public const string PackageManagerSubcommand = "pm";

    /// <summary>Операция package manager, сообщающая путь установленного пакета.</summary>
    public const string PackagePathOperation = "path";

    /// <summary>Подкоманда платформенного сервиса <c>cmd</c>.</summary>
    public const string CommandSubcommand = "cmd";

    /// <summary>Имя сервиса <c>package</c>.</summary>
    public const string PackageServiceName = "package";

    /// <summary>Операция запроса activity-компонентов, подходящих под intent.</summary>
    public const string QueryActivitiesOperation = "query-activities";

    /// <summary>Аргумент краткой формы ответа о компонентах.</summary>
    public const string BriefArgument = "--brief";

    /// <summary>Аргумент вывода компонентов в форме <c>package/activity</c>.</summary>
    public const string ComponentsArgument = "--components";

    /// <summary>Аргумент фильтра по action.</summary>
    public const string ActionArgument = "-a";

    /// <summary>Аргумент фильтра по category.</summary>
    public const string CategoryArgument = "-c";

    /// <summary>Action главного входа в приложение.</summary>
    public const string MainAction = "android.intent.action.MAIN";

    /// <summary>Category launcher-а.</summary>
    public const string LauncherCategory = "android.intent.category.LAUNCHER";

    /// <summary>Подкоманда перечисления процессов.</summary>
    public const string ProcessListSubcommand = "ps";

    /// <summary>Аргумент перечисления процессов всех пользователей.</summary>
    public const string AllProcessesArgument = "-A";

    /// <summary>Аргумент задания формата вывода процессов.</summary>
    public const string FormatArgument = "-o";

    /// <summary>Формат вывода процессов: идентификатор и имя.</summary>
    public const string ProcessFormat = "PID,NAME";

    /// <summary>Подкоманда дампа состояния сервиса.</summary>
    public const string DumpSubcommand = "dumpsys";

    /// <summary>Имя сервиса <c>window</c>.</summary>
    public const string WindowServiceName = "window";

    /// <summary>Аргумент вывода сведений о дисплеях.</summary>
    public const string DisplaysArgument = "displays";

    /// <summary>Подкоманда activity manager.</summary>
    public const string ActivityManagerSubcommand = "am";

    /// <summary>Операция запуска компонента.</summary>
    public const string StartOperation = "start";

    /// <summary>Операция принудительной остановки пакета.</summary>
    public const string ForceStopOperation = "force-stop";

    /// <summary>Аргумент имени компонента при запуске.</summary>
    public const string ComponentArgument = "-n";

    /// <summary>Строит аргументы вывода версии bundled ADB.</summary>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildVersionArguments() => [VersionSubcommand];

    /// <summary>Строит аргументы подключения к точному endpoint-у.</summary>
    /// <param name="endpoint">Точный endpoint, к которому выполняется подключение.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildConnectArguments(AndroidEndpoint endpoint)
        => [ConnectSubcommand, endpoint.ToString()];

    /// <summary>Строит аргументы наблюдения состояния transport точного endpoint-а.</summary>
    /// <param name="endpoint">Точный endpoint, состояние которого запрашивается.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildGetStateArguments(AndroidEndpoint endpoint)
        => [TargetArgument, endpoint.ToString(), GetStateSubcommand];

    /// <summary>Строит аргументы чтения свойства <c>sys.boot_completed</c>.</summary>
    /// <param name="endpoint">Точный endpoint, свойство которого читается.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildBootCompletedArguments(AndroidEndpoint endpoint)
        => BuildPropertyArguments(endpoint, BootCompletedProperty);

    /// <summary>Строит аргументы чтения свойства <c>ro.build.version.release</c>.</summary>
    /// <param name="endpoint">Точный endpoint, свойство которого читается.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildAndroidReleaseArguments(AndroidEndpoint endpoint)
        => BuildPropertyArguments(endpoint, AndroidReleaseProperty);

    /// <summary>Строит аргументы чтения свойства <c>ro.build.version.sdk</c>.</summary>
    /// <param name="endpoint">Точный endpoint, свойство которого читается.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildSdkLevelArguments(AndroidEndpoint endpoint)
        => BuildPropertyArguments(endpoint, SdkLevelProperty);

    /// <summary>Строит аргументы запроса пути установленного пакета.</summary>
    /// <param name="endpoint">Точный endpoint, у которого запрашивается пакет.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildPackagePathArguments(
        AndroidEndpoint endpoint,
        AndroidPackageId package)
        => [TargetArgument, endpoint.ToString(), ShellSubcommand, PackageManagerSubcommand, PackagePathOperation, package.ToString()];

    /// <summary>Строит аргументы запроса launcher-компонентов пакета.</summary>
    /// <param name="endpoint">Точный endpoint, у которого запрашивается компонент.</param>
    /// <param name="package">Идентификатор запрошенного пакета.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildLauncherQueryArguments(
        AndroidEndpoint endpoint,
        AndroidPackageId package)
        =>
        [
            TargetArgument,
            endpoint.ToString(),
            ShellSubcommand,
            CommandSubcommand,
            PackageServiceName,
            QueryActivitiesOperation,
            BriefArgument,
            ComponentsArgument,
            ActionArgument,
            MainAction,
            CategoryArgument,
            LauncherCategory,
            package.ToString(),
        ];

    /// <summary>Строит аргументы перечисления процессов точного endpoint-а.</summary>
    /// <param name="endpoint">Точный endpoint, процессы которого перечисляются.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildProcessListArguments(AndroidEndpoint endpoint)
        =>
        [
            TargetArgument,
            endpoint.ToString(),
            ShellSubcommand,
            ProcessListSubcommand,
            AllProcessesArgument,
            FormatArgument,
            ProcessFormat,
        ];

    /// <summary>Строит аргументы дампа сведений о дисплеях точного endpoint-а.</summary>
    /// <param name="endpoint">Точный endpoint, сведения о дисплеях которого запрашиваются.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildWindowDumpArguments(AndroidEndpoint endpoint)
        =>
        [
            TargetArgument,
            endpoint.ToString(),
            ShellSubcommand,
            DumpSubcommand,
            WindowServiceName,
            DisplaysArgument,
        ];

    /// <summary>Строит аргументы запуска разрешённого launcher-компонента.</summary>
    /// <param name="endpoint">Точный endpoint, на котором запускается компонент.</param>
    /// <param name="component">Разрешённый launcher-компонент пакета.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildStartGameArguments(
        AndroidEndpoint endpoint,
        AndroidComponent component)
        =>
        [
            TargetArgument,
            endpoint.ToString(),
            ShellSubcommand,
            ActivityManagerSubcommand,
            StartOperation,
            ComponentArgument,
            component.Flattened,
        ];

    /// <summary>Строит аргументы принудительной остановки пакета.</summary>
    /// <param name="endpoint">Точный endpoint, на котором останавливается пакет.</param>
    /// <param name="package">Идентификатор останавливаемого пакета.</param>
    /// <returns>Список аргументов процесса.</returns>
    public static IReadOnlyList<string> BuildForceStopGameArguments(
        AndroidEndpoint endpoint,
        AndroidPackageId package)
        =>
        [
            TargetArgument,
            endpoint.ToString(),
            ShellSubcommand,
            ActivityManagerSubcommand,
            ForceStopOperation,
            package.ToString(),
        ];

    private static IReadOnlyList<string> BuildPropertyArguments(AndroidEndpoint endpoint, string property)
        => [TargetArgument, endpoint.ToString(), ShellSubcommand, GetPropertySubcommand, property];
}
