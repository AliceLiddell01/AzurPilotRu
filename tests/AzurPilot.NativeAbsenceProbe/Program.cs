using System.Reflection;
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
// Режим --abi-mismatch загружает DLL с несовместимой версией ABI. Её экспорт функции декодирования
// возвращает INVALID_PNG, поэтому ожидаемый отказ по версии доказывает проверку до вызова этой функции.
// Режим --abi-v3-missing-release загружает DLL ABI v3 без обязательной функции освобождения и
// проверяет, что несовместимость обнаруживается до вызова декодирования.
// Режим --abi-v3-missing-roi проверяет обязательный ROI экспорт до создания native frame.
// Режим --disposed-frame проверяет отказ GetInfo/ScanRegion после Dispose в процессе без native DLL.
// Режим --process-output выдаёт детерминированный бинарный stdout и конкурентный stderr для runner tests.
//
// Режим --application-composition <путь> выполняет реальный startup приложения через composition root
// с явно переданным путём конфигурации. Аргументы здесь допустимы, потому что проба — тестовый
// инструмент: продукт аргументов не разбирает, а путь конфигурации передаётся кодом. Так
// application-level доказательства (код выхода, application failure, здоровый snapshot) получаются в
// процессе с полностью управляемым каталогом запуска и без зависимости от пользовательского
// %LOCALAPPDATA%\AzurPilot\config.json.
// Режим --interactive-composition <путь> вызывает интерактивный startup с тестовым callback вместо UI
// и сообщает, была ли сессия открыта.
//
// Кроме самого исключения проба прогоняет его через production-маппер
// NativeBoundaryFailureMapper и печатает полученный стабильный application-level код отказа: тесты
// проверяют код, полученный из реального failure mode, а не из мока.
// Коды выхода: 0 — ожидаемый результат получен; 1 — результат не соответствует ожидаемому.

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args is ["--physical-owner-lifetime", string ownerPid, string ownerPath, string ownerStartTime, string recordPath])
{
    return await AzurPilot.NativeAbsenceProbe.PhysicalLifetimeProbe.RunAsync(ownerPid, ownerPath, ownerStartTime, recordPath)
        .ConfigureAwait(false);
}

if (args.Length == 5 && args[0] == "--agent-mutation-seam")
{
    return await AzurPilot.NativeAbsenceProbe.AgentMutationProbe.RunAsync(args[1], args[2], args[3], args[4])
        .ConfigureAwait(false);
}

if (args.Length == 4 && string.Equals(args[0], "--process-output", StringComparison.Ordinal))
{
    return await RunProcessOutputAsync(args[1], args[2], args[3]).ConfigureAwait(false);
}

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

        if (!exception.Message.Contains("Версия ABI", StringComparison.Ordinal)
            || exception.Message.Contains("azurpilot_native_query", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Проба: query отклонён не проверкой версии ABI: " + exception.Message);
            return 1;
        }

        try
        {
            using NativeFrame frame = AzurPilotNativeBridge.DecodePng([]);
            Console.Error.WriteLine("Проба: DecodePng неожиданно успешно завершился при несовместимой версии ABI.");
            return 1;
        }
        catch (NativeAbiMismatchException frameException)
        {
            if (!frameException.Message.Contains(AzurPilotNativeBridge.LibraryName, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                "Проба: диагностика API кадра с несовместимым ABI не называет библиотеку: "
                    + frameException.Message);
                return 1;
            }

            if (!frameException.Message.Contains("Версия ABI", StringComparison.Ordinal)
                || frameException.Message.Contains("azurpilot_native_frame_decode_png", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "Проба: вызов функции декодирования отклонён не проверкой версии ABI: "
                    + frameException.Message);
                return 1;
            }

            Console.WriteLine(
                "Проба: несовместимый ABI отклонён до вызова функции декодирования: "
                + frameException.Message);
        }
        catch (Exception frameException)
        {
            Console.Error.WriteLine(
                "Проба: функция декодирования вызвана до проверки несовместимого ABI: "
                + $"{frameException.GetType().Name}: {frameException.Message}");
            return 1;
        }

        Console.WriteLine($"Проба: получено ожидаемое исключение несовместимого ABI: {exception.Message}");
        return ReportMappedFailure(exception);
    }

    Console.Error.WriteLine("Проба: несовместимый ABI не привёл к NativeAbiMismatchException.");
    return 1;
}

