using System.Diagnostics;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests;

/// <summary>
/// Негативные проверки interop boundary: доказательство, что положительные проверки не проходят на
/// моке и что production-код interop честно сообщает об отсутствии native библиотеки.
/// </summary>
/// <remarks>
/// <para>
/// Проверки выполняют production-код interop в отдельном процессе, из каталога которого native
/// библиотека заведомо убрана. В процессе теста загруженный модуль остаётся доступным до завершения
/// процесса, поэтому «библиотеки нет» там воспроизвести нельзя: проверка должна идти там, где файла
/// действительно нет.
/// </para>
/// <para>
/// Тест, который в такой ситуации проходит или пропускается, доказательством границы не является.
/// </para>
/// </remarks>
[Collection(InteropCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NativeInteropNegativeTests
{
    private const string NativeLibraryFileName = AzurPilotNativeBridge.LibraryName + ".dll";
    private const string ProbeAssemblyFileName = "AzurPilot.NativeAbsenceProbe.dll";
    private const string ProbeDirectoryName = "probe";
    private const string ProbeExpectedMarker = "Проба: получено ожидаемое исключение";

    [Fact(DisplayName = "Отсутствующая native библиотека обязана выбросить исключение, а не пропустить проверку")]
    public void MissingNativeLibraryThrowsInsteadOfSkipping()
    {
        ProbeResult result = RunProbe(brokenLibrary: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(ProbeExpectedMarker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(AzurPilotNativeBridge.LibraryName, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Повреждённая native библиотека обязана выбросить исключение с диагностикой")]
    public void BrokenNativeLibraryThrowsWithDiagnostics()
    {
        ProbeResult result = RunProbe(brokenLibrary: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(ProbeExpectedMarker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(AzurPilotNativeBridge.LibraryName, result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Запускает пробу в каталоге без native библиотеки и возвращает результат её работы.
    /// </summary>
    /// <param name="brokenLibrary">
    /// Если <see langword="true"/>, в каталог пробы кладётся файл с именем библиотеки, который не
    /// является корректной библиотекой x64: проверяется ветка BadImageFormatException.
    /// </param>
    /// <returns>Код выхода и вывод пробы.</returns>
    private static ProbeResult RunProbe(bool brokenLibrary)
    {
        string probeDirectory = Path.Combine(Path.GetTempPath(), "azurpilot-native-absence-probe", ProbeDirectoryName);
        if (Directory.Exists(probeDirectory))
        {
            Directory.Delete(probeDirectory, recursive: true);
        }

        _ = Directory.CreateDirectory(probeDirectory);
        CopyProbeFiles(probeDirectory);

        if (brokenLibrary)
        {
            File.WriteAllText(Path.Combine(probeDirectory, NativeLibraryFileName), "не библиотека");
        }

        ProcessStartInfo startInfo = new("dotnet", $"exec \"{Path.Combine(probeDirectory, ProbeAssemblyFileName)}\"")
        {
            WorkingDirectory = probeDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить процесс-пробу.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProbeResult(process.ExitCode, output + error);
    }

    private static void CopyProbeFiles(string probeDirectory)
    {
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith("AzurPilot.NativeAbsenceProbe", StringComparison.Ordinal)
                || string.Equals(fileName, "AzurPilot.Windows.dll", StringComparison.Ordinal)
                || string.Equals(fileName, "AzurPilot.Windows.pdb", StringComparison.Ordinal)
                || string.Equals(fileName, "AzurPilot.Core.dll", StringComparison.Ordinal)
                || string.Equals(fileName, "AzurPilot.Core.pdb", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(probeDirectory, fileName), overwrite: true);
            }
        }
    }

    /// <summary>Результат работы процесса-пробы.</summary>
    /// <param name="ExitCode">Код выхода процесса.</param>
    /// <param name="StandardOutput">Объединённый вывод процесса.</param>
    private sealed record ProbeResult(int ExitCode, string StandardOutput);
}
