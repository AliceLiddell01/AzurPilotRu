using System.ComponentModel;
using System.Diagnostics;

namespace AzurPilot.App.LogViewer;

/// <summary>
/// Точка запуска отдельного окна терминала для viewer-а.
/// </summary>
/// <remarks>
/// Интерфейс существует ровно для того, чтобы отказ среды («отдельное окно недоступно») проверялся
/// детерминированно: тест подменяет эту границу, а не зависит от установленного Windows Terminal.
/// </remarks>
internal interface ILogViewerWindowHost
{
    /// <summary>Отдельное окно терминала доступно в этой среде.</summary>
    bool IsAvailable { get; }

    /// <summary>Открывает отдельное окно, в котором запускается указанная команда.</summary>
    /// <param name="executablePath">Полный путь исполняемого файла приложения.</param>
    /// <param name="arguments">Аргументы запуска viewer-а.</param>
    /// <returns><see langword="true"/>, если окно удалось открыть.</returns>
    bool TryOpen(string executablePath, IReadOnlyList<string> arguments);
}

/// <summary>Отдельное окно Windows Terminal как хост viewer-а.</summary>
/// <remarks>
/// <para>
/// Аргументы передаются списком <see cref="ProcessStartInfo.ArgumentList"/>, поэтому путь и значения
/// экранирует хост процесса. Склейка командной строки вручную здесь не используется: она позволила бы
/// имени канала или пути выйти за пределы одного аргумента.
/// </para>
/// <para>
/// <c>wt.exe</c> только открывает окно: сам viewer — отдельный процесс того же приложения. Поэтому
/// отслеживать его жизненный цикл через возвращённый <see cref="Process"/> нельзя; завершение канала
/// закрывает viewer, а вместе с ним и окно.
/// </para>
/// </remarks>
internal sealed class WindowsTerminalWindowHost : ILogViewerWindowHost
{
    private const string TerminalExecutableName = "wt.exe";

    /// <summary>Единственный экземпляр границы: она не хранит состояния.</summary>
    internal static WindowsTerminalWindowHost Instance { get; } = new();

    /// <inheritdoc/>
    public bool IsAvailable => ResolveTerminalPath() is not null;

    /// <inheritdoc/>
    public bool TryOpen(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (ResolveTerminalPath() is not string terminal)
        {
            return false;
        }

        ProcessStartInfo startInfo = new(terminal)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--window");
        startInfo.ArgumentList.Add("new");
        startInfo.ArgumentList.Add(executablePath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            // Отказ запуска окна — ожидаемый результат этой границы, а не ошибка программирования.
            return false;
        }
    }

    /// <summary>Находит <c>wt.exe</c> в <c>PATH</c> процесса.</summary>
    /// <returns>Полный путь либо <see langword="null"/>, если терминал недоступен.</returns>
    private static string? ResolveTerminalPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), TerminalExecutableName);
            }
            catch (ArgumentException)
            {
                // Некорректный элемент PATH — не повод отказать в поиске по остальным каталогам.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