if (args.Length == 1 && string.Equals(args[0], "--abi-v3-missing-release", StringComparison.Ordinal))
{
    return RunMissingReleaseProbe();
}

if (args.Length == 1 && string.Equals(args[0], "--abi-v3-missing-roi", StringComparison.Ordinal))
{
    return RunMissingRoiProbe();
}

if (args.Length == 1 && string.Equals(args[0], "--disposed-frame", StringComparison.Ordinal))
{
    return RunDisposedFrameProbe();
}

if (args.Length == 2 && string.Equals(args[0], "--application-composition", StringComparison.Ordinal))
{
    return await RunApplicationCompositionAsync(args[1]).ConfigureAwait(false);
}

if (args.Length == 2 && string.Equals(args[0], "--interactive-composition", StringComparison.Ordinal))
{
    return await RunInteractiveCompositionAsync(args[1]).ConfigureAwait(false);
}

if (args.Length != 0)
{
    Console.Error.WriteLine(
        "Проба: неизвестные аргументы; допустимы --abi-mismatch, --abi-v3-missing-release, "
        + "--abi-v3-missing-roi, --disposed-frame, --process-output <stdout> <stderr> <delay> "
        + "и --application-composition <путь>.");
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

// Проверяет, что DLL ABI v3 без обязательной функции освобождения отвергается до декодирования.
static int RunMissingReleaseProbe()
{
    try
    {
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng([]);
        Console.Error.WriteLine("Проба: DecodePng неожиданно успешно завершился без обязательного экспорта освобождения.");
        return 1;
    }
    catch (NativeAbiMismatchException exception)
    {
        if (!exception.Message.Contains(AzurPilotNativeBridge.LibraryName, StringComparison.Ordinal)
            || !exception.Message.Contains("azurpilot_native_frame_release", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Проба: диагностика отсутствующей функции освобождения не называет библиотеку и экспорт: "
                + exception.Message);
            return 1;
        }

        Console.WriteLine(
            "Проба: ABI v3 без обязательной функции освобождения отклонён до декодирования: "
            + exception.Message);
        return ReportMappedFailure(exception);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            "Проба: вызов декодирования дошёл до неожиданной ошибки: "
            + $"{exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

// Доказывает preflight обязательного ROI-экспорта до создания native frame.
static int RunMissingRoiProbe()
{
    try
    {
        using NativeFrame frame = AzurPilotNativeBridge.DecodePng([]);
        Console.Error.WriteLine("Проба: DecodePng неожиданно успешно завершился без обязательного ROI экспорта.");
        return 1;
    }
    catch (NativeAbiMismatchException exception)
    {
        if (!exception.Message.Contains(AzurPilotNativeBridge.LibraryName, StringComparison.Ordinal)
            || !exception.Message.Contains("azurpilot_native_frame_scan_region", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Проба: диагностика отсутствующего ROI экспорта не называет библиотеку и экспорт.");
            return 1;
        }

        Console.WriteLine("Проба: ABI v3 без обязательного ROI экспорта отклонён до декодирования: " + exception.Message);
        return ReportMappedFailure(exception);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Проба: ROI preflight завершился неожиданно: {exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

// Создаёт закрытый пустой кадр с помощью отражения: этот тестовый процесс не содержит native DLL.
static int RunDisposedFrameProbe()
{
    try
    {
        Type safeHandleType = typeof(NativeFrame).Assembly.GetType(
            "AzurPilot.Windows.NativeFrameSafeHandle",
            throwOnError: true)!;
        object safeHandle = Activator.CreateInstance(safeHandleType, nonPublic: true)
            ?? throw new InvalidOperationException("Не удалось создать пустой SafeHandle для пробы.");
        ConstructorInfo frameConstructor = typeof(NativeFrame).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [safeHandleType],
            modifiers: null)
            ?? throw new InvalidOperationException("Не найден внутренний конструктор NativeFrame.");

        using NativeFrame frame = (NativeFrame)frameConstructor.Invoke([safeHandle]);
        frame.Dispose();
        try
        {
            _ = frame.GetInfo();
            Console.Error.WriteLine("Проба: GetInfo после Dispose неожиданно завершился успешно.");
            return 1;
        }
        catch (ObjectDisposedException)
        {
            Console.WriteLine("Проба: GetInfo после Dispose отклонён до native вызова.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "Проба: GetInfo после Dispose завершился неожиданно: "
                + $"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }

        try
        {
            _ = frame.ScanRegion(new PixelRegion(0, 0, 1, 1));
            Console.Error.WriteLine("Проба: ScanRegion после Dispose неожиданно завершился успешно.");
            return 1;
        }
        catch (ObjectDisposedException)
        {
            Console.WriteLine("Проба: ScanRegion после Dispose отклонён до native вызова.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "Проба: ScanRegion после Dispose завершился неожиданно: "
                + $"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            "Проба: не удалось подготовить закрытый кадр: "
            + $"{exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

// Изолированный child process для побайтового доказательства общей process boundary.
static async Task<int> RunProcessOutputAsync(string outputCount, string errorCount, string delayValue)
{
    if (!int.TryParse(outputCount, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int stdoutBytes)
        || !int.TryParse(errorCount, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int stderrBytes)
        || !int.TryParse(delayValue, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int delayMilliseconds))
    {
        Console.Error.WriteLine("Проба: количества байтов и задержка должны быть неотрицательными Int32.");
        return 1;
    }

    Task writeError = Task.Run(async () =>
    {
        const int chunkSize = 4096;
        string chunk = new('e', chunkSize);
        for (int remaining = stderrBytes; remaining > 0;)
        {
            int count = Math.Min(remaining, chunkSize);
            await Console.Error.WriteAsync(chunk.AsMemory(0, count)).ConfigureAwait(false);
            remaining -= count;
        }

        await Console.Error.FlushAsync().ConfigureAwait(false);
    });

    using Stream output = Console.OpenStandardOutput();
    byte[] bytes = new byte[4096];
    for (int index = 0; index < bytes.Length; index++)
    {
        bytes[index] = (byte)(index % 256);
    }

    for (int remaining = stdoutBytes; remaining > 0;)
    {
        int count = Math.Min(remaining, bytes.Length);
        await output.WriteAsync(bytes.AsMemory(0, count)).ConfigureAwait(false);
        remaining -= count;
    }

    await output.FlushAsync().ConfigureAwait(false);
    await writeError.ConfigureAwait(false);
    await Task.Delay(delayMilliseconds).ConfigureAwait(false);
    return 0;
}

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

// Выполняет интерактивный startup без UI: callback отмечает, была ли открыта сессия.
// Это позволяет проверить production startup gate при native failure в отдельном процессе,
// где native runtime действительно отсутствует.
static async Task<int> RunInteractiveCompositionAsync(string configurationPath)
{
    bool sessionCallbackInvoked = false;
    AzurPilotInteractiveRunResult result;
    try
    {
        result = await AzurPilotHost.RunInteractiveAsync(
            (_, _, _, _, _, _) =>
            {
                sessionCallbackInvoked = true;
                return Task.CompletedTask;
            },
            configurationPath).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"Проба: interactive composition завершилась исключением {exception.GetType().Name}: {exception.Message}");
        return 1;
    }

    Console.WriteLine($"Проба: application exit code={result.Startup.ExitCode}");
    if (result.Startup.FailureInfo is not null)
    {
        Console.WriteLine($"Проба: application failure code={result.Startup.FailureInfo.Code}");
    }

    Console.WriteLine($"Проба: interactive session started={(result.InteractiveSessionStarted ? "true" : "false")}");
    Console.WriteLine($"Проба: interactive callback invoked={(sessionCallbackInvoked ? "true" : "false")}");
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
