using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Legacy-контракт схемы v2: документ с <c>schemaVersion = 2</c>, секциями диагностики и MuMu.
/// </summary>
/// <remarks>
/// <para>
/// Число 2 — версия этой legacy-схемы, поэтому оно принадлежит этому типу; версия, которую понимает
/// текущая сборка, принадлежит <see cref="AzurPilotConfiguration.CurrentSchemaVersion"/>. Второй копии
/// ни одного из этих чисел не заводится.
/// </para>
/// <para>
/// До появления схемы v3 этот набор свойств был эффективной схемой; теперь он допускается только как
/// legacy-вход и нормализуется к v3 в памяти. Контракт не сериализуется обратно: файл v2 не
/// переписывается, не создаётся и не ремонтируется.
/// </para>
/// <para>
/// Строгость к v2 не ослабляется: неизвестное свойство на любом уровне, включая секции будущих
/// capability, неверный регистр имени или значения, duplicate property и нарушение required-контракта
/// отвергаются так же, как раньше.
/// </para>
/// </remarks>
internal sealed record AzurPilotConfigurationV2
{
    /// <summary>Версия legacy-схемы, которой соответствует этот контракт.</summary>
    internal const int LegacySchemaVersion = 2;

    /// <summary>Версия схемы конфигурации, объявленная в legacy-документе.</summary>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>Настройки диагностики приложения.</summary>
    [JsonPropertyName("diagnostics")]
    public required DiagnosticsConfiguration Diagnostics { get; init; }

    /// <summary>Настройки MuMu.</summary>
    [JsonPropertyName("mumu")]
    public required MuMuConfiguration MuMu { get; init; }

    /// <summary>Нормализует legacy-документ v2 к эффективной схеме v3 в памяти.</summary>
    /// <returns>
    /// Конфигурация схемы v3: прочитанные диагностика и выбор экземпляра MuMu, встроенные значения
    /// секций, которых в схеме v2 не было.
    /// </returns>
    /// <remarks>
    /// Нормализация выполняется только в памяти и не касается файла. Значения новых секций
    /// (<c>ui.mode</c> и <c>logging.viewer.enabled</c>) берутся у единственного владельца встроенных
    /// defaults, поэтому второй копии этих значений не появляется.
    /// </remarks>
    internal AzurPilotConfiguration ToCurrentSchema()
        => AzurPilotConfigurationDefaults.Create() with { Diagnostics = Diagnostics, MuMu = MuMu };
}
