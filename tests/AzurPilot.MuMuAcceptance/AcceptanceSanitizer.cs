namespace AzurPilot.MuMuAcceptance;

/// <summary>
/// Санитайзер отчёта приёмки: machine-specific значения заменяются логическими плейсхолдерами.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт приёмки не должен раскрывать конкретную машину. Значения, которые становятся известны только в
/// runtime (каталог установки, путь control surface, профиль пользователя, рабочий каталог), попадают в
/// этот санитайзер, а весь текст отчёта проходит через <see cref="Sanitize"/>.
/// </para>
/// <para>
/// Санитайзер — вторая линия защиты, а не первая: отчёт не должен содержать эти значения и до замены.
/// Поэтому после сборки текста выполняется проверка <see cref="ContainsProtectedValue"/>, и её провал
/// означает, что приёмка не доказана, даже если все шаги матрицы прошли.
/// </para>
/// </remarks>
internal sealed class AcceptanceSanitizer
{
    /// <summary>Плейсхолдер каталога установки MuMuPlayer.</summary>
    internal const string InstallRootPlaceholder = "<install-root>";

    /// <summary>Плейсхолдер пути control surface установки.</summary>
    internal const string ControlSurfacePlaceholder = "<control-surface>";

    /// <summary>Плейсхолдер домашнего каталога пользователя.</summary>
    internal const string UserProfilePlaceholder = "<user-profile>";

    /// <summary>Плейсхолдер рабочего каталога прогона.</summary>
    internal const string WorkingDirectoryPlaceholder = "<working-directory>";

    private readonly List<KeyValuePair<string, string>> _protectedValues = [];

    /// <summary>Регистрирует machine-specific значение, которое не должно попасть в отчёт.</summary>
    /// <param name="value">Значение, полученное в runtime.</param>
    /// <param name="placeholder">Логический плейсхолдер для этого значения.</param>
    internal void Protect(string? value, string placeholder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placeholder);

        if (!LooksLikeMachinePath(value))
        {
            return;
        }

        foreach (KeyValuePair<string, string> existing in _protectedValues)
        {
            if (string.Equals(existing.Key, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        _protectedValues.Add(new KeyValuePair<string, string>(value!, placeholder));
    }

    /// <summary>Заменяет защищённые значения их плейсхолдерами.</summary>
    /// <param name="text">Текст отчёта.</param>
    /// <returns>Текст без machine-specific значений.</returns>
    internal string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string sanitized = text;
        foreach (KeyValuePair<string, string> protectedValue in _protectedValues)
        {
            sanitized = sanitized.Replace(
                protectedValue.Key,
                protectedValue.Value,
                StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    /// <summary>Проверяет, содержит ли текст хотя бы одно защищённое значение.</summary>
    /// <param name="text">Текст отчёта.</param>
    /// <returns><see langword="true"/>, если machine-specific значение попало в текст.</returns>
    internal bool ContainsProtectedValue(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (KeyValuePair<string, string> protectedValue in _protectedValues)
        {
            if (text.Contains(protectedValue.Key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Проверяет, похоже ли значение на machine-specific путь.</summary>
    /// <remarks>
    /// Защищаются только пути: короткие значения вроде версии или identity экземпляра заменять нельзя —
    /// это испортило бы сам отчёт, не повысив его приватность.
    /// </remarks>
    /// <param name="value">Проверяемое значение.</param>
    /// <returns><see langword="true"/>, если значение является путём.</returns>
    private static bool LooksLikeMachinePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= 3)
        {
            return false;
        }

        return Path.IsPathFullyQualified(value)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('/', StringComparison.Ordinal);
    }
}
