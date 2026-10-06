using AzurPilot.Core.Failures;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests.Failures;

/// <summary>
/// Доказательства проекции native boundary failures в application-level отказ: маппинг каждого типа
/// исключения проверяется на реальных типах production-кода, а сценарии отсутствующей native DLL и
/// несовместимого ABI — на исключениях, реально полученных процессом-пробой.
/// </summary>
/// <remarks>
/// Проверки маппинга не подменяют failure mode моком: используются исключения, которые бросает
/// production-код interop, и те же строки диагностики, которые он формирует. Проверки процесс-пробы
/// запускают пробу в отдельном процессе с собственной временной fixture и не разделяют состояние с
/// другими проверками границы.
/// </remarks>
public sealed class NativeBoundaryFailureMapperTests
{
    /// <summary>Маппинг типов исключений границы в стабильные коды отказа.</summary>
    /// <param name="kind">Вид исключения production-кода.</param>
    /// <param name="expectedCode">Ожидаемый стабильный код отказа.</param>
    /// <param name="expectedIsRetryable">Ожидаемый признак осмысленности повтора.</param>
    /// <param name="expectedExceptionTypeName">Ожидаемое CLR-имя типа исключения в details.</param>
    [Theory(DisplayName = "Исключение границы проецируется в стабильный application-level код отказа")]
    [InlineData(ExceptionKind.NativeBoundaryUnavailable, ApplicationFailure.NativeUnavailable, true, nameof(NativeBoundaryUnavailableException))]
    [InlineData(ExceptionKind.NativeAbiMismatch, ApplicationFailure.NativeIncompatible, false, nameof(NativeAbiMismatchException))]
    [InlineData(ExceptionKind.OperationCanceled, ApplicationFailure.OperationCancelled, true, nameof(OperationCanceledException))]
    [InlineData(ExceptionKind.TaskCanceled, ApplicationFailure.OperationCancelled, true, nameof(TaskCanceledException))]
    [InlineData(ExceptionKind.BoundaryStatusInternal, ApplicationFailure.InternalError, false, nameof(AzurPilotNativeBoundaryException))]
    [InlineData(ExceptionKind.BoundaryStructLayout, ApplicationFailure.InternalError, false, nameof(AzurPilotNativeBoundaryException))]
    [InlineData(ExceptionKind.UnexpectedInvalidOperation, ApplicationFailure.InternalError, false, nameof(InvalidOperationException))]
    [InlineData(ExceptionKind.UnexpectedTimeout, ApplicationFailure.InternalError, false, nameof(TimeoutException))]
    public void BoundaryExceptionMapsToStableApplicationCode(
        ExceptionKind kind,
        string expectedCode,
        bool expectedIsRetryable,
        string expectedExceptionTypeName)
    {
        Exception exception = CreateException(kind);

        ApplicationFailure failure = NativeBoundaryFailureMapper.Map(exception);

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(expectedIsRetryable, failure.IsRetryable);

        // Отмена — ожидаемый исход операции: структурированных details у неё нет, поэтому тип
        // исключения в details проверяется только для отказов, которые их несут.
        Assert.Equal(
            expectedCode == ApplicationFailure.OperationCancelled ? null : expectedExceptionTypeName,
            failure.Details?[NativeBoundaryFailureMapper.ExceptionTypeKey]);
    }

    [Fact(DisplayName = "Коды отказа берутся из замороженного контракта ApplicationFailure")]
    public void MappedCodesComeFromFrozenContract()
    {
        // Маппер не заводит второй каталог кодов: каждое ожидание в наборе — константа контракта.
        Assert.Equal(ApplicationFailure.NativeUnavailable, NativeBoundaryFailureMapper
            .Map(new NativeBoundaryUnavailableException("Native библиотека «AzurPilot.Native» не загружена.")).Code);
        Assert.Equal(ApplicationFailure.NativeIncompatible, NativeBoundaryFailureMapper
            .Map(new NativeAbiMismatchException("Версия ABI native библиотеки «AzurPilot.Native»: 2; ожидается: 1.")).Code);
        Assert.Equal(ApplicationFailure.OperationCancelled, NativeBoundaryFailureMapper
            .Map(new OperationCanceledException("Операция отменена вызывающей стороной.")).Code);
        Assert.Equal(ApplicationFailure.InternalError, NativeBoundaryFailureMapper
            .Map(new AzurPilotNativeBoundaryException("Native функция azurpilot_native_query завершилась с кодом 4: внутренняя ошибка native стороны.")).Code);
        Assert.Equal(ApplicationFailure.InternalError, NativeBoundaryFailureMapper
            .Map(new InvalidOperationException("Непредвиденное состояние платформенной границы.")).Code);
    }

