using AzurPilot.Core.Failures;
using Xunit;

namespace AzurPilot.Tests.Failures;

/// <summary>
/// Доказательства контракта <see cref="ApplicationResult{T}"/>: результат-отказ читается значением, а
/// исключением сообщается только ошибка программирования вызывающей стороны.
/// </summary>
/// <remarks>
/// Разбор результата (<c>Deconstruct</c>) обязан быть пригоден для ветвления: результат-отказ даёт
/// признак отказа и <see langword="default"/> вместо значения. Строгими остаются свойства
/// <see cref="ApplicationResult{T}.Value"/> и <see cref="ApplicationResult{T}.FailureInfo"/> —
/// обращение к ним в неподходящем состоянии по-прежнему является ошибкой программирования.
/// </remarks>
public sealed class ApplicationResultTests
{
    [Fact(DisplayName = "Недоказанное состояние MuMu имеет стабильный код и входит в закрытый whitelist")]
    public void MuMuStateUnprovenIsAnAcceptedStableFailureCode()
    {
        Assert.Equal("mumu_state_unproven", ApplicationFailure.MuMuStateUnproven);
        ApplicationFailure failure = new()
        {
            Code = ApplicationFailure.MuMuStateUnproven,
            Message = "Состояние MuMu не доказано; запуск не выполнялся.",
        };

        ApplicationResult result = ApplicationResult.Failure(failure);

        Assert.True(result.IsFailure);
        Assert.Same(failure, result.FailureInfo);
    }

    private static readonly ApplicationFailure Failure = new()
    {
        Code = ApplicationFailure.ConfigurationInvalid,
        Message = "Файл конфигурации не соответствует схеме.",
    };

    [Fact(DisplayName = "Успешный результат разбирается в признак успеха и значение")]
    public void SuccessDeconstructsIntoValue()
    {
        (bool isSuccess, string? value) = ApplicationResult<string>.Success("snapshot");

        Assert.True(isSuccess);
        Assert.Equal("snapshot", value);
    }

    [Fact(DisplayName = "Результат-отказ разбирается без исключения, а строгие свойства остаются строгими")]
    public void FailureDeconstructsWithoutThrowing()
    {
        ApplicationResult<string> result = ApplicationResult<string>.Failure(Failure);

        (bool isSuccess, string? value) = result;

        Assert.False(isSuccess);
        Assert.Null(value);

        // Разбор не отменяет строгость доступа к состоянию: значение и описание отказа читаются только
        // в подходящем состоянии.
        _ = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Same(Failure, result.FailureInfo);
    }

    [Fact(DisplayName = "Успешный результат сообщает отсутствие отказа исключением, а не значением")]
    public void SuccessRejectsFailureAccess()
    {
        ApplicationResult<string> result = ApplicationResult<string>.Success("snapshot");

        _ = Assert.Throws<InvalidOperationException>(() => result.FailureInfo);
    }
}
