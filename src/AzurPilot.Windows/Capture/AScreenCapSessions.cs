using System.Buffers.Binary;
using AzurPilot.Core.Capture;
using AzurPilot.Windows.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Capture;

/// <summary>
/// <c>aScreenCap</c> и <c>aScreenCap_nc</c>: закреплённый бинарный файл ClnViewer, выбранный по SDK и ABI
/// устройства, выдаёт LZ4-сжатый BMP (заголовок BMZ1). Сборки существуют только для Android 5–9; на более
/// новой системе транспорт пробуется последней сборкой, и отказ запуска классифицируется как UNSUPPORTED.
/// </summary>
internal sealed class AScreenCapSession(ScreenshotSessionContext context, bool netcat)
    : ScreenshotSession(netcat ? ScreenshotMethod.AScreenCapNc : ScreenshotMethod.AScreenCap, context)
{
    private const string RemoteName = "ascreencap";
    private const int MaximumPayload = AzurPilotNativeBridge.MaximumRawInputBytes;
    private static readonly string[] SupportedAbis = ["x86_64", "arm64-v8a", "x86", "armeabi-v7a"];
    private readonly NetcatChannel? _channel = netcat ? new NetcatChannel(context) : null;
    private string _remote = string.Empty;
    private bool _beyondPinnedSdk;

    internal override async Task OpenAsync(CancellationToken cancellationToken)
    {
        CaptureDeviceFacts facts = await Context.FactsAsync(cancellationToken).ConfigureAwait(false);
        if (Array.IndexOf(SupportedAbis, facts.Abi) < 0)
        {
            throw new CaptureBackendException("abi_unsupported", unsupported: true, detail: facts.Abi);
        }

        if (facts.SdkLevel < 21)
        {
            throw new CaptureBackendException("sdk_unsupported", unsupported: true, detail: CaptureAdb.Number(facts.SdkLevel));
        }

        string build = facts.SdkLevel <= 25 ? "5" : facts.SdkLevel <= 27 ? "8" : "9";
        _beyondPinnedSdk = facts.SdkLevel > 28;
        CaptureDependency dependency = CaptureDependencies.Get("ascreencap-" + build + "-" + facts.Abi);
        Facts["dependency"] = dependency.Id + "@" + dependency.Version[..12];
        Facts["license"] = dependency.License;
        Facts["sdk_pinned_range"] = "21-28";
        await Context.Adb.PushVerifiedAsync(dependency, RemoteName, executable: true, cancellationToken).ConfigureAwait(false);
        _remote = CaptureAdb.DeviceDirectory + "/" + RemoteName;
        if (_channel is not null)
        {
            _ = await _channel.OpenAsync(ReverseTrackedAsync, cancellationToken).ConfigureAwait(false);
            Facts["netcat"] = _channel.Command;
        }
    }

    protected override async Task<CapturedAndroidScreenshot> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        CaptureMoment started = Now();
        string producer = _remote + " --pack 2 --stdout";
        byte[] data;
        if (_channel is null)
        {
            ApplicationResult<WindowsProcessOutcome> result = await Context.Adb.RunBinaryAsync(["exec-out", producer],
                MaximumPayload, CaptureAdb.ShortTimeout, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                throw new CaptureBackendException("ascreencap_command_failed", detail: result.FailureInfo!.Code);
            }

            WindowsProcessOutcome outcome = result.Value!;
            if (outcome.ExitCode != 0 || !outcome.StandardOutputBinaryComplete)
            {
                throw Rejected("exit=" + CaptureAdb.Number(outcome.ExitCode));
            }

            data = outcome.StandardOutputBytes;
        }
        else
        {
            data = await _channel.ReceiveAsync(producer, async (stream, token) =>
            {
                byte[] header = new byte[AzurPilotNativeBridge.Bmz1HeaderBytes];
                await NetcatChannel.ReadExactAsync(stream, header, token).ConfigureAwait(false);
                uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != AzurPilotNativeBridge.Bmz1Magic)
                {
                    // Порядок байтов заголовка зависит от сборки; сжатый размер берётся в найденном порядке.
                    compressed = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
                }

                if (compressed == 0 || compressed > MaximumPayload)
                {
                    throw Rejected("bmz1_header_invalid");
                }

                byte[] all = new byte[header.Length + compressed];
                header.CopyTo(all, 0);
                await NetcatChannel.ReadExactAsync(stream, all.AsMemory(header.Length), token).ConfigureAwait(false);
                return all;
            }, CaptureAdb.ShortTimeout, cancellationToken).ConfigureAwait(false);
        }

        if (data.Length < AzurPilotNativeBridge.Bmz1HeaderBytes)
        {
            throw Rejected("bmz1_output_empty");
        }

        long acquired = Clock.GetTimestamp();
        NativeFrame frame = AzurPilotNativeBridge.DecodeBmz1(data);
        return Complete(frame, started, acquired, data.Length);
    }

    // На Android новее закреплённых сборок отказ запуска — наблюдённая неприменимость, а не сбой транспорта.
    private CaptureBackendException Rejected(string detail)
        => new(_beyondPinnedSdk ? "binary_rejected_by_android_version" : "ascreencap_output_invalid",
            unsupported: _beyondPinnedSdk, detail: detail);

    protected override ValueTask CloseTransportAsync()
    {
        _channel?.Dispose();
        return ValueTask.CompletedTask;
    }
}
