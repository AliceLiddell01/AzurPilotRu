using Microsoft.Extensions.Logging;

namespace AzurPilot.AndroidAcceptance;

/// <summary>
/// Единственная фабрика logger-ов инструмента приёмки Android.
/// </summary>
/// <remarks>
/// <para>
/// Инструмент не подключает сторонних logging packages: реализация фабрики узкая и держит ровно один
/// provider — <see cref="StderrLoggerProvider"/>. Этого достаточно, потому что production-сервисы
/// readiness и lifecycle принимают стандартные <c>ILogger&lt;T&gt;</c> и не знают, какой provider за ними
/// стоит.
/// </para>
/// <para>
/// Второй provider не поддерживается осознанно: вторая поверхность structured log в приёмке не нужна, а её
/// появление сделало бы границу <c>stdout</c>/<c>stderr</c> неоднозначной.
/// </para>
/// </remarks>
internal sealed class StderrLoggerFactory : ILoggerFactory
{
    private readonly StderrLoggerProvider _provider = new();

    /// <summary>Создаёт logger для категории.</summary>
    /// <param name="categoryName">Категория logger-а.</param>
    /// <returns>Structured logger, пишущий в <c>stderr</c>.</returns>
    public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);

    /// <summary>Отклоняет подключение второго provider-а.</summary>
    /// <param name="provider">Provider, который не будет подключён.</param>
    /// <exception cref="NotSupportedException">Фабрика поддерживает ровно один provider.</exception>
    public void AddProvider(ILoggerProvider provider)
        => throw new NotSupportedException(
            "Фабрика приёмки поддерживает ровно один provider: structured log идёт в stderr.");

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();
}
