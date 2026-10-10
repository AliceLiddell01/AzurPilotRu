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

    [Fact(DisplayName = "Несовместимый ABI отвергается до query и frame API")]
    public void IncompatibleNativeAbiThrowsBeforeQueryAndFrameApi()
    {
        NativeBoundaryProbeRunner.ProbeRun result = NativeBoundaryProbeRunner.Run(
            NativeBoundaryProbeRunner.ProbeMode.AbiMismatch);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Проба: получено ожидаемое исключение несовместимого ABI", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Проба: несовместимый ABI отклонён до frame API", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(AzurPilotNativeBridge.LibraryName, result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeIncompatible, result.FailureCode);
    }
}
