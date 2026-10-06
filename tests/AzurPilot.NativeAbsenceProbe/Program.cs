using AzurPilot.Windows;

// Проба для негативной проверки interop boundary.
//
// Запускается тестом из каталога, где native библиотеки заведомо нет, и проверяет, что production-код
// interop честно сообщает об этом исключением. Так проверка выполняется в процессе, где библиотека
// действительно недоступна: в процессе теста загруженный модуль остаётся доступным до его завершения,
// поэтому отсутствие файла там доказать нельзя.
//
// Коды выхода: 0 — ожидаемое исключение получено; 1 — исключения не было или оно другого типа.

try
{
    _ = AzurPilotNativeBridge.NativeAbiVersion();
}
catch (NativeBoundaryUnavailableException exception)
{
    if (!exception.Message.Contains(AzurPilotNativeBridge.LibraryName, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"Проба: исключение получено, но диагностика не называет библиотеку "
            + $"«{AzurPilotNativeBridge.LibraryName}»: {exception.Message}");
        return 1;
    }

    Console.WriteLine($"Проба: получено ожидаемое исключение: {exception.Message}");
    return 0;
}

Console.Error.WriteLine(
    $"Проба: отсутствие native библиотеки «{AzurPilotNativeBridge.LibraryName}» не привело к исключению.");
return 1;
