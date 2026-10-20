using System.Collections.Frozen;
using System.Globalization;
using AzurPilot.Core.Android;
using AzurPilot.Core.Capture;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;

namespace AzurPilot.Windows.Capture;

/// <summary>
/// Ожидаемый отказ транспорта снимка внутри одного backend. Наружу не выходит: маршрутизатор проецирует
/// его в <see cref="ApplicationFailure"/> через <see cref="ScreenshotCaptureFailures"/>.
/// </summary>
internal sealed class CaptureBackendException : Exception
{
    internal CaptureBackendException(string reason, bool unsupported = false, string? detail = null,
        Exception? inner = null)
        : base("Транспорт снимка отклонил операцию: " + reason, inner)
    {
        Reason = reason;
        Unsupported = unsupported;
        Detail = detail;
    }

    /// <summary>Машинная причина.</summary>
    internal string Reason { get; }

    /// <summary>Метод неприменим к target по наблюдённому основанию (UNSUPPORTED, а не FAILED).</summary>
    internal bool Unsupported { get; }

    /// <summary>Ограниченная техническая деталь без путей и сырого вывода устройства.</summary>
    internal string? Detail { get; }

    /// <summary>Сессия больше непригодна и должна быть закрыта.</summary>
    internal bool SessionBroken { get; init; } = true;
}

/// <summary>Единственный владелец отказов маршрутизатора транспортов снимков.</summary>
internal static class ScreenshotCaptureFailures
{
    internal static ApplicationFailure From(CaptureBackendException exception, ScreenshotMethod method,
        MuMuInstanceId instanceId, AndroidEndpoint endpoint)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal)
        {
            ["reason"] = exception.Reason,
            ["method"] = ScreenshotMethodNames.ToName(method),
            ["verdict"] = CaptureVerdictNames.ToName(exception.Unsupported ? CaptureVerdict.Unsupported : CaptureVerdict.Failed),
            ["instance_id"] = BoundedDiagnosticText.Bounded(instanceId.ToString()),
            ["endpoint"] = BoundedDiagnosticText.Bounded(endpoint.ToString()),
        };
        if (exception.Detail is string detail)
        {
            details["detail"] = BoundedDiagnosticText.Bounded(detail);
        }

        if (exception.InnerException is NativeCaptureSourceException native)
        {
            details["native_status_code"] = native.StatusCode.ToString(CultureInfo.InvariantCulture);
            if (native.ProviderStatus != 0)
            {
                details["provider_status"] = native.ProviderStatus.ToString(CultureInfo.InvariantCulture);
            }
        }

        return new ApplicationFailure
        {
            Code = exception.Unsupported
                ? ApplicationFailure.ScreenshotMethodUnsupported
                : ApplicationFailure.AndroidScreenshotCaptureFailed,
            Message = exception.Unsupported
                ? "Выбранный транспорт снимка неприменим к этому экземпляру."
                : "Транспорт снимка не получил проверенный RGB8-кадр.",
            IsRetryable = false,
            Details = details.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }
}
