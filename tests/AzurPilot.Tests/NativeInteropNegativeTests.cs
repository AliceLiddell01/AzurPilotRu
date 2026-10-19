using AzurPilot.Core.Failures;
using AzurPilot.Tests.Failures;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests;

/// <summary>
/// Негативные проверки interop boundary: доказательство, что положительные проверки не проходят на
/// моке и что production-код interop честно сообщает об отсутствии DLL и несовместимом ABI.
/// </summary>
/// <remarks>
/// <para>
/// Проверки выполняют production-код interop в отдельных процессах. Каждый получает собственный
/// каталог с заданной негативной fixture. В процессе положительного теста загруженный модуль
/// остаётся доступным до завершения процесса, поэтому негативную загрузку там воспроизвести нельзя.
/// </para>
/// <para>
/// Тест, который в такой ситуации проходит или пропускается, доказательством границы не является.
/// </para>
/// <para>
/// Помимо самого исключения проверки утверждают стабильный application-level код отказа, который
/// проба получила из реально выброшенного исключения через production-маппер
/// <see cref="NativeBoundaryFailureMapper"/>.
/// </para>
/// </remarks>
[Collection(InteropCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NativeInteropNegativeTests
{
    [Fact(DisplayName = "Текущий ABI без match отвергается до декодирования")]
    public void MissingMatchExportIsRejectedBeforeDecode()
    {
        NativeBoundaryProbeRunner.ProbeRun result = NativeBoundaryProbeRunner.Run(NativeBoundaryProbeRunner.ProbeMode.AbiMissingMatch);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("match export отклонён до декодирования", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeIncompatible, result.FailureCode);
    }

    [Fact(DisplayName = "Текущий ABI без preview отвергает превью до вызова отсутствующего экспорта")]
    public void MissingPreviewExportIsRejectedBeforeCall()
    {
        NativeBoundaryProbeRunner.ProbeRun result = NativeBoundaryProbeRunner.Run(NativeBoundaryProbeRunner.ProbeMode.AbiMissingPreview);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("preview export отклонён до вызова", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeIncompatible, result.FailureCode);
    }

    private const string ProbeExpectedMarker = "Проба: получено ожидаемое исключение";

    [Fact(DisplayName = "Отсутствующая native библиотека обязана выбросить исключение, а не пропустить проверку")]
    public void MissingNativeLibraryThrowsInsteadOfSkipping()
    {
        NativeBoundaryProbeRunner.ProbeRun result = NativeBoundaryProbeRunner.Run(
            NativeBoundaryProbeRunner.ProbeMode.MissingLibrary);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(ProbeExpectedMarker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(AzurPilotNativeBridge.LibraryName, result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeUnavailable, result.FailureCode);
    }

    [Fact(DisplayName = "Native отказ игрового каталога не кешируется type initializer-ом")]
    public void GameCatalogNativeFailureCanBeRetried()
    {
        NativeBoundaryProbeRunner.ProbeRun result = NativeBoundaryProbeRunner.Run(
            NativeBoundaryProbeRunner.ProbeMode.RetryableGameCatalog);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("отказ загрузки каталога 1 получен напрямую и не кеширован", result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("отказ загрузки каталога 2 получен напрямую и не кеширован", result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeUnavailable, result.FailureCode);
    }

    [Fact(DisplayName = "Повреждённая native библиотека обязана выбросить исключение с диагностикой")]
    public void BrokenNativeLibraryThrowsWithDiagnostics()
    {
        NativeBoundaryProbeRunner.ProbeRun result = NativeBoundaryProbeRunner.Run(
            NativeBoundaryProbeRunner.ProbeMode.BrokenLibrary);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(ProbeExpectedMarker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(AzurPilotNativeBridge.LibraryName, result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeUnavailable, result.FailureCode);
    }

    [Fact(DisplayName = "Несовместимый ABI отвергается до query и вызова функции декодирования")]
    public void IncompatibleNativeAbiThrowsBeforeQueryAndFrameApi()
    {
        NativeBoundaryProbeRunner.ProbeRun result = NativeBoundaryProbeRunner.Run(
            NativeBoundaryProbeRunner.ProbeMode.AbiMismatch);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Проба: получено ожидаемое исключение несовместимого ABI", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Проба: несовместимый ABI отклонён до вызова функции декодирования", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(AzurPilotNativeBridge.LibraryName, result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeIncompatible, result.FailureCode);
    }

    [Fact(DisplayName = "Текущий ABI без функции освобождения кадра отвергается до декодирования")]
    public void AbiV3WithoutFrameReleaseIsRejectedBeforeDecode()
    {
        NativeBoundaryProbeRunner.ProbeRun result = NativeBoundaryProbeRunner.Run(
            NativeBoundaryProbeRunner.ProbeMode.AbiV3MissingRelease);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Проба: текущий ABI без обязательной функции освобождения отклонён до декодирования", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("azurpilot_native_frame_release", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeIncompatible, result.FailureCode);
    }

    [Fact(DisplayName = "Текущий ABI без ROI экспорта отвергается до декодирования кадра")]
    public void AbiV3WithoutRoiIsRejectedBeforeDecode()
    {
        NativeBoundaryProbeRunner.ProbeRun result = NativeBoundaryProbeRunner.Run(
            NativeBoundaryProbeRunner.ProbeMode.AbiV3MissingRoi);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Проба: текущий ABI без обязательного ROI экспорта отклонён до декодирования", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("azurpilot_native_frame_scan_region", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeIncompatible, result.FailureCode);
    }
}
