using System.Text;
using AzurPilot.App;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;

// Проба для негативной проверки interop boundary и application startup.
//
// Запускается тестом из каталога, где native библиотеки заведомо нет, и проверяет, что production-код
// interop честно сообщает об этом исключением. Так проверка выполняется в процессе, где библиотека
// действительно недоступна: в процессе теста загруженный модуль остаётся доступным до его завершения,
// поэтому отсутствие файла там доказать нельзя.
//
// Режим --abi-mismatch загружает отдельную тестовую DLL с несовместимой версией ABI и доказывает,
// что Query отвергает её до вызова azurpilot_native_query.
//
// Режим --application-composition <путь> выполняет реальный startup приложения через composition root
// с явно переданным путём конфигурации. Аргументы здесь допустимы, потому что проба — тестовый
// инструмент: продукт аргументов не разбирает, а путь конфигурации передаётся кодом. Так
// application-level доказательства (код выхода, application failure, здоровый snapshot) получаются в
// процессе с полностью управляемым каталогом запуска и без зависимости от пользовательского
// %LOCALAPPDATA%\AzurPilot\config.json.
//
// Кроме самого исключения проба прогоняет его через production-маппер
// NativeBoundaryFailureMapper и печатает полученный стабильный application-level код отказа: тесты
// проверяют код, полученный из реального failure mode, а не из мока.
// Коды выхода: 0 — ожидаемый результат получен; 1 — результат не соответствует ожидаемому.

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
            Console.Error.WriteLine(
                $"Проба: диагностика несовместимого ABI не называет библиотеку: {exception.Message}");
            return 1;
        }

        Console.WriteLine($"Проба: получено ожидаемое исключение несовместимого ABI: {exception.Message}");
        return ReportMappedFailure(exception);
    }

    Console.Error.WriteLine("Проба: несовместимый ABI не привёл к NativeAbiMismatchException.");
    return 1;
}

if (args.Length == 2 && string.Equals(args[0], "--application-composition", StringComparison.Ordinal))
{
    return await RunApplicationCompositionAsync(args[1]).ConfigureAwait(false);
}

if (args.Length != 0)
{
    Console.Error.WriteLine(
        "Проба: неизвестные аргументы; допустимы --abi-mismatch и --application-composition <путь>.");
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
    return ReportMappedFailure(exception);
}

Console.Error.WriteLine(
    $"Проба: отсутствие native библиотеки «{AzurPilotNativeBridge.LibraryName}» не привело к исключению.");
return 1;

// Выполняет startup приложения с явно переданным путём конфигурации и сообщает полученный код выхода
// и стабильный application-level код отказа. Код выхода самой пробы описывает корректность прогона,
// а код выхода приложения передаётся данными: тест сверяет его с ожидаемым application failure.
static async Task<int> RunApplicationCompositionAsync(string configurationPath)
{
    AzurPilotStartupResult result;
    try
    {
        result = await AzurPilotHost.RunAsync(configurationPath).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"Проба: composition завершилась исключением {exception.GetType().Name}: {exception.Message}");
        return 1;
    }

    Console.WriteLine($"Проба: application exit code={result.ExitCode}");
    if (result.FailureInfo is not null)
    {
        Console.WriteLine($"Проба: application failure code={result.FailureInfo.Code}");
    }

    return 0;
}

// Прогоняет реально полученное исключение через production-маппер и сообщает стабильный код отказа.
// Код выхода 1 означает, что проекция не дала ожидаемый ApplicationFailure.
static int ReportMappedFailure(Exception exception)
{
    ApplicationFailure failure = NativeBoundaryFailureMapper.Map(exception);
    Console.WriteLine($"Проба: application failure code={failure.Code}");
    if (failure.Code != ApplicationFailure.NativeUnavailable
        && failure.Code != ApplicationFailure.NativeIncompatible)
    {
        Console.Error.WriteLine(
            $"Проба: маппер вернул код {failure.Code} вместо кода ошибки границы для "
            + $"{exception.GetType().Name}.");
        return 1;
    }

    return 0;
}
