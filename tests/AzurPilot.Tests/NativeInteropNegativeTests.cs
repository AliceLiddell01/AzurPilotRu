using System.Diagnostics;
using System.Text;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests;

/// <summary>
/// Негативные проверки interop boundary: доказательство, что положительные проверки не проходят на
/// моке и что production-код interop честно сообщает об отсутствии DLL и несовместимом ABI.
/// </summary>
/// <remarks>
/// <para>
/// Проверки выполняют production-код interop в отдельных процессах. Каждый получает собственный
/// каталог с заданной негативной fixture. В процессе положительного теста загруженный модуль
/// остаётся доступным до завершения процесса, поэтому негативную загрузку там воспроизвести нельзя.
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
    private const string ProbeExpectedMarker = "Проба: получено ожидаемое исключение";

    [Fact(DisplayName = "Отсутствующая native библиотека обязана выбросить исключение, а не пропустить проверку")]
    public void MissingNativeLibraryThrowsInsteadOfSkipping()
    {
        ProbeResult result = RunProbe(ProbeMode.MissingLibrary);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(ProbeExpectedMarker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(AzurPilotNativeBridge.LibraryName, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Повреждённая native библиотека обязана выбросить исключение с диагностикой")]
    public void BrokenNativeLibraryThrowsWithDiagnostics()
    {
        ProbeResult result = RunProbe(ProbeMode.BrokenLibrary);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(ProbeExpectedMarker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(AzurPilotNativeBridge.LibraryName, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Несовместимый ABI отвергается до вызова native query")]
    public void IncompatibleNativeAbiThrowsBeforeQuery()
    {
        ProbeResult result = RunProbe(ProbeMode.AbiMismatch);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Проба: получено ожидаемое исключение несовместимого ABI", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(AzurPilotNativeBridge.LibraryName, result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>Запускает production interop в отдельном процессе с заданной негативной fixture.</summary>
    /// <param name="mode">Отсутствующая DLL, повреждённая DLL или DLL с несовместимым ABI.</param>
    /// <returns>Код выхода и вывод пробы.</returns>
    private static ProbeResult RunProbe(ProbeMode mode)
    {
        string probeDirectory = Path.Combine(Path.GetTempPath(), "azurpilot-native-boundary-probe", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(probeDirectory);
        try
        {
            CopyProbeFiles(probeDirectory);
            if (mode == ProbeMode.BrokenLibrary)
            {
                File.WriteAllText(Path.Combine(probeDirectory, NativeLibraryFileName), "не библиотека");
            }
            else if (mode == ProbeMode.AbiMismatch)
            {
                string fixture = Path.Combine(AppContext.BaseDirectory, "abi-mismatch", NativeLibraryFileName);
                Assert.True(File.Exists(fixture), $"Не собрана негативная ABI fixture: {fixture}");
                File.Copy(fixture, Path.Combine(probeDirectory, NativeLibraryFileName));
            }

            ProcessStartInfo startInfo = new("dotnet")
            {
                WorkingDirectory = probeDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(Path.Combine(probeDirectory, ProbeAssemblyFileName));
            if (mode == ProbeMode.AbiMismatch)
            {
                startInfo.ArgumentList.Add("--abi-mismatch");
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить процесс-пробу.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException("Процесс-проба native boundary не завершился за 30 секунд.");
            }

            return new ProbeResult(process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(probeDirectory, recursive: true);
        }
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

    private enum ProbeMode
    {
        MissingLibrary,
        BrokenLibrary,
        AbiMismatch,
    }
}
