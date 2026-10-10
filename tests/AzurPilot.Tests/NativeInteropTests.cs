using AzurPilot.Core;
using AzurPilot.Core.Failures;
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
        Assert.Equal(PinnedVersions.OpenCv, info.OpencvVersion);
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

    [Fact(DisplayName = "Native PNG decode возвращает размеры RGB8 без managed pixel copy")]
    public void NativeFrameDecodeReturnsImmutableMetadata()
    {
        byte[] png = ReadFixture("rgb_red_blue.png");

        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);
        NativeFrameInfo info = frame.GetInfo();

        Assert.Equal(2u, info.Width);
        Assert.Equal(1u, info.Height);
        Assert.Equal(6u, info.StrideBytes);
        Assert.Equal(6u, info.ByteLength);
        Assert.Equal(1u, info.PixelFormat);
    }

    [Fact(DisplayName = "Native PNG decode fail-closed отклоняет повреждённые и неподдерживаемые входы")]
    public void NativeFrameDecodeRejectsInvalidAndUnsupportedPng()
    {
        byte[] empty = [];
        AzurPilotNativeBoundaryException emptyInput = Assert.ThrowsAny<AzurPilotNativeBoundaryException>(
            () => AzurPilotNativeBridge.DecodePng(empty));
        ApplicationFailure emptyFailure = NativeBoundaryFailureMapper.Map(emptyInput);
        Assert.Equal(ApplicationFailure.NativeFrameInvalid, emptyFailure.Code);
        Assert.Equal("5", emptyFailure.Details?[NativeBoundaryFailureMapper.NativeStatusCodeKey]);

        byte[] truncated = ReadFixture("rgb_red_blue.png")[..^4];
        AzurPilotNativeBoundaryException invalid = Assert.ThrowsAny<AzurPilotNativeBoundaryException>(
            () => AzurPilotNativeBridge.DecodePng(truncated));
        ApplicationFailure invalidFailure = NativeBoundaryFailureMapper.Map(invalid);
        Assert.Equal(ApplicationFailure.NativeFrameInvalid, invalidFailure.Code);
        Assert.False(invalidFailure.IsRetryable);
        Assert.Equal("5", invalidFailure.Details?[NativeBoundaryFailureMapper.NativeStatusCodeKey]);

        byte[] corruptIdat = ReadFixture("corrupt_idat_checksum.png");
        AzurPilotNativeBoundaryException corruptIdatError = Assert.ThrowsAny<AzurPilotNativeBoundaryException>(
            () => AzurPilotNativeBridge.DecodePng(corruptIdat));
        ApplicationFailure corruptIdatFailure = NativeBoundaryFailureMapper.Map(corruptIdatError);
        Assert.Equal(ApplicationFailure.NativeFrameInvalid, corruptIdatFailure.Code);
        Assert.Equal("5", corruptIdatFailure.Details?[NativeBoundaryFailureMapper.NativeStatusCodeKey]);

        byte[] nonOpaqueRgba = ReadFixture("rgba_nonopaque.png");
        AzurPilotNativeBoundaryException unsupported = Assert.ThrowsAny<AzurPilotNativeBoundaryException>(
            () => AzurPilotNativeBridge.DecodePng(nonOpaqueRgba));
        ApplicationFailure unsupportedFailure = NativeBoundaryFailureMapper.Map(unsupported);
        Assert.Equal(ApplicationFailure.NativeFrameInvalid, unsupportedFailure.Code);
        Assert.Equal("6", unsupportedFailure.Details?[NativeBoundaryFailureMapper.NativeStatusCodeKey]);

        byte[] oversized = ReadFixture("oversized_dimensions.png");
        AzurPilotNativeBoundaryException tooLarge = Assert.ThrowsAny<AzurPilotNativeBoundaryException>(
            () => AzurPilotNativeBridge.DecodePng(oversized));
        ApplicationFailure oversizedFailure = NativeBoundaryFailureMapper.Map(tooLarge);
        Assert.Equal(ApplicationFailure.NativeFrameInvalid, oversizedFailure.Code);
        Assert.Equal("7", oversizedFailure.Details?[NativeBoundaryFailureMapper.NativeStatusCodeKey]);
    }

    [Fact(DisplayName = "SafeHandle переживает GC, конкурентный GetInfo и Dispose и повторное освобождение")]
    public async Task NativeFrameSafeHandleProtectsConcurrentMetadataAndRelease()
    {
        byte[] png = ReadFixture("rgb_red_blue.png");
        NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(2u, frame.GetInfo().Width);

        Task[] readers = [.. Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() =>
            {
                try
                {
                    NativeFrameInfo info = frame.GetInfo();
                    Assert.Equal(6u, info.ByteLength);
                }
                catch (ObjectDisposedException)
                {
                    // Dispose, начавшийся до SafeHandle.AddRef, корректно закрывает доступ.
                }
            }))];
        Task disposer = Task.Run(frame.Dispose, TestContext.Current.CancellationToken);
        await Task.WhenAll(readers.Append(disposer));

        frame.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => frame.GetInfo());
    }

    [Fact(DisplayName = "Повторные managed create/read/dispose циклы не оставляют живых SafeHandle")]
    public void NativeFrameRepeatedCreateAndDisposeIsIdempotent()
    {
        byte[] png = ReadFixture("rgb_red_blue.png");

        for (int iteration = 0; iteration < 512; iteration++)
        {
            NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);
            Assert.Equal(6u, frame.GetInfo().ByteLength);
            frame.Dispose();
            frame.Dispose();
        }
    }

    private static byte[] ReadFixture(string name)
        => File.ReadAllBytes(Path.Combine(
            PinnedVersions.RepositoryRoot,
            "native",
            "tests",
            "fixtures",
            name));
}
