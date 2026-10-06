using System.ComponentModel;
using AzurPilot.Core.Failures;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Доказательства проекции отказов платформенной границы MuMu: исключения process/registry/filesystem и
/// отказы границы запуска становятся application-level отказом, а не протекают наружу как machine
/// contract.
/// </summary>
[Trait("Category", "MuMuWindows")]
public sealed class MuMuPlatformFailureMapperTests
{
    private static readonly string ExecutablePath =
        MuMuWindowsTestPaths.Create("nx_main", "MuMuManager.exe");

    [Theory(DisplayName = "Исключение платформы проецируется в стабильный код отказа")]
    [InlineData(MuMuFailureReasons.RegistryAccessFailed)]
    [InlineData(MuMuFailureReasons.FileSystemAccessFailed)]
    public void PlatformExceptionMapsToStableCode(string reason)
    {
        ApplicationFailure failure =
            MuMuPlatformFailureMapper.ForPlatformException(new UnauthorizedAccessException("Отказано в доступе."), reason);

        Assert.Equal(ApplicationFailure.InternalError, failure.Code);
        Assert.False(failure.IsRetryable);
        Assert.Equal(reason, failure.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal(
            nameof(UnauthorizedAccessException),
            failure.Details![MuMuFailureDetailKeys.ExceptionType]);
    }

    [Fact(DisplayName = "Код ошибки ОС сохраняется как диагностический факт")]
    public void NativeErrorCodeIsPreserved()
    {
        ApplicationFailure failure = MuMuPlatformFailureMapper.ForPlatformException(
            new Win32Exception(5, "Отказано в доступе."),
            MuMuFailureReasons.RegistryAccessFailed);

        Assert.Equal("5", failure.Details![MuMuFailureDetailKeys.NativeErrorCode]);
    }

    [Fact(DisplayName = "Отмена остаётся отменой, а не ошибкой платформы")]
    public void CancellationStaysCancellation()
    {
        ApplicationFailure failure = MuMuPlatformFailureMapper.ForPlatformException(
            new OperationCanceledException("Операция отменена."),
            MuMuFailureReasons.FileSystemAccessFailed);

        Assert.Equal(ApplicationFailure.OperationCancelled, failure.Code);
        Assert.Null(failure.Details);
    }

    [Fact(DisplayName = "Незапустившаяся control surface проецируется в отказ control surface")]
    public void ProcessStartFailureMapsToControlSurfaceFailure()
    {
        ApplicationFailure failure = MuMuPlatformFailureMapper.ForProcessStartFailure(
            ExecutablePath,
            new Win32Exception(2, "Не удалось найти файл."));

        Assert.Equal(ApplicationFailure.MuMuControlSurfaceUnsupported, failure.Code);
        Assert.False(failure.IsRetryable);
        Assert.Equal(MuMuFailureReasons.ProcessStartFailed, failure.Details![MuMuFailureDetailKeys.Reason]);

        // В details попадает только имя файла: путь установки machine-specific и наружу не выходит.
        Assert.Equal("MuMuManager.exe", failure.Details![MuMuFailureDetailKeys.ExecutableName]);
        Assert.DoesNotContain(ExecutablePath, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Превышение дедлайна проецируется в отказ lifecycle-таймаута с признаком повтора")]
    public void ProcessTimeoutMapsToRetryableLifecycleTimeout()
    {
        ApplicationFailure failure = MuMuPlatformFailureMapper.ForProcessTimeout(
            ExecutablePath,
            TimeSpan.FromSeconds(2),
            11,
            0);

        Assert.Equal(ApplicationFailure.MuMuLifecycleTimeout, failure.Code);
        Assert.True(failure.IsRetryable);
        Assert.Equal(MuMuFailureReasons.ProcessTimeout, failure.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal("2000", failure.Details![MuMuFailureDetailKeys.DurationMilliseconds]);
        Assert.Equal("11", failure.Details![MuMuFailureDetailKeys.StandardOutputCharacters]);
        Assert.Equal("0", failure.Details![MuMuFailureDetailKeys.StandardErrorCharacters]);
    }

    [Fact(DisplayName = "Усечённый вывод проецируется в отказ control surface")]
    public void TruncatedOutputMapsToControlSurfaceFailure()
    {
        ApplicationFailure failure = MuMuPlatformFailureMapper.ForOutputTruncation(ExecutablePath, 131072, 0);

        Assert.Equal(ApplicationFailure.MuMuControlSurfaceUnsupported, failure.Code);
        Assert.Equal(MuMuFailureReasons.OutputTruncated, failure.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal("131072", failure.Details![MuMuFailureDetailKeys.StandardOutputCharacters]);
    }

    [Fact(DisplayName = "Нераспознанная форма ответа проецируется в отказ control surface")]
    public void UnrecognizedResponseMapsToControlSurfaceFailure()
    {
        ApplicationFailure withoutProviderCode =
            MuMuPlatformFailureMapper.ForUnrecognizedResponse("version", -1, null);

        Assert.Equal(ApplicationFailure.MuMuControlSurfaceUnsupported, withoutProviderCode.Code);
        Assert.Equal(MuMuFailureReasons.ResponseUnrecognized, withoutProviderCode.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal("-1", withoutProviderCode.Details![MuMuFailureDetailKeys.ExitCode]);
        Assert.False(withoutProviderCode.Details.ContainsKey(MuMuFailureDetailKeys.ProviderErrorCode));

        ApplicationFailure withProviderCode =
            MuMuPlatformFailureMapper.ForUnrecognizedResponse("info --vmindex <index>", -200, -200);

        Assert.Equal("-200", withProviderCode.Details![MuMuFailureDetailKeys.ProviderErrorCode]);
    }

    [Fact(DisplayName = "Маппер не бросает исключений и не подменяет код отказа общим сообщением")]
    public void MapperDoesNotThrowAndKeepsCodes()
    {
        ApplicationFailure cancellation = MuMuPlatformFailureMapper.ForCancellation();

        Assert.Equal(ApplicationFailure.OperationCancelled, cancellation.Code);
        Assert.Null(cancellation.Details);

        _ = Assert.Throws<ArgumentNullException>(
            () => MuMuPlatformFailureMapper.ForPlatformException(null!, MuMuFailureReasons.RegistryAccessFailed));
        _ = Assert.Throws<ArgumentException>(
            () => MuMuPlatformFailureMapper.ForPlatformException(new IOException(), " "));
        _ = Assert.Throws<ArgumentException>(
            () => MuMuPlatformFailureMapper.ForProcessStartFailure(" ", null));
        _ = Assert.Throws<ArgumentException>(
            () => MuMuPlatformFailureMapper.ForUnrecognizedResponse(" ", 0, null));
    }
}