    [Fact(DisplayName = "Код возврата native стороны попадает в details как отдельное значение")]
    public void NativeStatusCodeIsPreservedInDetails()
    {
        // Строка диагностики — та же форма, которую формирует AzurPilotNativeBridge.
        string message = $"Native функция azurpilot_native_query завершилась с кодом {AzurPilotNativeBridge.StatusOpencvFailure}: "
            + $"{AzurPilotNativeBridge.DescribeStatus(AzurPilotNativeBridge.StatusOpencvFailure)}. "
            + "Сведения о native boundary использовать нельзя.";

        ApplicationFailure failure = NativeBoundaryFailureMapper.Map(
            new AzurPilotNativeBoundaryException(message));

        Assert.Equal(ApplicationFailure.InternalError, failure.Code);
        Assert.Equal(
            AzurPilotNativeBridge.StatusOpencvFailure.ToString(System.Globalization.CultureInfo.InvariantCulture),
            failure.Details?[NativeBoundaryFailureMapper.NativeStatusCodeKey]);
    }

    [Fact(DisplayName = "Неожиданное исключение не раскрывает текст исходного исключения")]
    public void UnexpectedExceptionMessageIsBounded()
    {
        const string originalMessage = "Секрет: connection string = Server=internal-host;Password=секрет.";

        ApplicationFailure failure = NativeBoundaryFailureMapper.Map(new InvalidOperationException(originalMessage));

        Assert.Equal(ApplicationFailure.InternalError, failure.Code);
        Assert.DoesNotContain(originalMessage, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", failure.Message, StringComparison.Ordinal);
        Assert.Equal(nameof(InvalidOperationException), failure.Details?[NativeBoundaryFailureMapper.ExceptionTypeKey]);
    }

    [Fact(DisplayName = "Details ошибки границы ограничены структурированными парами")]
    public void BoundaryFailureDetailsAreBounded()
    {
        ApplicationFailure failure = NativeBoundaryFailureMapper.Map(
            new NativeBoundaryUnavailableException("Native библиотека «AzurPilot.Native» не загружена."));

        Assert.NotNull(failure.Details);
        Assert.InRange(failure.Details.Count, 1, 3);
        Assert.Equal(
            AzurPilotNativeBridge.LibraryName,
            failure.Details[NativeBoundaryFailureMapper.NativeLibraryKey]);
    }

    [Fact(DisplayName = "Отмена не помечается как ошибка и не содержит details")]
    public void CancellationCarriesNoDetails()
    {
        using CancellationTokenSource source = new();
        source.Cancel();

        ApplicationFailure failure = NativeBoundaryFailureMapper.Map(
            new OperationCanceledException("Операция отменена.", source.Token));

        Assert.Equal(ApplicationFailure.OperationCancelled, failure.Code);
        Assert.True(failure.IsRetryable);
        Assert.Null(failure.Details);
    }

    [Fact(DisplayName = "Маппинг детерминирован для одного и того же исключения")]
    public void MappingIsDeterministic()
    {
        Exception exception = new AzurPilotNativeBoundaryException(
            "Native функция azurpilot_native_build_info завершилась с кодом 2: предоставленный буфер меньше требуемого размера.");

        Assert.Equal(
            NativeBoundaryFailureMapper.Map(exception),
            NativeBoundaryFailureMapper.Map(exception));
    }

    [Fact(DisplayName = "Маппер отвергает null вместо исключения внутри проекции")]
    public void NullExceptionIsRejected()
    {
        _ = Assert.Throws<ArgumentNullException>(() => NativeBoundaryFailureMapper.Map(null!));
    }

    [Fact(DisplayName = "Процесс-проба без native DLL сообщает код native_unavailable")]
    public void MissingNativeLibraryProbeReportsNativeUnavailable()
    {
        NativeBoundaryProbeRunner.ProbeRun run = NativeBoundaryProbeRunner.Run(NativeBoundaryProbeRunner.ProbeMode.MissingLibrary);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(ApplicationFailure.NativeUnavailable, run.FailureCode);
    }

    [Fact(DisplayName = "Процесс-проба с несовместимым ABI сообщает код native_incompatible")]
    public void IncompatibleAbiProbeReportsNativeIncompatible()
    {
        NativeBoundaryProbeRunner.ProbeRun run = NativeBoundaryProbeRunner.Run(NativeBoundaryProbeRunner.ProbeMode.AbiMismatch);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(ApplicationFailure.NativeIncompatible, run.FailureCode);
    }

    /// <summary>Создаёт исключение production-кода, соответствующее описанию случая.</summary>
    /// <param name="kind">Вид исключения границы.</param>
    /// <returns>Исключение с той же диагностикой, которую формирует production-код interop.</returns>
    private static Exception CreateException(ExceptionKind kind) => kind switch
    {
        ExceptionKind.NativeBoundaryUnavailable => new NativeBoundaryUnavailableException(
            $"Native библиотека «{AzurPilotNativeBridge.LibraryName}» не загружена: Не найден указанный модуль. "
            + "Соберите native часть через CMake preset и повторите managed сборку."),
        ExceptionKind.NativeAbiMismatch => new NativeAbiMismatchException(
            $"Версия ABI native библиотеки «{AzurPilotNativeBridge.LibraryName}»: 2; ожидается: 1. "
            + "Native библиотека несовместима с managed фундаментом."),
        ExceptionKind.OperationCanceled => new OperationCanceledException("Операция отменена запросом отмены."),
        ExceptionKind.TaskCanceled => new TaskCanceledException("Задача отменена запросом отмены."),
        ExceptionKind.BoundaryStatusInternal => new AzurPilotNativeBoundaryException(
            $"Native функция azurpilot_native_query завершилась с кодом {AzurPilotNativeBridge.StatusInternal}: "
            + $"{AzurPilotNativeBridge.DescribeStatus(AzurPilotNativeBridge.StatusInternal)}. "
            + "Сведения о native boundary использовать нельзя."),
        ExceptionKind.BoundaryStructLayout => new AzurPilotNativeBoundaryException(
            "Раскладка managed структуры сведений о native boundary изменилась."),
        ExceptionKind.UnexpectedInvalidOperation => new InvalidOperationException(
            "Непредвиденное состояние платформенной границы."),
        ExceptionKind.UnexpectedTimeout => new TimeoutException(
            "Превышено время ожидания платформенной операции."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид исключения границы."),
    };

    /// <summary>Вид исключения production-кода, участвующий в проверке маппинга.</summary>
    public enum ExceptionKind
    {
        /// <summary>Native библиотека недоступна.</summary>
        NativeBoundaryUnavailable,

        /// <summary>Версия ABI native библиотеки несовместима.</summary>
        NativeAbiMismatch,

        /// <summary>Операция отменена через <see cref="OperationCanceledException"/>.</summary>
        OperationCanceled,

        /// <summary>Задача отменена через <see cref="TaskCanceledException"/>.</summary>
        TaskCanceled,

        /// <summary>Native сторона вернула код ошибки.</summary>
        BoundaryStatusInternal,

        /// <summary>Раскладка managed структуры не совпала с заголовком ABI.</summary>
        BoundaryStructLayout,

        /// <summary>Неожиданное исключение платформенной границы.</summary>
        UnexpectedInvalidOperation,

        /// <summary>Неожиданное исключение с сообщением, которое нельзя раскрывать.</summary>
        UnexpectedTimeout,
    }
}
