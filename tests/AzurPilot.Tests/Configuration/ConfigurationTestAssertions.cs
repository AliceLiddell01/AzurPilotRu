using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>Общие проверки результата загрузки конфигурации для тестов этой capability.</summary>
internal static class ConfigurationTestAssertions
{
    /// <summary>Проверяет, что загрузка вернула отказ с ожидаемым стабильным кодом.</summary>
    /// <param name="result">Результат загрузки конфигурации.</param>
    /// <param name="expectedCode">Ожидаемый код отказа из набора <see cref="ApplicationFailure"/>.</param>
    /// <param name="scenario">Описание сценария для диагностики падения.</param>
    /// <returns>Отказ, чтобы тест мог дополнительно проверить его details.</returns>
    internal static ApplicationFailure AssertFailure(
        ApplicationResult<AzurPilotConfigurationSnapshot> result,
        string expectedCode,
        string scenario)
    {
        Assert.True(result.IsFailure, $"Сценарий «{scenario}»: ожидался отказ загрузки, получено {result}.");

        ApplicationFailure failure = result.FailureInfo!;
        Assert.Equal(expectedCode, failure.Code);
        Assert.False(
            string.IsNullOrWhiteSpace(failure.Message),
            $"Сценарий «{scenario}»: отказ обязан содержать человекочитаемое сообщение.");
        return failure;
    }
}
