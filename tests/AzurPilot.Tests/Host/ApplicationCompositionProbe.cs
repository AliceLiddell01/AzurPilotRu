using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using AzurPilot.Tests.Failures;
using Xunit;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Запуск тестовой пробы, которая выполняет реальный application startup через composition root с явно
/// переданным путём конфигурации.
/// </summary>
/// <remarks>
/// <para>
/// Продукт аргументов запуска не разбирает, поэтому application-level доказательства из полностью
/// управляемого каталога можно получить только выполнив composition кодом. Это делает проба: она
/// ссылается на <c>AzurPilot.App</c> и вызывает startup с временным путём конфигурации, оставаясь
/// тестовым инструментом, а не boundary приложения.
/// </para>
/// <para>
/// Каталог прогона — самодостаточный output пробы (managed зависимости приложения и native runtime),
/// поэтому сценарии различаются только составом каталога: без native библиотеки, с fixture
/// несовместимого ABI и с рабочим native runtime.
/// </para>
/// <para>
/// Пользовательский <c>%LOCALAPPDATA%\AzurPilot\config.json</c> не читается: путь конфигурации всегда
/// задаёт тест, и файл пользователя не подменяется.
/// </para>
/// </remarks>
internal sealed class ApplicationCompositionProbe : IDisposable
{
    private const string StagedFolderName = "native-absence-probe";
    private const string ProbeAssemblyFileName = "AzurPilot.NativeAbsenceProbe.dll";
    private const string NativeLibraryFileName = "AzurPilot.Native.dll";
    private const string OpenCvRuntimePattern = "opencv_world*.dll";
    private const string SymbolsExtension = ".pdb";
    private const string ApplicationCompositionOption = "--application-composition";
    private const string ApplicationExitCodePrefix = "Проба: application exit code=";
    private const int ProcessTimeoutMilliseconds = 60_000;

    private readonly string _root;
    private readonly bool _ownsRoot;

    private ApplicationCompositionProbe(string root, bool ownsRoot)
    {
        _root = root;
        _ownsRoot = ownsRoot;
    }

    /// <summary>Каталог застейдженного полного output пробы внутри тестового выхода.</summary>
    private static string StagedDirectory => Path.Combine(AppContext.BaseDirectory, StagedFolderName);

    /// <summary>Открывает застейдженный output пробы с рабочим native runtime.</summary>
    /// <returns>Запуск пробы из каталога с native библиотекой.</returns>
    internal static ApplicationCompositionProbe CreateStaged()
    {
        string assembly = Path.Combine(StagedDirectory, ProbeAssemblyFileName);
        Assert.True(File.Exists(assembly), $"Не застейджен полный output пробы: {assembly}.");

        return new ApplicationCompositionProbe(StagedDirectory, ownsRoot: false);
    }

    /// <summary>Создаёт изолированную копию output пробы без native runtime.</summary>
    /// <returns>Запуск пробы из каталога, где native библиотеки нет.</returns>
    internal static ApplicationCompositionProbe CreateWithoutNativeRuntime()
        => new(CreateIsolatedCopy(), ownsRoot: true);

    /// <summary>Создаёт изолированную копию output пробы с fixture несовместимого ABI.</summary>
    /// <returns>Запуск пробы из каталога, где вместо native библиотеки лежит несовместимая DLL.</returns>
    internal static ApplicationCompositionProbe CreateWithIncompatibleAbiFixture()
    {
        string root = CreateIsolatedCopy();
        string fixture = Path.Combine(AppContext.BaseDirectory, "abi-mismatch", NativeLibraryFileName);
        Assert.True(File.Exists(fixture), $"Не собрана негативная ABI fixture: {fixture}.");

        File.Copy(fixture, Path.Combine(root, NativeLibraryFileName), overwrite: true);
        return new ApplicationCompositionProbe(root, ownsRoot: true);
    }

