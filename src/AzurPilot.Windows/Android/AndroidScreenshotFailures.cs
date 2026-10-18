using System.Collections.Frozen;
using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.Processes;

namespace AzurPilot.Windows.Android;

/// <summary>Единственный владелец bounded отказов screenshot capture.</summary>
internal static class AndroidScreenshotFailures
{
    internal static ApplicationFailure CaptureFailed(
        string reason,
        MuMuInstanceId instanceId,
        AndroidEndpoint endpoint,
        WindowsProcessOutcome? outcome = null,
        Exception? exception = null)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            ["reason"] = reason,
        };

        if (!string.IsNullOrWhiteSpace(instanceId.Index))
        {
            details["instance_id"] = BoundedDiagnosticText.Bounded(instanceId.ToString());
        }

        if (!string.IsNullOrWhiteSpace(endpoint.Host)
            && endpoint.Port is >= AndroidEndpoint.MinPort and <= AndroidEndpoint.MaxPort)
        {
            details["endpoint"] = BoundedDiagnosticText.Bounded(endpoint.ToString());
        }

        if (outcome is not null)
        {
            details["exit_code"] = outcome.ExitCode.ToString(CultureInfo.InvariantCulture);
            details["encoded_png_bytes"] = outcome.StandardOutputBytes.Length.ToString(CultureInfo.InvariantCulture);
            details["stdout_bytes_read"] = outcome.StandardOutputBytesRead.ToString(CultureInfo.InvariantCulture);
            if (reason == "command_failed")
            {
                // Тот же fail-closed parser распознаёт только доказанный отказ точного target.
                // Unknown не устанавливает причину сбоя захвата; сырой stderr не публикуется.
                details["transport_state"] = AdbResponseParser.ParseTransport(outcome, endpoint).State
                    .ToString().ToLowerInvariant();
            }
        }

        if (exception is not null)
        {
            details["exception_type"] = exception.GetType().Name;
            if (exception is NativeFrameDecodeException decodeException)
            {
                details["native_status_code"] = decodeException.StatusCode.ToString(CultureInfo.InvariantCulture);
            }
        }

        return new ApplicationFailure
        {
            Code = ApplicationFailure.AndroidScreenshotCaptureFailed,
            Message = "Не удалось получить целый PNG screenshot и подтверждённый native RGB8 frame.",
            IsRetryable = false,
            Details = details.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }
}
