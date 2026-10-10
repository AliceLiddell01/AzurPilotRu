using AzurPilot.Core;
using AzurPilot.Core.Failures;
using AzurPilot.Tests.Failures;
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
    [Fact(DisplayName = "Управляемое объявление структуры совпадает с раскладкой заголовка ABI")]
    public void ManagedStructSizeMatchesAbiHeader()
    {
        // Заголовок ABI замораживает полный размер структуры: смещения 0, 4, 8, 12, 16, 20 и 24..55,
        // выравнивание 4, полный размер 56 байт. Проверяется фактический размер управляемой структуры
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

    [Fact(DisplayName = "Native библиотека подтверждает исполнение OpenCV и обязательные возможности")]
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

    [Fact(DisplayName = "Декодирование PNG возвращает размеры RGB8 без копирования пикселей в C#")]
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

    [Fact(DisplayName = "Декодирование PNG отклоняет повреждённые и неподдерживаемые входы")]
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

        AssertPngFailure(emptyInput, emptyFailure);
        AssertPngFailure(invalid, invalidFailure);
        AssertPngFailure(corruptIdatError, corruptIdatFailure);
        AssertPngFailure(unsupported, unsupportedFailure);
        AssertPngFailure(tooLarge, oversizedFailure);
    }

    [Fact(DisplayName = "GetInfo после Dispose отклоняется до native вызова даже без DLL")]
    public void DisposedNativeFrameRejectsMetadataBeforeNativeCall()
    {
        string output = NativeBoundaryProbeRunner.AssertDisposedFrameRejectsBeforeNativeCall();

        Assert.Contains(
            "Проба: GetInfo после Dispose отклонён до native вызова.",
            output,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Освобождённый кадр предсказуемо отклоняет GetInfo и повторный Dispose безопасен")]
    public void DisposedNativeFrameRejectsMetadataAndRepeatedDisposeIsSafe()
    {
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(ReadFixture("rgb_red_blue.png"));
        frame.Dispose();
        frame.Dispose();

        ObjectDisposedException error = Assert.Throws<ObjectDisposedException>(() => frame.GetInfo());

        Assert.Equal(typeof(NativeFrame).FullName, error.ObjectName);
    }

    [Fact(DisplayName = "SafeHandle переживает GC, конкурентный GetInfo и Dispose и повторное освобождение")]
    public async Task NativeFrameSafeHandleProtectsConcurrentMetadataAndRelease()
    {
        byte[] png = ReadFixture("rgb_red_blue.png");
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(2u, frame.GetInfo().Width);

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] readers = [.. Enumerable.Range(0, 64)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    try
                    {
                        NativeFrameInfo info = frame.GetInfo();
                        Assert.Equal(6u, info.ByteLength);
                    }
                    catch (ObjectDisposedException error)
                    {
                        // Конкурентный Dispose завершился до получения блокировки читателем.
                        Assert.Equal(typeof(NativeFrame).FullName, error.ObjectName);
                    }
                }
            }, TestContext.Current.CancellationToken))];
        Task[] disposers = [.. Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                frame.Dispose();
                frame.Dispose();
            }, TestContext.Current.CancellationToken))];
        start.SetResult();
        await Task.WhenAll(readers.Concat(disposers));

        frame.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => frame.GetInfo());
    }

    [Fact(DisplayName = "Повторные циклы создания, чтения и освобождения не оставляют живых SafeHandle")]
    public void NativeFrameRepeatedCreateAndDisposeIsIdempotent()
    {
        byte[] png = ReadFixture("rgb_red_blue.png");

        for (int iteration = 0; iteration < 512; iteration++)
        {
            NativeFrame frame = AzurPilotNativeBridge.DecodePng(png);
            Assert.Equal(6u, frame.GetInfo().ByteLength);
            frame.Dispose();
            frame.Dispose();
            _ = Assert.Throws<ObjectDisposedException>(() => frame.GetInfo());
        }
    }

    private static void AssertPngFailure(
        AzurPilotNativeBoundaryException error,
        ApplicationFailure failure)
    {
        Assert.Equal("NativeFrameDecodeException", error.GetType().Name);
        Assert.DoesNotContain("завершилась с кодом", error.Message, StringComparison.Ordinal);
        Assert.Equal(ApplicationFailure.NativeFrameInvalid, failure.Code);
        Assert.False(failure.IsRetryable);
        Assert.NotNull(failure.Details);
        Assert.Equal(3, failure.Details.Count);
        Assert.Equal("NativeFrameDecodeException", failure.Details[NativeBoundaryFailureMapper.ExceptionTypeKey]);
        Assert.Equal(AzurPilotNativeBridge.LibraryName, failure.Details[NativeBoundaryFailureMapper.NativeLibraryKey]);
    }

    private static byte[] ReadFixture(string name)
        => File.ReadAllBytes(Path.Combine(
            PinnedVersions.RepositoryRoot,
            "native",
            "tests",
            "fixtures",
            name));
}
