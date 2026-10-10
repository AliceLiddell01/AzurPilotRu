namespace AzurPilot.Core;

/// <summary>
/// Сведения о native boundary, полученные от native библиотеки через C ABI.
/// </summary>
/// <remarks>
/// Тип намеренно не зависит от Windows и от способа загрузки native библиотеки: это форма данных,
/// которой обмениваются managed и native стороны. Имена capability совпадают с именами модулей
/// OpenCV, доступных в сборке native библиотеки.
/// </remarks>
/// <param name="AbiVersion">Версия ABI native библиотеки.</param>
/// <param name="OpencvVersion">Версия OpenCV, с которой собрана native библиотека.</param>
/// <param name="Capabilities">Имена capability, реально доступных в сборке native библиотеки.</param>
/// <param name="BuildFlags">Биты признаков сборки native библиотеки (факты исполнения, а не константы).</param>
/// <param name="BuildInfo">Строка сведений о сборке native библиотеки в формате key=value.</param>
public sealed record NativeBoundaryInfo(
    uint AbiVersion,
    Version OpencvVersion,
    IReadOnlyCollection<string> Capabilities,
    uint BuildFlags,
    string BuildInfo);

/// <summary>
/// Результат проверки совместимости native boundary с ожидаемым контрактом.
/// </summary>
/// <param name="IsCompatible">Признак полной совместимости.</param>
/// <param name="Reason">Причина несовместимости; для совместимого boundary — текст подтверждения.</param>
public sealed record NativeBoundaryCompatibility(bool IsCompatible, string Reason);

/// <summary>
/// Ожидаемый контракт native boundary: версия ABI и обязательные capability.
/// </summary>
/// <remarks>
/// Нормативный владелец формы C ABI и битовых значений — <c>native/include/azurpilot_native_abi.h</c>.
/// Этот тип повторяет только те значения, которые managed стороне нужно сверить, чтобы обнаружить
/// несовместимую native библиотеку до использования её данных.
/// </remarks>
public sealed class NativeBoundaryContract
{
    /// <summary>Ожидаемая версия ABI native библиотеки.</summary>
    public const uint ExpectedAbiVersion = 2;

    /// <summary>Строковое представление ожидаемой версии ABI — значение из заголовка ABI.</summary>
    public const string ExpectedAbiVersionString = "2";

    /// <summary>Канонический экземпляр контракта фундамента.</summary>
    public static NativeBoundaryContract Canonical { get; } = new();

    /// <summary>Создаёт контракт с явно заданными ожиданиями.</summary>
    /// <param name="expectedAbiVersion">Ожидаемая версия ABI.</param>
    /// <param name="requiredCapabilities">Имена capability, обязательных в native библиотеке.</param>
    public NativeBoundaryContract(uint expectedAbiVersion, IEnumerable<string> requiredCapabilities)
    {
        ArgumentNullException.ThrowIfNull(requiredCapabilities);

        RequiredAbiVersion = expectedAbiVersion;
        RequiredCapabilities = new HashSet<string>(requiredCapabilities, StringComparer.Ordinal);
    }

    private NativeBoundaryContract()
        : this(ExpectedAbiVersion, ["core", "imgcodecs"])
    {
    }

    /// <summary>Версия ABI, ожидаемая этим контрактом.</summary>
    public uint RequiredAbiVersion { get; }

    /// <summary>Имена capability, которые обязана подтвердить native библиотека.</summary>
    public IReadOnlySet<string> RequiredCapabilities { get; }

    /// <summary>
    /// Проверяет, что сведения от native библиотеки удовлетворяют контракту.
    /// </summary>
    /// <param name="info">Сведения, полученные от native библиотеки.</param>
    /// <returns>Результат проверки с причиной; исключений не бросает.</returns>
    public NativeBoundaryCompatibility Check(NativeBoundaryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (info.AbiVersion != RequiredAbiVersion)
        {
            return new NativeBoundaryCompatibility(
                false,
                $"Версия ABI native библиотеки: {info.AbiVersion}; ожидается: {RequiredAbiVersion}. "
                + "Несовместимую версию границы использовать нельзя.");
        }

        List<string> missing = [];
        foreach (string capability in RequiredCapabilities)
        {
            if (!info.Capabilities.Contains(capability))
            {
                missing.Add(capability);
            }
        }

        if (missing.Count > 0)
        {
            missing.Sort(StringComparer.Ordinal);
            return new NativeBoundaryCompatibility(
                false,
                $"Native библиотека не подтвердила capability: {string.Join(", ", missing)}.");
        }

        return new NativeBoundaryCompatibility(
            true,
            $"Граница совместима: ABI {info.AbiVersion}, OpenCV {info.OpencvVersion}.");
    }
}
