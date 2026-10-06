using System.Text;
using AzurPilot.Windows;

// Проба для негативной проверки interop boundary.
//
// Запускается тестом из каталога, где native библиотеки заведомо нет, и проверяет, что production-код
// interop честно сообщает об этом исключением. Так проверка выполняется в процессе, где библиотека
// действительно недоступна: в процессе теста загруженный модуль остаётся доступным до его завершения,
// поэтому отсутствие файла там доказать нельзя.
//
// Режим --abi-mismatch загружает отдельную тестовую DLL с несовместимой версией ABI и доказывает,
// что Query отвергает её до вызова azurpilot_native_query.
// Коды выхода: 0 — ожидаемое исключение получено; 1 — исключения не было или диагностика неверна.

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Length == 1 && string.Equals(args[0], "--abi-mismatch", StringComparison.Ordinal))
{
    try
    {
        _ = AzurPilotNativeBridge.Query();
    }
    catch (NativeAbiMismatchException exception)
    {
        if (!exception.Message.Contains(AzurPilotNativeBridge.LibraryName, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Проба: диагностика несовместимого ABI не называет библиотеку: {exception.Message}");
            return 1;
        }

        Console.WriteLine($"Проба: получено ожидаемое исключение несовместимого ABI: {exception.Message}");
        return 0;
    }

    Console.Error.WriteLine("Проба: несовместимый ABI не привёл к NativeAbiMismatchException.");
    return 1;
}

if (args.Length != 0)
{
    Console.Error.WriteLine("Проба: неизвестные аргументы; допустим только --abi-mismatch.");
    return 1;
}

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
