using System.Globalization;
using System.Text.RegularExpressions;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Capture;

/// <summary>Сведения устройства, нужные транспортам снимков.</summary>
/// <param name="SdkLevel">ro.build.version.sdk.</param>
/// <param name="Abi">ro.product.cpu.abi.</param>
/// <param name="AndroidRelease">ro.build.version.release.</param>
internal sealed record CaptureDeviceFacts(int SdkLevel, string Abi, string AndroidRelease);

/// <summary>
/// Адресные команды bundled ADB для транспортов снимков: каждая команда несёт <c>-s endpoint</c>, глобальные
/// операции сервера ADB (kill-server, reverse/forward без target) не выполняются. Shell-команды
/// собираются только из констант и токенов, прошедших <see cref="Token"/>, поэтому значение не может стать
/// второй командой, перенаправлением или подстановкой.
/// </summary>
internal sealed partial class CaptureAdb(IWindowsProcessRunner runner, string adbExecutablePath, AndroidEndpoint endpoint)
{
    /// <summary>Каталог helper-файлов AzurPilot на устройстве; удаление вне него не выполняется.</summary>
    internal const string DeviceDirectory = "/data/local/tmp/azurpilot-capture";

    internal static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);

    internal AndroidEndpoint Endpoint => endpoint;

    /// <summary>Проверяет, что значение — безопасный shell-токен (без пробелов и метасимволов).</summary>
    /// <exception cref="ArgumentException">Значение содержит недопустимые символы.</exception>
    internal static string Token(string value)
    {
        return SafeToken().IsMatch(value)
            ? value
            : throw new ArgumentException("Значение не является безопасным shell-токеном.", nameof(value));
    }

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal Task<ApplicationResult<WindowsProcessOutcome>> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return new AdbClient(runner, adbExecutablePath, timeout).RunAsync(
            ["-s", endpoint.ToString(), .. arguments], cancellationToken);
    }

    internal Task<ApplicationResult<WindowsProcessOutcome>> RunBinaryAsync(IReadOnlyList<string> arguments,
        int maximumBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return new AdbClient(runner, adbExecutablePath, timeout).RunBinaryAsync(
            ["-s", endpoint.ToString(), .. arguments], maximumBytes, cancellationToken);
    }

    /// <summary>Выполняет shell-команду и требует код выхода 0 и полный текстовый вывод.</summary>
    /// <exception cref="CaptureBackendException">Команда не выполнена или завершилась ошибкой.</exception>
    internal async Task<string> ShellAsync(string command, string failureReason, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ApplicationResult<WindowsProcessOutcome> result = await RunAsync(["shell", command], timeout ?? ShortTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new CaptureBackendException(failureReason, detail: result.FailureInfo!.Code);
        }

        WindowsProcessOutcome outcome = result.Value!;
        if (outcome.ExitCode != 0 || outcome.StandardOutputTruncated)
        {
            throw new CaptureBackendException(failureReason,
                detail: "exit=" + Number(outcome.ExitCode));
        }

        return outcome.StandardOutput;
    }

    /// <summary>Читает SDK, ABI и версию Android одной командой.</summary>
    internal async Task<CaptureDeviceFacts> QueryFactsAsync(CancellationToken cancellationToken)
    {
        string output = await ShellAsync(
            "getprop ro.build.version.sdk; getprop ro.product.cpu.abi; getprop ro.build.version.release",
            "device_facts_unavailable", cancellationToken).ConfigureAwait(false);
        string[] lines = output.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length < 3 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int sdk)
            || !SafeToken().IsMatch(lines[1]) || !SafeToken().IsMatch(lines[2]))
        {
            throw new CaptureBackendException("device_facts_unrecognized");
        }

        return new CaptureDeviceFacts(sdk, lines[1], lines[2]);
    }

    /// <summary>Отправляет файл в каталог helper-ов и сверяет SHA-256 на устройстве.</summary>
    internal async Task PushVerifiedAsync(CaptureDependency dependency, string remoteName, bool executable,
        CancellationToken cancellationToken)
    {
        string remote = DeviceDirectory + "/" + Token(remoteName);
        string existing = await ShellAsync(
            "mkdir -p " + DeviceDirectory + " && (sha256sum " + remote + " 2>/dev/null || true)",
            "helper_directory_unavailable", cancellationToken).ConfigureAwait(false);
        if (!existing.StartsWith(dependency.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            ApplicationResult<WindowsProcessOutcome> pushed = await RunAsync(["push", dependency.LocalPath, remote],
                TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            if (pushed.IsFailure || pushed.Value!.ExitCode != 0)
            {
                throw new CaptureBackendException("helper_push_failed", detail: dependency.Id);
            }

            string verified = await ShellAsync("sha256sum " + remote, "helper_verify_failed", cancellationToken)
                .ConfigureAwait(false);
            if (!verified.StartsWith(dependency.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CaptureBackendException("helper_digest_mismatch", detail: dependency.Id);
            }
        }

        if (executable)
        {
            _ = await ShellAsync("chmod 755 " + remote, "helper_chmod_failed", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Запускает helper в фоне и возвращает его точный PID. Вывод helper-а не читается; процесс переживает
    /// завершение команды ADB и останавливается только <see cref="StopHelperAsync"/> по этому PID.
    /// </summary>
    internal async Task<int> StartHelperAsync(string command, CancellationToken cancellationToken)
    {
        string output = await ShellAsync("nohup " + command + " >/dev/null 2>&1 </dev/null & echo $!",
            "helper_start_failed", cancellationToken).ConfigureAwait(false);
        return int.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 1
            ? pid
            : throw new CaptureBackendException("helper_pid_unavailable");
    }

    /// <summary>
    /// Останавливает helper только если PID всё ещё принадлежит процессу с ожидаемым маркером в cmdline:
    /// переиспользованный PID чужого процесса не затрагивается. Ошибка остановки не бросается.
    /// </summary>
    internal async Task<bool> StopHelperAsync(int pid, string marker, CancellationToken cancellationToken)
    {
        string id = Number(pid);
        string check = "c=$(tr '\\0' ' ' < /proc/" + id + "/cmdline 2>/dev/null); case \"$c\" in *" + Token(marker)
            + "*) ";
        try
        {
            _ = await RunAsync(["shell", check + "kill " + id + ";; esac; true"], ShortTimeout, cancellationToken)
                .ConfigureAwait(false);
            ApplicationResult<WindowsProcessOutcome> after = await RunAsync(
                ["shell", "sleep 0.2; " + check + "kill -9 " + id + "; echo alive;; esac; true"], ShortTimeout,
                cancellationToken).ConfigureAwait(false);
            return after.IsSuccess && !after.Value!.StandardOutput.Contains("alive", StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Создаёт target-scoped forward на свободный порт хоста и возвращает этот порт.</summary>
    internal async Task<int> ForwardAsync(string remote, CancellationToken cancellationToken)
    {
        ApplicationResult<WindowsProcessOutcome> result = await RunAsync(["forward", "tcp:0", Token(remote)],
            ShortTimeout, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value!.ExitCode == 0
            && int.TryParse(result.Value.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535
                ? port
                : throw new CaptureBackendException("forward_failed");
    }

    internal async Task RemoveForwardAsync(int port, CancellationToken cancellationToken)
    {
        _ = await RunAsync(["forward", "--remove", "tcp:" + Number(port)], ShortTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Создаёт target-scoped reverse со свободного порта устройства на loopback-порт хоста.</summary>
    internal async Task<int> ReverseAsync(int hostPort, CancellationToken cancellationToken)
    {
        ApplicationResult<WindowsProcessOutcome> result = await RunAsync(
            ["reverse", "tcp:0", "tcp:" + Number(hostPort)], ShortTimeout, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value!.ExitCode == 0
            && int.TryParse(result.Value.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535
                ? port
                : throw new CaptureBackendException("reverse_failed", unsupported: result.IsSuccess);
    }

    internal async Task RemoveReverseAsync(int devicePort, CancellationToken cancellationToken)
    {
        _ = await RunAsync(["reverse", "--remove", "tcp:" + Number(devicePort)], ShortTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    [GeneratedRegex("^[A-Za-z0-9._/:=,@%+-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeToken();
}
