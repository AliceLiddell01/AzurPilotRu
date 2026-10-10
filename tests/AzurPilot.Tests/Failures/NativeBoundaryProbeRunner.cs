using System.Diagnostics;
using System.Text;
using AzurPilot.Core.Failures;
using AzurPilot.Windows;
using Xunit;

namespace AzurPilot.Tests.Failures;

/// <summary>
/// Запускает процесс-пробу native boundary с заданной негативной fixture и читает стабильный
/// application-level код отказа, который проба получила через production-маппер.
/// </summary>
/// <remarks>
/// <para>
/// Проба — тестовый инструмент, а не boundary приложения: она исполняет production-код interop
/// (<c>AzurPilotNativeBridge</c>) в отдельном процессе из каталога, где native библиотеки заведомо
/// нет или подложена fixture с несовместимым ABI. В процессе положительного теста загруженный модуль
/// остаётся доступным до завершения процесса, поэтому негативную загрузку там воспроизвести нельзя.
/// </para>
/// <para>
/// Мок здесь не используется: проба сообщает код отказа, полученный из реально выброшенного
/// production-исключения.
/// </para>
/// </remarks>
internal static class NativeBoundaryProbeRunner
{
    /// <summary>Строка вывода пробы с полученным application-level кодом отказа.</summary>
    internal const string FailureCodePrefix = "Проба: application failure code=";

    private const string NativeLibraryFileName = AzurPilotNativeBridge.LibraryName + ".dll";
    private const string ProbeAssemblyFileName = "AzurPilot.NativeAbsenceProbe.dll";

    /// <summary>Запускает пробу в отдельном процессе с заданной негативной fixture.</summary>
    /// <param name="mode">Отсутствующая DLL, повреждённая DLL или DLL с несовместимым ABI.</param>
    /// <returns>Код выхода, объединённый вывод пробы и прочитанный код отказа.</returns>
    internal static ProbeRun Run(ProbeMode mode)
    {
        string probeDirectory = Path.Combine(
            Path.GetTempPath(),
            "azurpilot-native-boundary-probe",
            Guid.NewGuid().ToString("N"));
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

            string combined = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            return new ProbeRun(process.ExitCode, combined, ReadFailureCode(combined));
        }
        finally
        {
            Directory.Delete(probeDirectory, recursive: true);
        }
    }

    /// <summary>Читает стабильный код отказа, сообщённый пробой.</summary>
    /// <param name="probeOutput">Объединённый вывод процесса-пробы.</param>
    /// <returns>Код отказа из фиксированного набора <see cref="ApplicationFailure"/>.</returns>
    internal static string ReadFailureCode(string probeOutput)
    {
        foreach (string line in probeOutput.Split('\n'))
        {
            string candidate = line.Trim();
            if (!candidate.StartsWith(FailureCodePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string code = candidate[FailureCodePrefix.Length..].Trim();
            Assert.True(IsKnownCode(code), $"Проба сообщила неизвестный код отказа: «{code}».");
            return code;
        }

        throw new InvalidOperationException(
            $"Проба не сообщила код отказа: в выводе нет строки «{FailureCodePrefix}». Вывод: {probeOutput}");
    }

    private static bool IsKnownCode(string code)
        => code is ApplicationFailure.ConfigurationInvalid
            or ApplicationFailure.ConfigurationSchemaUnsupported
            or ApplicationFailure.NativeUnavailable
            or ApplicationFailure.NativeIncompatible
            or ApplicationFailure.NativeFrameInvalid
            or ApplicationFailure.OperationCancelled
            or ApplicationFailure.InternalError;

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
    /// <param name="FailureCode">Стабильный application-level код отказа, сообщённый пробой.</param>
    internal sealed record ProbeRun(int ExitCode, string StandardOutput, string FailureCode);

    /// <summary>Негативная fixture, с которой запускается проба.</summary>
    internal enum ProbeMode
    {
        /// <summary>Каталог пробы без native библиотеки.</summary>
        MissingLibrary,

        /// <summary>Вместо native библиотеки подложен файл, не являющийся библиотекой.</summary>
        BrokenLibrary,

        /// <summary>Подложена DLL с несовместимой версией ABI.</summary>
        AbiMismatch,
    }
}