    /// <summary>Выполняет application startup с явно переданным путём конфигурации.</summary>
    /// <param name="configurationPath">Абсолютный путь временного файла конфигурации.</param>
    /// <returns>Код выхода пробы, код выхода приложения и прочитанные structured записи.</returns>
    internal ApplicationProbeRun Run(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.Combine(_root, ProbeAssemblyFileName));
        startInfo.ArgumentList.Add(ApplicationCompositionOption);
        startInfo.ArgumentList.Add(configurationPath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить процесс-пробу.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Процесс-проба application composition не завершился за отведённое время.");
        }

        string standardOutput = output.GetAwaiter().GetResult();
        string standardError = error.GetAwaiter().GetResult();
        return new ApplicationProbeRun(
            process.ExitCode,
            ReadApplicationExitCode(standardOutput),
            standardOutput,
            standardError,
            StructuredLogRecord.ReadFrom(standardError));
    }

    /// <summary>Удаляет временный каталог прогона, если он принадлежит тесту.</summary>
    public void Dispose()
    {
        if (!_ownsRoot)
        {
            return;
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Каталог мог остаться занятым завершающимся процессом: это не влияет на результат теста.
        }
        catch (UnauthorizedAccessException)
        {
            // Каталог мог остаться занятым завершающимся процессом: это не влияет на результат теста.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Читает код выхода приложения, сообщённый пробой.</summary>
    /// <param name="probeOutput">Содержимое stdout процесса-пробы.</param>
    /// <returns>Код выхода application host.</returns>
    /// <exception cref="InvalidOperationException">Проба не сообщила код выхода приложения.</exception>
    private static int ReadApplicationExitCode(string probeOutput)
    {
        foreach (string line in probeOutput.Split('\n'))
        {
            string candidate = line.Trim();
            if (candidate.StartsWith(ApplicationExitCodePrefix, StringComparison.Ordinal)
                && int.TryParse(
                    candidate[ApplicationExitCodePrefix.Length..],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int exitCode))
            {
                return exitCode;
            }
        }

        throw new InvalidOperationException(
            $"Проба не сообщила код выхода приложения: в выводе нет строки «{ApplicationExitCodePrefix}». "
            + $"Вывод: {probeOutput}");
    }

    private static string CreateIsolatedCopy()
    {
        _ = CreateStaged();

        string root = Path.Combine(
            Path.GetTempPath(),
            "AzurPilot.Tests.ApplicationComposition",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(root);

        foreach (string directory in Directory.EnumerateDirectories(StagedDirectory, "*", SearchOption.AllDirectories))
        {
            _ = Directory.CreateDirectory(Path.Combine(root, Path.GetRelativePath(StagedDirectory, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(StagedDirectory, "*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(file);
            if (IsNativeRuntimeFile(fileName))
            {
                continue;
            }

            File.Copy(file, Path.Combine(root, Path.GetRelativePath(StagedDirectory, file)));
        }

        return root;
    }

    private static bool IsNativeRuntimeFile(string fileName)
        => string.Equals(fileName, NativeLibraryFileName, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(SymbolsExtension, StringComparison.OrdinalIgnoreCase)
            || FileSystemName.MatchesSimpleExpression(OpenCvRuntimePattern, fileName, ignoreCase: true);
}

/// <summary>Результат прогона пробы, выполнившей application startup.</summary>
/// <param name="ProbeExitCode">Код выхода процесса-пробы: 0 — прогон корректен.</param>
/// <param name="ApplicationExitCode">Код выхода application host, сообщённый пробой.</param>
/// <param name="StandardOutput">Содержимое stdout процесса-пробы.</param>
/// <param name="StandardError">Содержимое stderr процесса-пробы.</param>
/// <param name="Logs">Structured записи, прочитанные из stderr процесса-пробы.</param>
internal sealed record ApplicationProbeRun(
    int ProbeExitCode,
    int ApplicationExitCode,
    string StandardOutput,
    string StandardError,
    IReadOnlyList<StructuredLogRecord> Logs)
{
    /// <summary>Читает стабильный application-level код отказа, сообщённый пробой.</summary>
    /// <returns>Код отказа из фиксированного набора <c>ApplicationFailure</c>.</returns>
    internal string ReadFailureCode()
        => NativeBoundaryProbeRunner.ReadFailureCode(StandardOutput + StandardError);
}
