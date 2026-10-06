using Xunit;

namespace AzurPilot.Tests;

/// <summary>
/// Определение непараллельной коллекции тестов interop.
/// </summary>
/// <remarks>
/// Проверки interop делят общее состояние процесса: положительные проверки загружают native
/// библиотеку, а негативные запускают отдельный процесс-пробу. Последовательный запуск делает
/// результат проверок независимым от порядка и от параллелизма тестовой платформы.
/// </remarks>
[CollectionDefinition(InteropCollection.Name, DisableParallelization = true)]
public sealed class InteropCollectionDefinition
{
}

/// <summary>Имя непараллельной коллекции тестов interop.</summary>
internal static class InteropCollection
{
    /// <summary>Имя коллекции.</summary>
    internal const string Name = "NativeInterop";
}
