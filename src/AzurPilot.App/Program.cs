using System.Text;
using AzurPilot.Core;
using AzurPilot.Windows;

// Точка входа фундамента: проверяет native boundary и печатает полученные сведения.
// Интерактивный режим, разбор аргументов командной строки и продуктовые capability здесь
// намеренно отсутствуют — это фундамент, а не приложение.

Console.OutputEncoding = Encoding.UTF8;

try
{
    NativeBoundaryContract contract = NativeBoundaryContract.Canonical;
    NativeBoundaryInfo info = AzurPilotNativeBridge.Query();
    NativeBoundaryCompatibility compatibility = contract.Check(info);

    Console.WriteLine("AzurPilot: сведения о native boundary");
    Console.WriteLine($"  Имя native библиотеки: {AzurPilotNativeBridge.LibraryName}");
    Console.WriteLine($"  Версия ABI: {info.AbiVersion}");
    Console.WriteLine($"  Версия OpenCV: {info.OpencvVersion} ({info.OpencvVersion.Major}.{info.OpencvVersion.Minor}.{info.OpencvVersion.Build})");
    Console.WriteLine($"  Код OpenCV исполнен при заполнении сведений: {DescribeOpencvExecuted(info.BuildFlags)}");
    Console.WriteLine($"  Доступные capability: {string.Join(", ", info.Capabilities)}");
    Console.WriteLine($"  Совместимость границы: {(compatibility.IsCompatible ? "совместима" : "несовместима")} — {compatibility.Reason}");
    Console.WriteLine($"  Сведения о сборке: {info.BuildInfo}");

    return compatibility.IsCompatible ? 0 : 1;
}
catch (AzurPilotNativeBoundaryException exception)
{
    Console.Error.WriteLine($"Ошибка native boundary: {exception.Message}");
    return 1;
}
catch (DllNotFoundException exception)
{
    Console.Error.WriteLine(
        $"Native библиотека «{AzurPilotNativeBridge.LibraryName}» не загружена: {exception.Message} "
        + "Соберите native часть и положите её рядом с managed сборкой.");
    return 1;
}
catch (BadImageFormatException exception)
{
    Console.Error.WriteLine(
        $"Native библиотека «{AzurPilotNativeBridge.LibraryName}» несовместима с процессом: {exception.Message} "
        + "Managed фундамент требует native библиотеку x64.");
    return 1;
}

static string DescribeOpencvExecuted(uint buildFlags)
{
    return (buildFlags & AzurPilotNativeBridge.BuildFlagOpencvExecuted) != 0
        ? "да"
        : "нет (сведения о сборке не доказывают исполнение OpenCV)";
}
