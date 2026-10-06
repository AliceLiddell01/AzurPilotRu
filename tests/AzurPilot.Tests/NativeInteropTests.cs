using AzurPilot.Core;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests;

/// <summary>
/// Интеграционное доказательство interop boundary: тест загружает реально собранную native
/// библиотеку и получает от неё данные через замороженный C ABI.
/// </summary>
/// <remarks>
/// Коллекция помечена как непараллельная: проверки interop делят общее состояние процесса (загрузку
/// native библиотеки), поэтому выполняются последовательно.
/// </remarks>
[Collection(InteropCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NativeInteropTests
{
    [Fact(DisplayName = "Managed объявление структуры совпадает с раскладкой заголовка ABI")]
    public void ManagedStructSizeMatchesAbiHeader()
    {
        // Заголовок ABI замораживает полный размер структуры: смещения 0, 4, 8, 12, 16, 20 и 24..55,
        // выравнивание 4, полный размер 56 байт. Проверяется фактический размер managed структуры
        // interop, а не только объявленная константа: расхождение — ошибка контракта.
        int size = AzurPilotNativeBridge.VerifyStructLayout();

        Assert.Equal(56, size);
        Assert.Equal(56, AzurPilotNativeBridge.NativeInfoSizeInBytes);
    }

    [Fact(DisplayName = "Native библиотека реально загружается и сообщает ожидаемую версию ABI")]
    public void NativeLibraryReportsExpectedAbiVersion()
    {
        uint nativeVersion = AzurPilotNativeBridge.NativeAbiVersion();

        Assert.Equal(PinnedVersions.NativeAbi, nativeVersion);
        Assert.Equal(NativeBoundaryContract.ExpectedAbiVersion, nativeVersion);
    }

    [Fact(DisplayName = "Native библиотека сообщает версию OpenCV из закреплённого пина")]
    public void NativeLibraryReportsPinnedOpenCvVersion()
    {
        NativeBoundaryInfo info = AzurPilotNativeBridge.Query();

        Assert.Equal(NativeBoundaryContract.ExpectedAbiVersion, info.AbiVersion);
        Assert.Equal(5, info.OpencvVersion.Major);
        Assert.Equal(PinnedVersions.OpenCv.Minor, info.OpencvVersion.Minor);
        Assert.Equal(PinnedVersions.OpenCv.Build, info.OpencvVersion.Build);
        Assert.False(string.IsNullOrWhiteSpace(info.BuildInfo));
    }

    [Fact(DisplayName = "Native библиотека подтверждает исполнение OpenCV и обязательные capability")]
    public void NativeLibraryConfirmsOpenCvExecutionAndCapabilities()
    {
        NativeBoundaryInfo info = AzurPilotNativeBridge.Query();

        Assert.True(
            (info.BuildFlags & AzurPilotNativeBridge.BuildFlagOpencvExecuted) != 0,
            $"Native библиотека не подтвердила исполнение кода OpenCV: build_flags={info.BuildFlags}.");

        Assert.Contains("core", info.Capabilities);
        Assert.Contains("imgcodecs", info.Capabilities);

        NativeBoundaryCompatibility compatibility = NativeBoundaryContract.Canonical.Check(info);
        Assert.True(compatibility.IsCompatible, compatibility.Reason);
    }

    [Fact(DisplayName = "Строка сведений о сборке содержит версию ABI и версию OpenCV")]
    public void BuildInfoStringContainsPinnedVersions()
    {
        string buildInfo = AzurPilotNativeBridge.BuildInfo();

        Assert.Contains($"abi_version={PinnedVersions.NativeAbi}", buildInfo, StringComparison.Ordinal);
        Assert.Contains($"opencv_version={PinnedVersions.OpenCv}", buildInfo, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', buildInfo);
        Assert.DoesNotContain('\r', buildInfo);
    }
}
