namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Control surface MuMu: исполняемый файл, через который провайдер принимает команды.
/// </summary>
/// <remarks>
/// <para>
/// Значение описывает только точку входа. Точная форма команд принадлежит
/// <see cref="MuMuManagerCommandBuilder"/>, разбор ответов — <see cref="MuMuManagerResponseParser"/>;
/// здесь они не повторяются.
/// </para>
/// <para>
/// Абсолютный путь — runtime data конкретной установки: он не является константой проекта и не
/// появляется в исходниках, тестах и документации.
/// </para>
/// </remarks>
public sealed record MuMuControlSurface
{
    /// <summary>Абсолютный путь к исполняемому файлу control surface.</summary>
    public required string ExecutablePath { get; init; }
}
