using AzurPilot.Core.Failures;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства wire-формы MuMu-кодов отказа и закрытости набора.
/// </summary>
/// <remarks>
/// Значения кодов — часть стабильного контракта отказа: их читает платформенный adapter и сопоставляет
/// владелец кодов выхода процесса. Тест фиксирует эти значения явно, чтобы случайное переименование не
/// прошло незамеченным, и проверяет, что набор расширен ровно семью MuMu-кодами без дублей.
/// </remarks>
public sealed class MuMuFailureCodeTests
{
    private static readonly string[] MuMuCodes =
    [
        ApplicationFailure.MuMuInstallationNotFound,
        ApplicationFailure.MuMuInstallationAmbiguous,
        ApplicationFailure.MuMuInstanceNotFound,
        ApplicationFailure.MuMuInstanceAmbiguous,
        ApplicationFailure.MuMuControlSurfaceUnsupported,
        ApplicationFailure.MuMuLifecyclePostconditionNotMet,
        ApplicationFailure.MuMuLifecycleTimeout,
    ];

    private static readonly string[] ExistingCodes =
    [
        ApplicationFailure.ConfigurationInvalid,
        ApplicationFailure.ConfigurationSchemaUnsupported,
        ApplicationFailure.NativeUnavailable,
        ApplicationFailure.NativeIncompatible,
        ApplicationFailure.OperationCancelled,
        ApplicationFailure.InternalError,
    ];

    [Fact(DisplayName = "Семь MuMu-кодов имеют зафиксированную wire-форму")]
    public void MuMuCodesHaveFrozenWireForm()
    {
        Assert.Equal("mumu_installation_not_found", ApplicationFailure.MuMuInstallationNotFound);
        Assert.Equal("mumu_installation_ambiguous", ApplicationFailure.MuMuInstallationAmbiguous);
        Assert.Equal("mumu_instance_not_found", ApplicationFailure.MuMuInstanceNotFound);
        Assert.Equal("mumu_instance_ambiguous", ApplicationFailure.MuMuInstanceAmbiguous);
        Assert.Equal("mumu_control_surface_unsupported", ApplicationFailure.MuMuControlSurfaceUnsupported);
        Assert.Equal("mumu_lifecycle_postcondition_not_met", ApplicationFailure.MuMuLifecyclePostconditionNotMet);
        Assert.Equal("mumu_lifecycle_timeout", ApplicationFailure.MuMuLifecycleTimeout);
    }

    [Fact(DisplayName = "Все семь MuMu-кодов входят в закрытый набор ApplicationFailure")]
    public void MuMuCodesAreAcceptedByClosedSet()
    {
        Assert.All(MuMuCodes, code =>
        {
            ApplicationFailure failure = CreateFailure(code);
            Assert.Equal(code, failure.Code);
        });
    }

    [Fact(DisplayName = "Набор расширен ровно семью MuMu-кодами без дублей")]
    public void MuMuCodesAreExactlySevenWithoutDuplicates()
    {
        Assert.Equal(7, MuMuCodes.Length);
        Assert.Equal(MuMuCodes.Length, MuMuCodes.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(MuMuCodes.Intersect(ExistingCodes, StringComparer.Ordinal));
        Assert.Equal(13, MuMuCodes.Concat(ExistingCodes).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(DisplayName = "Код вне закрытого набора отвергается как ошибка программирования")]
    public void UnknownCodeIsRejected()
    {
        _ = Assert.Throws<ArgumentException>(() => CreateFailure("mumu_lifecycle_unknown"));
        _ = Assert.Throws<ArgumentException>(() => CreateFailure("mumu_instance_not_found "));
        _ = Assert.Throws<ArgumentException>(() => CreateFailure("MUMU_INSTANCE_NOT_FOUND"));
        _ = Assert.Throws<ArgumentNullException>(() => CreateFailure(null!));
    }

    [Fact(DisplayName = "MuMu-отказ несёт bounded details и человекочитаемое сообщение")]
    public void MuMuFailureCarriesBoundedDetails()
    {
        ApplicationFailure failure = new()
        {
            Code = ApplicationFailure.MuMuLifecycleTimeout,
            Message = "MuMu instance mumu:1 не достиг состояния running в пределах deadline.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["operation"] = "start",
                ["instance_id"] = "mumu:1",
                ["state"] = "stopped",
                ["elapsed_ms"] = "120000",
            },
        };

        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, failure.Code);
        Assert.Equal("mumu:1", failure.Details!["instance_id"]);
        Assert.Equal(4, failure.Details!.Count);

        // Machine-specific данные в details не попадают: значений-путей там нет.
        Assert.DoesNotContain(failure.Details!.Values, value => value.Contains('\\'));
        Assert.DoesNotContain(failure.Details!.Values, value => value.Contains('/'));
    }

    private static ApplicationFailure CreateFailure(string code)
        => new()
        {
            Code = code,
            Message = "Проверка закрытого набора application-level кодов.",
        };
}
